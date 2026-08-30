<template>
  <div class="flex-1 flex flex-col h-full bg-chatgpt-dark overflow-y-auto">
    <!-- Page Header -->
    <div class="sticky top-0 z-10 bg-chatgpt-dark/80 backdrop-blur-xl border-b border-gray-200/60">
      <div class="max-w-7xl mx-auto px-6 py-4">
        <div class="flex items-center justify-between gap-4">
          <div class="flex items-center gap-4">
            <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-sky-500 to-blue-600 flex items-center justify-center shadow-md shadow-sky-500/20">
              <i class="pi pi-folder text-white text-sm"></i>
            </div>
            <div>
              <h1 class="text-xl font-bold text-gray-900 tracking-tight">Dự án</h1>
              <p class="text-xs text-gray-500">Nhóm các đoạn chat theo dự án với hướng dẫn chung</p>
            </div>
          </div>
          <Button
            @click="openCreateForm"
            label="Tạo dự án"
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
        <p class="text-sm">Đang tải danh sách dự án...</p>
      </div>

      <div v-else-if="projects.length === 0" class="flex flex-col items-center justify-center py-20 text-center">
        <div class="w-20 h-20 rounded-2xl bg-gradient-to-br from-gray-100 to-gray-200 flex items-center justify-center mb-5 shadow-inner">
          <i class="pi pi-folder-open text-3xl text-gray-300" aria-hidden="true"></i>
        </div>
        <h3 class="text-lg font-semibold text-gray-600 mb-1">Chưa có dự án nào</h3>
        <p class="text-sm text-gray-400 max-w-xs mb-4">Tạo dự án đầu tiên để nhóm các đoạn chat và áp dụng hướng dẫn chung cho trợ lý.</p>
        <Button label="Tạo dự án" icon="pi pi-plus" @click="openCreateForm" class="!min-h-11 !px-4 !rounded-xl !text-sm !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800" />
      </div>

      <div v-else class="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <div
          v-for="project in projects"
          :key="project.id"
          class="flex flex-col bg-white rounded-2xl border border-gray-100 shadow-sm hover:shadow-md hover:border-gray-200 transition-all duration-300 p-5"
        >
          <div class="flex items-start justify-between gap-2">
            <button
              type="button"
              @click="toggleProject(project)"
              :aria-expanded="expandedId === project.id"
              :aria-controls="`project-sessions-${project.id}`"
              class="flex items-center gap-3 min-w-0 flex-1 min-h-11 text-left rounded-xl cursor-pointer"
            >
              <span class="w-11 h-11 rounded-xl bg-gradient-to-br from-sky-50 to-blue-100 flex items-center justify-center flex-shrink-0 text-xl" aria-hidden="true">
                <span v-if="project.icon">{{ project.icon }}</span>
                <i v-else class="pi pi-folder text-sky-700"></i>
              </span>
              <span class="min-w-0 flex-1">
                <span class="block text-sm font-semibold text-gray-900 truncate">{{ project.name }}</span>
                <span class="block text-[11px] text-gray-400">{{ project.sessionCount }} đoạn chat · Cập nhật {{ formatDate(project.updatedAt) }}</span>
              </span>
              <i
                class="pi text-gray-400 flex-shrink-0"
                :class="expandedId === project.id ? 'pi-chevron-up' : 'pi-chevron-down'"
                aria-hidden="true"
              ></i>
            </button>
            <Button
              icon="pi pi-pencil"
              severity="secondary"
              text
              @click="openEditForm(project)"
              :aria-label="`Chỉnh sửa dự án ${project.name}`"
              class="!w-11 !h-11 !rounded-xl flex-shrink-0"
            />
            <Button
              icon="pi pi-trash"
              severity="danger"
              text
              :disabled="deletingId === project.id"
              @click="deleteProject(project)"
              :aria-label="`Xóa dự án ${project.name}`"
              class="!w-11 !h-11 !rounded-xl flex-shrink-0"
            />
          </div>

          <p class="text-xs text-gray-500 leading-relaxed line-clamp-2 mt-3">{{ project.description || 'Chưa có mô tả.' }}</p>
          <span
            v-if="project.instructions"
            class="inline-flex items-center gap-1 self-start px-2 py-0.5 rounded-full text-[11px] font-medium bg-sky-50 text-sky-800 border border-sky-200 mt-2"
          >
            <i class="pi pi-book text-[10px]" aria-hidden="true"></i> Có hướng dẫn riêng
          </span>

          <!-- Expanded: sessions of this project -->
          <div v-if="expandedId === project.id" :id="`project-sessions-${project.id}`" class="mt-4 pt-3 border-t border-gray-100">
            <div class="flex items-center justify-between gap-2 mb-2 flex-wrap">
              <h2 class="text-xs font-semibold text-gray-500 uppercase tracking-wider">Đoạn chat trong dự án</h2>
              <Button
                label="Chat mới trong dự án"
                icon="pi pi-plus"
                :loading="chatStartingId === project.id"
                :disabled="chatStartingId !== null"
                @click="startChatInProject(project)"
                class="!min-h-11 !px-3 !rounded-xl !text-xs !font-medium !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800"
              />
            </div>

            <div v-if="sessionsError" role="alert" class="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700 mb-2">
              {{ sessionsError }}
            </div>
            <div v-if="sessionsLoading" class="py-3 text-xs text-gray-500 italic" role="status">Đang tải đoạn chat...</div>
            <template v-else>
              <p v-if="projectSessions.length === 0" class="py-3 text-xs text-gray-500 italic">Chưa có đoạn chat nào trong dự án này.</p>
              <div v-else class="grid gap-1">
                <button
                  v-for="session in projectSessions"
                  :key="session.id"
                  type="button"
                  @click="openSession(session.id)"
                  class="w-full min-h-11 flex items-center justify-between gap-3 px-3 py-2 rounded-lg text-left hover:bg-chatgpt-light transition-colors cursor-pointer"
                >
                  <span class="flex items-center gap-2 min-w-0">
                    <i class="pi pi-message text-gray-500 flex-shrink-0" aria-hidden="true"></i>
                    <span class="text-sm text-gray-700 truncate">{{ session.title || 'Đoạn chat mới' }}</span>
                  </span>
                  <span class="text-[11px] text-gray-400 flex-shrink-0">{{ formatDate(session.createdAt) }}</span>
                </button>
              </div>
            </template>
          </div>
        </div>
      </div>
    </div>

    <!-- Create / Edit Dialog -->
    <Dialog
      v-model:visible="showForm"
      modal
      :header="editingId ? 'Chỉnh sửa dự án' : 'Tạo dự án mới'"
      :style="{ width: '560px' }"
      :breakpoints="{ '575px': '90vw' }"
    >
      <form @submit.prevent="saveProject" class="grid gap-4 pt-1">
        <div v-if="formError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
          {{ formError }}
        </div>

        <div class="grid grid-cols-1 sm:grid-cols-[1fr_120px] gap-3">
          <div class="grid gap-1">
            <label for="project-name" class="text-sm font-medium text-gray-700">Tên dự án</label>
            <InputText id="project-name" v-model="form.name" required maxlength="100" placeholder="Ví dụ: Ra mắt sản phẩm Q4" />
          </div>
          <div class="grid gap-1">
            <label for="project-icon" class="text-sm font-medium text-gray-700">Biểu tượng</label>
            <InputText id="project-icon" v-model="form.icon" maxlength="8" placeholder="Ví dụ: 📁" />
          </div>
        </div>

        <div class="grid gap-1">
          <label for="project-description" class="text-sm font-medium text-gray-700">Mô tả</label>
          <InputText id="project-description" v-model="form.description" maxlength="300" placeholder="Dự án này về điều gì?" />
        </div>

        <div class="grid gap-1">
          <label for="project-instructions" class="text-sm font-medium text-gray-700">Hướng dẫn cho trợ lý</label>
          <Textarea
            id="project-instructions"
            v-model="form.instructions"
            rows="5"
            placeholder="Ví dụ: Luôn trả lời ngắn gọn, ưu tiên số liệu và bối cảnh của dự án này..."
            class="w-full"
          />
          <p class="text-xs text-gray-400">Hướng dẫn này áp dụng cho mọi đoạn chat trong dự án.</p>
        </div>

        <div class="flex items-center justify-end gap-2 pt-1">
          <Button type="button" label="Hủy" text severity="secondary" :disabled="saving" @click="showForm = false" class="!min-h-11 !px-4 !rounded-xl !text-sm" />
          <Button
            type="submit"
            :label="editingId ? 'Lưu thay đổi' : 'Tạo dự án'"
            icon="pi pi-check"
            :loading="saving"
            class="!min-h-11 !px-4 !rounded-xl !text-sm !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800"
          />
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

interface Project {
  id: string;
  name: string;
  description: string | null;
  icon: string | null;
  instructions: string | null;
  sessionCount: number;
  createdAt: string;
  updatedAt: string;
}

/** Same shape the sidebar history consumes (ChatSessionDto slice). */
interface ProjectSession {
  id: string;
  title: string | null;
  createdAt: string;
}

interface ProjectForm {
  name: string;
  icon: string;
  description: string;
  instructions: string;
}

const router = useRouter();

const projects = ref<Project[]>([]);
const loading = ref(false);
const pageError = ref('');
const deletingId = ref<string | null>(null);
const chatStartingId = ref<string | null>(null);

/** Id of the project whose sessions are currently expanded (one at a time). */
const expandedId = ref<string | null>(null);
const projectSessions = ref<ProjectSession[]>([]);
const sessionsLoading = ref(false);
const sessionsError = ref('');

const showForm = ref(false);
const editingId = ref<string | null>(null);
const saving = ref(false);
const formError = ref('');

const emptyForm = (): ProjectForm => ({ name: '', icon: '', description: '', instructions: '' });
const form = ref<ProjectForm>(emptyForm());

const loadProjects = async () => {
  loading.value = projects.value.length === 0;
  pageError.value = '';
  try {
    const response = await http.get(ApiFactory.PROJECTS.BASE);
    if (response.ok) projects.value = await response.json();
    else pageError.value = await readApiError(response, 'Không thể tải danh sách dự án');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể tải danh sách dự án.');
  } finally {
    loading.value = false;
  }
};

const loadSessions = async (projectId: string) => {
  projectSessions.value = [];
  sessionsError.value = '';
  sessionsLoading.value = true;
  try {
    const response = await http.get(ApiFactory.PROJECTS.SESSIONS(projectId));
    // The user may have collapsed or switched projects while this was in flight.
    if (expandedId.value !== projectId) return;
    if (response.ok) projectSessions.value = await response.json();
    else sessionsError.value = await readApiError(response, 'Không thể tải đoạn chat của dự án');
  } catch (cause) {
    if (expandedId.value !== projectId) return;
    sessionsError.value = errorMessage(cause, 'Không thể tải đoạn chat của dự án.');
  } finally {
    if (expandedId.value === projectId) sessionsLoading.value = false;
  }
};

const toggleProject = async (project: Project) => {
  if (expandedId.value === project.id) {
    expandedId.value = null;
    return;
  }
  expandedId.value = project.id;
  await loadSessions(project.id);
};

/** Same activation mechanism as the sidebar history: ChatView watches ?id=. */
const openSession = (sessionId: string) => {
  router.push(`/chat?id=${sessionId}`);
};

const startChatInProject = async (project: Project) => {
  chatStartingId.value = project.id;
  pageError.value = '';
  try {
    const response = await http.post(ApiFactory.CHAT.CREATE_SESSION, { projectId: project.id });
    if (!response.ok) {
      pageError.value = await readApiError(response, 'Không thể tạo đoạn chat trong dự án');
      return;
    }
    // Same event name AppLayout/ChatView already listen for to refresh history.
    window.dispatchEvent(new CustomEvent('chat-session-created'));
    await router.push('/');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể tạo đoạn chat trong dự án.');
  } finally {
    chatStartingId.value = null;
  }
};

const openCreateForm = () => {
  editingId.value = null;
  form.value = emptyForm();
  formError.value = '';
  showForm.value = true;
};

const openEditForm = (project: Project) => {
  editingId.value = project.id;
  form.value = {
    name: project.name,
    icon: project.icon ?? '',
    description: project.description ?? '',
    instructions: project.instructions ?? ''
  };
  formError.value = '';
  showForm.value = true;
};

const saveProject = async () => {
  const name = form.value.name.trim();
  if (!name) {
    formError.value = 'Vui lòng nhập tên dự án.';
    return;
  }
  formError.value = '';
  saving.value = true;
  const payload = {
    name,
    description: form.value.description.trim() || undefined,
    icon: form.value.icon.trim() || undefined,
    instructions: form.value.instructions.trim() || undefined
  };
  try {
    const response = editingId.value
      ? await http.put(ApiFactory.PROJECTS.BY_ID(editingId.value), payload)
      : await http.post(ApiFactory.PROJECTS.BASE, payload);
    if (!response.ok) {
      formError.value = await readApiError(response, 'Không thể lưu dự án');
      return;
    }
    showForm.value = false;
    await loadProjects();
  } catch (cause) {
    formError.value = errorMessage(cause, 'Không thể lưu dự án.');
  } finally {
    saving.value = false;
  }
};

const deleteProject = async (project: Project) => {
  if (!confirm(`Xóa dự án "${project.name}"? Các đoạn chat trong dự án sẽ được giữ lại, chỉ không còn thuộc dự án nữa.`)) return;
  deletingId.value = project.id;
  pageError.value = '';
  try {
    const response = await http.delete(ApiFactory.PROJECTS.BY_ID(project.id));
    if (response.ok) {
      if (expandedId.value === project.id) expandedId.value = null;
      projects.value = projects.value.filter((item) => item.id !== project.id);
    } else pageError.value = await readApiError(response, 'Không thể xóa dự án');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể xóa dự án.');
  } finally {
    deletingId.value = null;
  }
};

const formatDate = (iso: string): string => {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleDateString('vi-VN', { year: 'numeric', month: '2-digit', day: '2-digit' });
};

onMounted(() => {
  void loadProjects();
});
</script>
