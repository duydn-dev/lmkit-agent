<template>
  <Transition name="canvas-slide">
    <aside
      v-show="visible"
      aria-label="Bảng Canvas"
      class="absolute inset-y-0 right-0 z-30 w-full md:w-[26rem] bg-white border-l border-gray-200 shadow-2xl flex flex-col"
      @keydown.esc.stop="emit('close')">
      <!-- Panel header -->
      <div class="flex items-center justify-between gap-2 px-4 py-3 border-b border-gray-200 flex-shrink-0">
        <h2 class="text-base font-semibold text-gray-900 flex items-center gap-2">
          <i class="pi pi-palette text-chatgpt-brand" aria-hidden="true"></i>
          Canvas
        </h2>
        <button
          @click="emit('close')"
          class="w-11 h-11 flex items-center justify-center rounded-lg text-gray-500 hover:text-gray-900 hover:bg-gray-100 transition-colors"
          aria-label="Đóng Canvas">
          <i class="pi pi-times" aria-hidden="true"></i>
        </button>
      </div>

      <!-- Shared mutation error (create / save / delete / version load) -->
      <p v-if="actionError" role="alert" class="mx-4 mt-3 text-sm text-red-600">{{ actionError }}</p>

      <!-- List view -->
      <div v-if="!selected && !isCreating" class="flex-1 overflow-y-auto p-4 flex flex-col gap-3">
        <button
          @click="startCreate"
          class="min-h-11 px-3 flex items-center justify-center gap-2 rounded-lg border border-dashed border-gray-300 text-sm font-medium text-gray-600 hover:text-gray-900 hover:border-gray-400 hover:bg-gray-50 transition-colors">
          <i class="pi pi-plus text-sm" aria-hidden="true"></i>
          <span>Tạo mới</span>
        </button>

        <p v-if="listLoading" class="text-sm text-gray-500">Đang tải danh sách canvas...</p>
        <p v-else-if="listError" role="alert" class="text-sm text-red-600">{{ listError }}</p>
        <p v-else-if="!sessionId" class="text-sm text-gray-500">
          Hãy bắt đầu đoạn chat để lưu và quản lý canvas cho phiên này.
        </p>
        <p v-else-if="artifacts.length === 0" class="text-sm text-gray-500">
          Chưa có canvas nào trong đoạn chat này. Nhấn "Tạo mới" hoặc dùng nút "Mở trong Canvas" trên câu trả lời có chứa mã.
        </p>

        <p v-if="detailError" role="alert" class="text-sm text-red-600">{{ detailError }}</p>
        <p v-if="detailLoading" class="text-sm text-gray-500">Đang mở canvas...</p>

        <ul v-if="artifacts.length > 0" class="flex flex-col gap-2 list-none p-0 m-0">
          <li v-for="artifact in artifacts" :key="artifact.rootId">
            <button
              @click="selectArtifact(artifact)"
              class="w-full text-left p-3 rounded-xl border border-gray-200 bg-white hover:border-sky-300 hover:bg-sky-50/50 transition-colors"
              :aria-label="`Mở canvas ${artifact.title || 'không có tiêu đề'}`">
              <span class="flex items-center justify-between gap-2">
                <span class="font-medium text-gray-800 truncate">{{ artifact.title || 'Không có tiêu đề' }}</span>
                <span class="flex-shrink-0 px-2 py-0.5 rounded-full border text-xs font-medium" :class="kindChipClass(artifact.kind)">
                  {{ kindLabel(artifact.kind) }}
                </span>
              </span>
              <span class="mt-1 flex items-center gap-2 text-xs text-gray-500">
                <span>v{{ artifact.version }}</span>
                <span aria-hidden="true">·</span>
                <span>{{ formatDate(artifact.updatedAt) }}</span>
              </span>
            </button>
          </li>
        </ul>
      </div>

      <!-- Editor view -->
      <div v-else-if="selected" class="flex-1 overflow-y-auto p-4 flex flex-col gap-3">
        <div class="flex items-center justify-between gap-2">
          <button
            @click="backToList"
            class="min-h-11 px-2 flex items-center gap-1.5 text-sm text-gray-600 hover:text-gray-900 rounded-md hover:bg-gray-100 transition-colors"
            aria-label="Quay lại danh sách canvas">
            <i class="pi pi-arrow-left text-sm" aria-hidden="true"></i>
            <span>Danh sách</span>
          </button>
          <span class="px-2 py-0.5 rounded-full border text-xs font-medium" :class="kindChipClass(selected.kind)">
            {{ kindLabel(selected.kind) }}<template v-if="selected.language"> · {{ selected.language }}</template>
          </span>
        </div>

        <label class="flex flex-col gap-1 text-sm text-gray-600">
          Tiêu đề
          <input
            v-model="draftTitle"
            type="text"
            :disabled="isViewingOldVersion"
            class="min-h-11 px-3 rounded-lg border border-gray-300 bg-white text-gray-900 disabled:bg-gray-50 disabled:text-gray-600" />
        </label>

        <label class="flex flex-col gap-1 text-sm text-gray-600">
          Phiên bản
          <select
            v-model="selectedVersion"
            @change="onVersionChange"
            class="min-h-11 px-3 rounded-lg border border-gray-300 bg-white text-gray-900">
            <option v-for="entry in versions" :key="entry.id" :value="entry.version">
              v{{ entry.version }}{{ entry.version === selected.version ? ' (mới nhất)' : '' }} — {{ formatDate(entry.createdAt) }}
            </option>
          </select>
        </label>

        <p v-if="versionLoading" class="text-sm text-gray-500">Đang tải phiên bản...</p>

        <div v-if="isViewingOldVersion" class="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2">
          <span class="text-sm text-amber-800">Đang xem phiên bản cũ (chỉ đọc).</span>
          <button
            @click="restoreVersion"
            class="min-h-11 px-3 rounded-lg bg-amber-700 text-white text-sm font-medium hover:bg-amber-800 transition-colors">
            Khôi phục phiên bản này
          </button>
        </div>

        <label class="flex-1 flex flex-col gap-1 text-sm text-gray-600">
          Nội dung
          <textarea
            :value="editorContent"
            @input="onEditorInput"
            :readonly="isViewingOldVersion"
            rows="12"
            class="w-full flex-1 min-h-[240px] p-3 rounded-lg border border-gray-300 font-mono text-sm text-gray-900 resize-y"
            :class="isViewingOldVersion ? 'bg-gray-50 text-gray-600' : 'bg-white'"></textarea>
        </label>

        <p v-if="statusMessage" role="status" class="text-sm text-emerald-700">{{ statusMessage }}</p>

        <div class="flex flex-wrap items-center gap-2">
          <button
            @click="save"
            :disabled="actionBusy || isViewingOldVersion"
            class="min-h-11 px-4 rounded-lg bg-sky-700 text-white text-sm font-medium hover:bg-sky-800 disabled:opacity-50 transition-colors">
            Lưu
          </button>
          <button
            @click="copyContent"
            class="min-h-11 px-3 rounded-lg border border-gray-200 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors">
            Sao chép
          </button>
          <button
            @click="insertIntoChat"
            class="min-h-11 px-3 rounded-lg border border-gray-200 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors">
            Chèn vào chat
          </button>
          <button
            @click="removeArtifact"
            :disabled="actionBusy"
            class="ml-auto min-h-11 px-3 rounded-lg text-sm font-medium text-red-600 hover:bg-red-50 disabled:opacity-50 transition-colors">
            Xóa
          </button>
        </div>
      </div>

      <!-- Create form -->
      <div v-else class="flex-1 overflow-y-auto p-4 flex flex-col gap-3">
        <div class="flex items-center gap-2">
          <button
            @click="backToList"
            class="min-h-11 px-2 flex items-center gap-1.5 text-sm text-gray-600 hover:text-gray-900 rounded-md hover:bg-gray-100 transition-colors"
            aria-label="Quay lại danh sách canvas">
            <i class="pi pi-arrow-left text-sm" aria-hidden="true"></i>
            <span>Danh sách</span>
          </button>
          <h3 class="text-sm font-semibold text-gray-900">Tạo canvas mới</h3>
        </div>

        <label class="flex flex-col gap-1 text-sm text-gray-600">
          Tiêu đề
          <input
            v-model="createForm.title"
            type="text"
            placeholder="Ví dụ: Ghi chú cuộc họp"
            class="min-h-11 px-3 rounded-lg border border-gray-300 bg-white text-gray-900" />
        </label>

        <label class="flex flex-col gap-1 text-sm text-gray-600">
          Loại
          <select v-model="createForm.kind" class="min-h-11 px-3 rounded-lg border border-gray-300 bg-white text-gray-900">
            <option value="markdown">Markdown</option>
            <option value="code">Code</option>
            <option value="text">Văn bản</option>
          </select>
        </label>

        <label v-if="createForm.kind === 'code'" class="flex flex-col gap-1 text-sm text-gray-600">
          Ngôn ngữ (tùy chọn)
          <input
            v-model="createForm.language"
            type="text"
            placeholder="ví dụ: python"
            class="min-h-11 px-3 rounded-lg border border-gray-300 bg-white text-gray-900" />
        </label>

        <label class="flex-1 flex flex-col gap-1 text-sm text-gray-600">
          Nội dung
          <textarea
            v-model="createForm.content"
            rows="10"
            class="w-full flex-1 min-h-[200px] p-3 rounded-lg border border-gray-300 bg-white font-mono text-sm text-gray-900 resize-y"></textarea>
        </label>

        <div class="flex items-center gap-2">
          <button
            @click="submitCreate"
            :disabled="actionBusy"
            class="min-h-11 px-4 rounded-lg bg-sky-700 text-white text-sm font-medium hover:bg-sky-800 disabled:opacity-50 transition-colors">
            Tạo mới
          </button>
          <button
            @click="backToList"
            class="min-h-11 px-3 rounded-lg border border-gray-200 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors">
            Hủy
          </button>
        </div>
      </div>
    </aside>
  </Transition>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import type {
  CanvasArtifactDetail,
  CanvasArtifactSummary,
  CanvasCreateFromChatPayload,
  CanvasKind,
  CanvasVersionInfo,
} from './canvas.types';

const props = defineProps<{
  visible: boolean;
  sessionId: string | null;
}>();

const emit = defineEmits<{
  (e: 'close'): void;
  (e: 'insert', text: string): void;
  (e: 'count-changed', count: number): void;
}>();

// --- List state ---------------------------------------------------------------

const artifacts = ref<CanvasArtifactSummary[]>([]);
const listLoading = ref(false);
const listError = ref('');
/** Session the list was last loaded for, so re-opening the panel is free. */
let listLoadedFor: string | null = null;

// --- Selection / editor state -------------------------------------------------

const selected = ref<CanvasArtifactDetail | null>(null);
const detailLoading = ref(false);
const detailError = ref('');

const versions = ref<CanvasVersionInfo[]>([]);
const selectedVersion = ref<number | null>(null);
const versionPreview = ref<{ version: number; content: string } | null>(null);
const versionLoading = ref(false);

const draftTitle = ref('');
const draftContent = ref('');

const actionBusy = ref(false);
const actionError = ref('');
const statusMessage = ref('');

// --- Create form --------------------------------------------------------------

const isCreating = ref(false);
const createForm = ref({ title: '', kind: 'markdown', language: '', content: '' });

// --- Derived ------------------------------------------------------------------

const isViewingOldVersion = computed(() => versionPreview.value !== null);
const editorContent = computed(() =>
  versionPreview.value ? versionPreview.value.content : draftContent.value
);

/** Textarea edits write back to the draft; old-version previews are read-only. */
const onEditorInput = (event: Event): void => {
  if (versionPreview.value) return;
  draftContent.value = (event.target as HTMLTextAreaElement).value;
};

// --- Presentation helpers -----------------------------------------------------

const kindLabel = (kind: string): string => {
  if (kind === 'code') return 'Code';
  if (kind === 'markdown') return 'Markdown';
  if (kind === 'text') return 'Văn bản';
  return kind;
};

const kindChipClass = (kind: string): string => {
  if (kind === 'code') return 'bg-sky-50 text-sky-700 border-sky-200';
  if (kind === 'markdown') return 'bg-violet-50 text-violet-700 border-violet-200';
  return 'bg-gray-100 text-gray-700 border-gray-200';
};

const formatDate = (value: string): string => {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('vi-VN');
};

// --- Data loading -------------------------------------------------------------

const loadList = async (force = false): Promise<void> => {
  const sessionId = props.sessionId;
  if (!sessionId) {
    artifacts.value = [];
    listLoadedFor = null;
    return;
  }
  if (!force && listLoadedFor === sessionId) return;
  listLoading.value = true;
  listError.value = '';
  try {
    const response = await http.get(ApiFactory.CANVAS.LIST(sessionId));
    if (!response.ok) {
      listError.value = await readApiError(response, 'Không thể tải danh sách canvas');
      return;
    }
    const data = await response.json() as CanvasArtifactSummary[];
    if (props.sessionId !== sessionId) return; // session switched mid-flight
    artifacts.value = Array.isArray(data) ? data : [];
    listLoadedFor = sessionId;
    emit('count-changed', artifacts.value.length);
  } catch (cause) {
    listError.value = errorMessage(cause, 'Không thể tải danh sách canvas.');
  } finally {
    listLoading.value = false;
  }
};

const loadVersions = async (rootId: string): Promise<void> => {
  try {
    const response = await http.get(ApiFactory.CANVAS.VERSIONS(rootId));
    if (response.ok) {
      const data = await response.json() as CanvasVersionInfo[];
      versions.value = (Array.isArray(data) ? [...data] : []).sort((a, b) => b.version - a.version);
    } else {
      versions.value = [];
    }
  } catch {
    versions.value = [];
  }
  // The dropdown must always at least contain the latest version.
  const current = selected.value;
  if (versions.value.length === 0 && current && current.rootId === rootId) {
    versions.value = [{ id: current.id, version: current.version, createdAt: current.updatedAt }];
  }
};

const selectArtifact = async (artifact: CanvasArtifactSummary): Promise<void> => {
  if (detailLoading.value) return;
  detailError.value = '';
  actionError.value = '';
  statusMessage.value = '';
  detailLoading.value = true;
  try {
    const response = await http.get(ApiFactory.CANVAS.BY_ROOT(artifact.rootId));
    if (!response.ok) {
      detailError.value = await readApiError(response, 'Không thể tải nội dung canvas');
      return;
    }
    const detail = await response.json() as CanvasArtifactDetail;
    selected.value = detail;
    isCreating.value = false;
    draftTitle.value = detail.title;
    draftContent.value = detail.content;
    selectedVersion.value = detail.version;
    versionPreview.value = null;
    await loadVersions(detail.rootId);
  } catch (cause) {
    detailError.value = errorMessage(cause, 'Không thể tải nội dung canvas.');
  } finally {
    detailLoading.value = false;
  }
};

// --- Versions -----------------------------------------------------------------

const onVersionChange = async (): Promise<void> => {
  const current = selected.value;
  const version = selectedVersion.value;
  if (!current || version === null) return;
  actionError.value = '';
  statusMessage.value = '';
  if (version === current.version) {
    versionPreview.value = null;
    return;
  }
  versionLoading.value = true;
  try {
    const response = await http.get(ApiFactory.CANVAS.VERSION(current.rootId, version));
    if (!response.ok) {
      actionError.value = await readApiError(response, 'Không thể tải phiên bản này');
      selectedVersion.value = current.version;
      versionPreview.value = null;
      return;
    }
    const detail = await response.json() as CanvasArtifactDetail;
    versionPreview.value = { version, content: detail.content };
  } catch (cause) {
    actionError.value = errorMessage(cause, 'Không thể tải phiên bản này.');
    selectedVersion.value = current.version;
    versionPreview.value = null;
  } finally {
    versionLoading.value = false;
  }
};

const restoreVersion = (): void => {
  const current = selected.value;
  const preview = versionPreview.value;
  if (!current || !preview) return;
  draftContent.value = preview.content;
  versionPreview.value = null;
  selectedVersion.value = current.version;
  statusMessage.value = `Đã khôi phục nội dung phiên bản ${preview.version} vào trình soạn thảo. Nhấn "Lưu" để tạo phiên bản mới.`;
};

// --- Mutations ----------------------------------------------------------------

const save = async (): Promise<void> => {
  const current = selected.value;
  if (!current || actionBusy.value || isViewingOldVersion.value) return;
  const title = draftTitle.value.trim();
  if (!title) {
    actionError.value = 'Vui lòng nhập tiêu đề.';
    return;
  }
  actionBusy.value = true;
  actionError.value = '';
  statusMessage.value = '';
  try {
    const response = await http.put(ApiFactory.CANVAS.BY_ROOT(current.rootId), {
      title,
      content: draftContent.value,
    });
    if (!response.ok) {
      actionError.value = await readApiError(response, 'Không thể lưu canvas');
      return;
    }
    const result = await response.json() as { id: string; version: number };
    current.id = result.id;
    current.version = result.version;
    current.title = title;
    current.content = draftContent.value;
    selectedVersion.value = result.version;
    statusMessage.value = `Đã lưu phiên bản ${result.version}.`;
    await loadVersions(current.rootId);
    await loadList(true);
  } catch (cause) {
    actionError.value = errorMessage(cause, 'Không thể lưu canvas.');
  } finally {
    actionBusy.value = false;
  }
};

const removeArtifact = async (): Promise<void> => {
  const current = selected.value;
  if (!current || actionBusy.value) return;
  if (!confirm('Xóa canvas này? Tất cả phiên bản của nó sẽ bị xóa vĩnh viễn.')) return;
  actionBusy.value = true;
  actionError.value = '';
  statusMessage.value = '';
  try {
    const response = await http.delete(ApiFactory.CANVAS.BY_ROOT(current.rootId));
    if (!response.ok) {
      actionError.value = await readApiError(response, 'Không thể xóa canvas');
      return;
    }
    selected.value = null;
    versions.value = [];
    versionPreview.value = null;
    await loadList(true);
  } catch (cause) {
    actionError.value = errorMessage(cause, 'Không thể xóa canvas.');
  } finally {
    actionBusy.value = false;
  }
};

const createArtifact = async (payload: CanvasCreateFromChatPayload): Promise<boolean> => {
  if (actionBusy.value) return false;
  actionBusy.value = true;
  actionError.value = '';
  statusMessage.value = '';
  try {
    const response = await http.post(ApiFactory.CANVAS.CREATE, {
      chatSessionId: props.sessionId ?? undefined,
      title: payload.title,
      kind: payload.kind,
      language: payload.language ?? undefined,
      content: payload.content,
    });
    if (!response.ok) {
      actionError.value = await readApiError(response, 'Không thể tạo canvas');
      return false;
    }
    const detail = await response.json() as CanvasArtifactDetail;
    isCreating.value = false;
    selected.value = detail;
    draftTitle.value = detail.title;
    draftContent.value = detail.content;
    selectedVersion.value = detail.version;
    versionPreview.value = null;
    statusMessage.value = 'Đã tạo canvas mới.';
    await loadVersions(detail.rootId);
    await loadList(true);
    return true;
  } catch (cause) {
    actionError.value = errorMessage(cause, 'Không thể tạo canvas.');
    return false;
  } finally {
    actionBusy.value = false;
  }
};

const submitCreate = async (): Promise<void> => {
  const title = createForm.value.title.trim();
  if (!title) {
    actionError.value = 'Vui lòng nhập tiêu đề.';
    return;
  }
  const kind: CanvasKind = createForm.value.kind === 'code'
    ? 'code'
    : createForm.value.kind === 'text' ? 'text' : 'markdown';
  const language = kind === 'code' && createForm.value.language.trim()
    ? createForm.value.language.trim()
    : null;
  const created = await createArtifact({ title, kind, language, content: createForm.value.content });
  if (created) createForm.value = { title: '', kind: 'markdown', language: '', content: '' };
};

// --- Clipboard / chat integration --------------------------------------------

const copyContent = async (): Promise<void> => {
  actionError.value = '';
  try {
    await navigator.clipboard.writeText(editorContent.value);
    statusMessage.value = 'Đã sao chép nội dung vào clipboard.';
  } catch {
    actionError.value = 'Không thể sao chép nội dung.';
  }
};

const insertIntoChat = (): void => {
  const current = selected.value;
  if (!current) return;
  const language = current.language ?? (current.kind === 'markdown' ? 'md' : '');
  emit('insert', '```' + language + '\n' + editorContent.value + '\n```');
  statusMessage.value = 'Đã chèn nội dung vào ô soạn tin nhắn.';
};

// --- Navigation ---------------------------------------------------------------

const startCreate = (): void => {
  isCreating.value = true;
  selected.value = null;
  versionPreview.value = null;
  actionError.value = '';
  statusMessage.value = '';
  detailError.value = '';
};

const backToList = (): void => {
  selected.value = null;
  isCreating.value = false;
  versionPreview.value = null;
  actionError.value = '';
  statusMessage.value = '';
  detailError.value = '';
};

/**
 * Entry point for ChatView's "Mở trong Canvas" button: creates the artifact
 * from the message's code block and opens the editor on it. Errors surface in
 * the panel's shared alert area.
 */
const createFromChat = async (payload: CanvasCreateFromChatPayload): Promise<void> => {
  backToList();
  await createArtifact(payload);
};

defineExpose({ createFromChat });

// --- Reactivity ---------------------------------------------------------------

watch(() => props.visible, (visible) => {
  if (visible) void loadList();
});

watch(() => props.sessionId, () => {
  artifacts.value = [];
  listLoadedFor = null;
  listError.value = '';
  backToList();
  if (props.visible) void loadList();
});
</script>

<style scoped>
.canvas-slide-enter-active,
.canvas-slide-leave-active {
  transition: transform 0.2s ease-in-out;
}

.canvas-slide-enter-from,
.canvas-slide-leave-to {
  transform: translateX(100%);
}

@media (prefers-reduced-motion: reduce) {
  .canvas-slide-enter-active,
  .canvas-slide-leave-active {
    transition: none;
  }
}
</style>
