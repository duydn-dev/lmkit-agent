<template>
  <div class="flex-1 flex flex-col h-full bg-chatgpt-dark overflow-y-auto">
    <!-- Page Header -->
    <div class="sticky top-0 z-10 bg-chatgpt-dark/80 backdrop-blur-xl border-b border-gray-200/60">
      <div class="max-w-7xl mx-auto px-6 py-4">
        <div class="flex items-center justify-between gap-4">
          <div class="flex items-center gap-4">
            <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-violet-500 to-purple-600 flex items-center justify-center shadow-md shadow-purple-500/20">
              <i class="pi pi-microchip-ai text-white text-sm"></i>
            </div>
            <div>
              <h1 class="text-xl font-bold text-gray-900 tracking-tight">Agents</h1>
              <p class="text-xs text-gray-500">Tạo trợ lý chuyên biệt với persona, công cụ và tri thức riêng</p>
            </div>
          </div>
          <Button
            @click="openCreateForm"
            label="Tạo agent"
            icon="pi pi-plus"
            class="!min-h-11 !px-4 !py-2.5 !rounded-xl !text-sm !font-medium !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800"
          />
        </div>
      </div>
    </div>

    <!-- Main Content -->
    <div class="flex-1 max-w-7xl mx-auto w-full px-6 py-6">
      <div v-if="pageError" role="alert" class="mb-5 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        {{ pageError }}
      </div>

      <div v-if="loading" class="flex flex-col items-center justify-center py-20 text-gray-500" role="status">
        <i class="pi pi-spin pi-spinner text-2xl mb-3" aria-hidden="true"></i>
        <p class="text-sm">Đang tải danh sách agent...</p>
      </div>

      <div v-else-if="agents.length === 0" class="flex flex-col items-center justify-center py-20 text-center">
        <div class="w-20 h-20 rounded-2xl bg-gradient-to-br from-gray-100 to-gray-200 flex items-center justify-center mb-5 shadow-inner">
          <i class="pi pi-microchip-ai text-3xl text-gray-300" aria-hidden="true"></i>
        </div>
        <h3 class="text-lg font-semibold text-gray-600 mb-1">Chưa có agent nào</h3>
        <p class="text-sm text-gray-400 max-w-xs mb-4">Tạo agent đầu tiên với persona riêng để bắt đầu.</p>
        <Button label="Tạo agent" icon="pi pi-plus" @click="openCreateForm" class="!min-h-11 !px-4 !rounded-xl !text-sm !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800" />
      </div>

      <div v-else class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
        <div v-for="agent in agents" :key="agent.id" class="flex flex-col bg-white rounded-2xl border border-gray-100 shadow-sm hover:shadow-md hover:border-gray-200 transition-all duration-300 p-5">
          <div class="flex items-start justify-between gap-3 mb-3">
            <div class="flex items-center gap-3 min-w-0">
              <div class="w-11 h-11 rounded-xl bg-gradient-to-br from-violet-50 to-purple-100 flex items-center justify-center flex-shrink-0 text-xl" aria-hidden="true">
                <span v-if="agent.icon">{{ agent.icon }}</span>
                <i v-else class="pi pi-microchip-ai text-purple-500"></i>
              </div>
              <div class="min-w-0">
                <h2 class="text-sm font-semibold text-gray-900 truncate">{{ agent.name }}</h2>
                <p class="text-[11px] text-gray-400">Tạo ngày {{ formatDate(agent.createdAt) }}</p>
              </div>
            </div>
          </div>

          <p class="text-xs text-gray-500 leading-relaxed line-clamp-3 flex-1">{{ agent.description || 'Chưa có mô tả.' }}</p>

          <div class="flex flex-wrap items-center gap-1.5 mt-3">
            <span v-if="agent.isOwner" class="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[11px] font-semibold bg-sky-50 text-sky-800 border border-sky-200">
              <i class="pi pi-user text-[10px]" aria-hidden="true"></i> Của tôi
            </span>
            <span v-if="agent.isSharedWithTenant" class="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[11px] font-semibold bg-emerald-50 text-emerald-900 border border-emerald-200">
              <i class="pi pi-share-alt text-[10px]" aria-hidden="true"></i> Chia sẻ tenant
            </span>
            <span class="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-medium bg-gray-50 text-gray-500 border border-gray-200">
              {{ agent.allowedTools && agent.allowedTools.length > 0 ? `${agent.allowedTools.length} công cụ` : 'Tất cả công cụ theo quyền' }}
            </span>
            <span v-if="agent.knowledgeDocumentIds.length > 0" class="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-medium bg-gray-50 text-gray-500 border border-gray-200">
              {{ agent.knowledgeDocumentIds.length }} tài liệu
            </span>
          </div>

          <div class="flex items-center gap-2 mt-4 pt-3 border-t border-gray-100">
            <Button
              label="Chat với agent"
              icon="pi pi-comments"
              :loading="chatStartingId === agent.id"
              :disabled="chatStartingId !== null"
              @click="startChatWithAgent(agent)"
              class="flex-1 !min-h-11 !rounded-xl !text-sm !font-medium !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800"
            />
            <Button
              v-if="agent.isOwner"
              icon="pi pi-pencil"
              severity="secondary"
              text
              @click="openEditForm(agent)"
              :aria-label="`Chỉnh sửa agent ${agent.name}`"
              class="!w-11 !h-11 !rounded-xl"
            />
            <Button
              v-if="agent.isOwner"
              icon="pi pi-trash"
              severity="danger"
              text
              :disabled="deletingId === agent.id"
              @click="deleteAgent(agent)"
              :aria-label="`Xóa agent ${agent.name}`"
              class="!w-11 !h-11 !rounded-xl"
            />
          </div>
        </div>
      </div>
    </div>

    <!-- Create / Edit Dialog -->
    <Dialog
      v-model:visible="showForm"
      modal
      :header="editingId ? 'Chỉnh sửa agent' : 'Tạo agent mới'"
      :style="{ width: '560px' }"
      :breakpoints="{ '575px': '90vw' }"
    >
      <form @submit.prevent="saveAgent" class="grid gap-4 pt-1">
        <div v-if="formError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
          {{ formError }}
        </div>

        <div class="grid grid-cols-1 sm:grid-cols-[1fr_120px] gap-3">
          <div class="grid gap-1">
            <label for="agent-name" class="text-sm font-medium text-gray-700">Tên agent</label>
            <InputText id="agent-name" v-model="form.name" required maxlength="100" placeholder="Ví dụ: Chuyên gia hợp đồng" />
          </div>
          <div class="grid gap-1">
            <label for="agent-icon" class="text-sm font-medium text-gray-700">Biểu tượng</label>
            <InputText id="agent-icon" v-model="form.icon" maxlength="8" placeholder="Ví dụ: 🤖" />
          </div>
        </div>

        <div class="grid gap-1">
          <label for="agent-description" class="text-sm font-medium text-gray-700">Mô tả</label>
          <InputText id="agent-description" v-model="form.description" maxlength="300" placeholder="Agent này làm gì?" />
        </div>

        <div class="grid gap-1">
          <label for="agent-persona" class="text-sm font-medium text-gray-700">Persona prompt <span class="text-red-600" aria-hidden="true">*</span></label>
          <Textarea id="agent-persona" v-model="form.personaPrompt" rows="5" required placeholder="Bạn là chuyên gia... Hãy trả lời theo phong cách..." class="w-full" />
          <p class="text-xs text-gray-400">Chỉ dẫn hệ thống định hình tính cách và chuyên môn của agent.</p>
        </div>

        <div class="grid gap-1">
          <label for="agent-tools" class="text-sm font-medium text-gray-700">Công cụ được phép</label>
          <MultiSelect
            v-model="form.allowedTools"
            :options="toolCatalog"
            optionLabel="label"
            optionValue="name"
            display="chip"
            :loading="optionsLoading"
            inputId="agent-tools"
            placeholder="Tất cả công cụ theo quyền"
            class="w-full"
          />
          <p class="text-xs text-gray-400">Bỏ trống = tất cả công cụ theo quyền của người dùng.</p>
        </div>

        <div class="grid gap-1">
          <label for="agent-docs" class="text-sm font-medium text-gray-700">Tri thức đính kèm</label>
          <MultiSelect
            v-model="form.knowledgeDocumentIds"
            :options="documents"
            optionLabel="fileName"
            optionValue="id"
            display="chip"
            filter
            :loading="optionsLoading"
            inputId="agent-docs"
            placeholder="Chọn tài liệu từ kho của bạn"
            class="w-full"
          />
          <p class="text-xs text-gray-400">Agent sẽ ưu tiên trả lời dựa trên các tài liệu này.</p>
        </div>

        <label for="agent-shared" class="flex items-center gap-2 text-sm text-gray-700">
          <Checkbox inputId="agent-shared" v-model="form.isSharedWithTenant" binary />
          Chia sẻ agent này với toàn tenant
        </label>

        <div class="flex items-center justify-end gap-2 pt-1">
          <Button type="button" label="Hủy" text severity="secondary" :disabled="saving" @click="showForm = false" class="!min-h-11 !px-4 !rounded-xl !text-sm" />
          <Button type="submit" :label="editingId ? 'Lưu thay đổi' : 'Tạo agent'" icon="pi pi-check" :loading="saving" class="!min-h-11 !px-4 !rounded-xl !text-sm !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800" />
        </div>
      </form>
    </Dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import { formatDate } from '@/utils/date';

interface CustomAgent {
  id: string;
  name: string;
  description: string | null;
  icon: string | null;
  /** null unless the caller owns the agent. */
  personaPrompt: string | null;
  allowedTools: string[] | null;
  knowledgeDocumentIds: string[];
  isSharedWithTenant: boolean;
  isOwner: boolean;
  createdAt: string;
}

interface ToolCatalogItem {
  name: string;
  label: string;
  description: string;
}

/** Minimal slice of the documents list (same endpoint DocumentView consumes). */
interface KnowledgeDocument {
  id: string;
  fileName: string;
}

interface AgentForm {
  name: string;
  icon: string;
  description: string;
  personaPrompt: string;
  allowedTools: string[];
  knowledgeDocumentIds: string[];
  isSharedWithTenant: boolean;
}

const router = useRouter();

const agents = ref<CustomAgent[]>([]);
const loading = ref(false);
const pageError = ref('');
const chatStartingId = ref<string | null>(null);
const deletingId = ref<string | null>(null);

const showForm = ref(false);
const editingId = ref<string | null>(null);
const saving = ref(false);
const formError = ref('');

const toolCatalog = ref<ToolCatalogItem[]>([]);
const documents = ref<KnowledgeDocument[]>([]);
const optionsLoading = ref(false);
let optionsLoaded = false;

const emptyForm = (): AgentForm => ({
  name: '',
  icon: '',
  description: '',
  personaPrompt: '',
  allowedTools: [],
  knowledgeDocumentIds: [],
  isSharedWithTenant: false
});

const form = ref<AgentForm>(emptyForm());

const loadAgents = async () => {
  loading.value = agents.value.length === 0;
  pageError.value = '';
  try {
    const response = await http.get(ApiFactory.AGENTS.CUSTOM);
    if (response.ok) agents.value = await response.json();
    else pageError.value = await readApiError(response, 'Không thể tải danh sách agent');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể tải danh sách agent.');
  } finally {
    loading.value = false;
  }
};

/** Lazily loads the tool catalog + the caller's documents for the pickers. */
const ensureFormOptions = async () => {
  if (optionsLoaded) return;
  optionsLoading.value = true;
  try {
    const [toolsResponse, docsResponse] = await Promise.all([
      http.get(ApiFactory.AGENTS.TOOL_CATALOG),
      http.get(ApiFactory.DOCUMENT.BASE)
    ]);
    if (toolsResponse.ok) toolCatalog.value = await toolsResponse.json();
    if (docsResponse.ok) documents.value = await docsResponse.json();
    optionsLoaded = toolsResponse.ok && docsResponse.ok;
    if (!optionsLoaded) {
      formError.value = 'Không thể tải danh mục công cụ hoặc tài liệu. Bạn vẫn có thể lưu agent.';
    }
  } catch (cause) {
    formError.value = errorMessage(cause, 'Không thể tải danh mục công cụ hoặc tài liệu.');
  } finally {
    optionsLoading.value = false;
  }
};

const openCreateForm = () => {
  editingId.value = null;
  form.value = emptyForm();
  formError.value = '';
  showForm.value = true;
  void ensureFormOptions();
};

const openEditForm = (agent: CustomAgent) => {
  editingId.value = agent.id;
  form.value = {
    name: agent.name,
    icon: agent.icon ?? '',
    description: agent.description ?? '',
    personaPrompt: agent.personaPrompt ?? '',
    allowedTools: agent.allowedTools ? [...agent.allowedTools] : [],
    knowledgeDocumentIds: [...agent.knowledgeDocumentIds],
    isSharedWithTenant: agent.isSharedWithTenant
  };
  formError.value = '';
  showForm.value = true;
  void ensureFormOptions();
};

const saveAgent = async () => {
  const name = form.value.name.trim();
  const personaPrompt = form.value.personaPrompt.trim();
  if (!name) {
    formError.value = 'Vui lòng nhập tên agent.';
    return;
  }
  if (!personaPrompt) {
    formError.value = 'Vui lòng nhập persona prompt.';
    return;
  }

  formError.value = '';
  saving.value = true;
  const payload = {
    name,
    description: form.value.description.trim() || undefined,
    icon: form.value.icon.trim() || undefined,
    personaPrompt,
    // Empty selection means "all tools the caller is entitled to" → null.
    allowedTools: form.value.allowedTools.length > 0 ? form.value.allowedTools : null,
    knowledgeDocumentIds: form.value.knowledgeDocumentIds,
    isSharedWithTenant: form.value.isSharedWithTenant
  };

  try {
    const response = editingId.value
      ? await http.put(ApiFactory.AGENTS.CUSTOM_BY_ID(editingId.value), payload)
      : await http.post(ApiFactory.AGENTS.CUSTOM, payload);
    if (!response.ok) {
      formError.value = await readApiError(response, 'Không thể lưu agent');
      return;
    }
    showForm.value = false;
    await loadAgents();
  } catch (cause) {
    formError.value = errorMessage(cause, 'Không thể lưu agent.');
  } finally {
    saving.value = false;
  }
};

const deleteAgent = async (agent: CustomAgent) => {
  if (!confirm(`Xóa agent "${agent.name}"? Hành động này không thể hoàn tác.`)) return;
  deletingId.value = agent.id;
  pageError.value = '';
  try {
    const response = await http.delete(ApiFactory.AGENTS.CUSTOM_BY_ID(agent.id));
    if (response.ok) agents.value = agents.value.filter((item) => item.id !== agent.id);
    else pageError.value = await readApiError(response, 'Không thể xóa agent');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể xóa agent.');
  } finally {
    deletingId.value = null;
  }
};

const startChatWithAgent = async (agent: CustomAgent) => {
  chatStartingId.value = agent.id;
  pageError.value = '';
  try {
    const response = await http.post(ApiFactory.CHAT.CREATE_SESSION, {
      title: `Chat với ${agent.name}`,
      customAgentId: agent.id
    });
    if (!response.ok) {
      pageError.value = await readApiError(response, 'Không thể tạo phiên chat với agent');
      return;
    }
    // Same event name AppLayout/ChatView already listen for to refresh history.
    window.dispatchEvent(new CustomEvent('chat-session-created'));
    // Activate the newly-created session by id so ChatView binds the agent's
    // persona/tools/knowledge. Without ?id= the first message would spawn a fresh,
    // un-bound session. Fall back to the previous behavior if the id is missing.
    const created = await response.json().catch(() => null);
    if (created && typeof created.id === 'string' && created.id) {
      await router.push('/chat?id=' + created.id);
    } else {
      pageError.value = 'Không thể mở phiên chat vừa tạo với agent.';
      await router.push('/');
    }
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể tạo phiên chat với agent.');
  } finally {
    chatStartingId.value = null;
  }
};

onMounted(() => {
  void loadAgents();
});
</script>
