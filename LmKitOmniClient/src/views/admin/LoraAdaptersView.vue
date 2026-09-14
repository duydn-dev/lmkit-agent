<template>
  <div class="flex-1 overflow-y-auto bg-gray-50 p-4 md:p-6">
    <div class="max-w-5xl mx-auto">
      <header class="mb-4 flex items-center gap-4">
        <div class="w-10 h-10 rounded-lg bg-[--color-gov-red] flex items-center justify-center shadow-md flex-shrink-0">
          <i class="pi pi-sliders-h text-white text-sm" aria-hidden="true"></i>
        </div>
        <div>
          <h1 class="text-xl font-bold text-gray-900 tracking-tight">LoRA Adapters</h1>
          <p class="text-sm text-gray-500">
            Adapter tinh chỉnh (fine-tune) hot-swap cho model chat. Gán adapter cho một agent trong Agent Studio.
          </p>
        </div>
      </header>

      <div v-if="pageError" role="alert" class="mb-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        {{ pageError }}
      </div>
      <div v-if="featureOff" class="mb-4 flex items-start gap-2.5 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-xs text-amber-800">
        <i class="pi pi-info-circle mt-0.5" aria-hidden="true"></i>
        <p>Tính năng LoRA đang TẮT phía máy chủ (section "Lora" trong cấu hình). Danh sách dưới đây chỉ để tham khảo cho đến khi bật.</p>
      </div>

      <div class="mb-3 flex flex-wrap items-center justify-between gap-3">
        <span class="relative">
          <i class="pi pi-search absolute left-3 top-1/2 -translate-y-1/2 text-gray-400 text-xs" aria-hidden="true"></i>
          <InputText v-model="search" placeholder="Lọc theo tên adapter…" class="!pl-8 w-72 max-w-full" aria-label="Lọc adapter" />
        </span>
        <div class="flex items-center gap-2">
          <Button icon="pi pi-refresh" severity="secondary" outlined :loading="loading" aria-label="Tải lại" @click="load" />
          <Button label="Tải lên adapter" icon="pi pi-upload" @click="openUpload" />
        </div>
      </div>

      <!-- Số adapter mỗi tenant nhỏ (file .gguf LoRA ~MB) → phân trang client-side là đủ -->
      <DataTable
        :value="filteredAdapters"
        paginator
        :rows="10"
        :rowsPerPageOptions="[10, 20, 50]"
        :loading="loading"
        dataKey="id"
        class="bg-white rounded-lg border border-gray-200 overflow-hidden">
        <template #empty>
          <div class="p-8 text-center text-gray-500 text-sm">
            <i class="pi pi-sliders-h text-3xl text-gray-300 block mb-3" aria-hidden="true"></i>
            {{ search ? 'Không có adapter phù hợp.' : 'Chưa có adapter nào được đăng ký.' }}
          </div>
        </template>

        <Column field="name" header="Tên adapter">
          <template #body="{ data }">
            <div class="font-semibold text-gray-900 text-sm">{{ data.name }}</div>
            <div v-if="data.description" class="text-xs text-gray-500 mt-0.5 max-w-72 truncate" :title="data.description">{{ data.description }}</div>
          </template>
        </Column>
        <Column field="targetModelId" header="Model đích">
          <template #body="{ data }">
            <Tag v-if="data.targetModelId" :value="data.targetModelId" severity="info" />
            <span v-else class="text-xs text-gray-400">Mọi model</span>
          </template>
        </Column>
        <Column field="scale" header="Scale" :style="{ width: '90px' }">
          <template #body="{ data }"><span class="text-sm tabular-nums">{{ data.scale.toFixed(2) }}</span></template>
        </Column>
        <Column field="fileSizeBytes" header="Kích thước" :style="{ width: '110px' }">
          <template #body="{ data }"><span class="text-sm tabular-nums">{{ formatBytes(data.fileSizeBytes) }}</span></template>
        </Column>
        <Column field="isActive" header="Trạng thái" :style="{ width: '110px' }">
          <template #body="{ data }">
            <Tag :value="data.isActive ? 'Kích hoạt' : 'Tạm tắt'" :severity="data.isActive ? 'success' : 'secondary'" />
          </template>
        </Column>
        <Column header="Thao tác" :style="{ width: '120px' }">
          <template #body="{ data }">
            <div class="flex items-center gap-1">
              <Button icon="pi pi-pencil" text rounded severity="secondary" aria-label="Sửa adapter" @click="openEdit(data)" />
              <Button icon="pi pi-trash" text rounded severity="danger" :loading="deletingId === data.id" aria-label="Xóa adapter" @click="confirmDelete(data)" />
            </div>
          </template>
        </Column>
      </DataTable>

      <!-- Upload dialog -->
      <Dialog v-model:visible="uploadVisible" modal header="Tải lên LoRA adapter" class="w-[34rem] max-w-[95vw]">
        <form class="grid gap-3" @submit.prevent="upload">
          <div v-if="uploadError" role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
            {{ uploadError }}
          </div>
          <div class="grid gap-1">
            <label for="lora-name" class="text-sm font-medium text-gray-700">Tên adapter <span class="text-red-500">*</span></label>
            <InputText id="lora-name" v-model="uploadForm.name" required maxlength="100" class="w-full" />
          </div>
          <div class="grid gap-1">
            <label for="lora-desc" class="text-sm font-medium text-gray-700">Mô tả</label>
            <Textarea id="lora-desc" v-model="uploadForm.description" rows="2" maxlength="500" class="w-full" />
          </div>
          <div class="grid grid-cols-2 gap-3">
            <div class="grid gap-1">
              <label for="lora-scale" class="text-sm font-medium text-gray-700">Scale (0–2, mặc định 1)</label>
              <InputNumber v-model="uploadForm.scale" inputId="lora-scale" :min="0" :max="2" :step="0.05" :minFractionDigits="2" class="w-full" />
            </div>
            <div class="grid gap-1">
              <label for="lora-target" class="text-sm font-medium text-gray-700">Model đích (tùy chọn)</label>
              <InputText id="lora-target" v-model="uploadForm.targetModelId" placeholder="vd qwen3.5:2b" class="w-full" />
            </div>
          </div>
          <div class="grid gap-1">
            <label for="lora-file" class="text-sm font-medium text-gray-700">Tệp adapter (.gguf) <span class="text-red-500">*</span></label>
            <input id="lora-file" type="file" accept=".gguf,.bin,.safetensors" class="text-sm" @change="onFilePicked" />
            <p class="text-xs text-gray-500">Tối đa 512MB. Tệp được lưu trong kho adapter tenant-scoped của máy chủ.</p>
          </div>
        </form>
        <template #footer>
          <Button label="Hủy" severity="secondary" outlined @click="uploadVisible = false" />
          <Button label="Tải lên" icon="pi pi-upload" :loading="uploading" @click="upload" />
        </template>
      </Dialog>

      <!-- Edit dialog -->
      <Dialog v-model:visible="editVisible" modal header="Cập nhật adapter" class="w-[28rem] max-w-[95vw]">
        <form class="grid gap-3" @submit.prevent="saveEdit">
          <div v-if="editError" role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
            {{ editError }}
          </div>
          <div class="grid gap-1">
            <label for="lora-edit-name" class="text-sm font-medium text-gray-700">Tên adapter <span class="text-red-500">*</span></label>
            <InputText id="lora-edit-name" v-model="editForm.name" required maxlength="100" class="w-full" />
          </div>
          <div class="grid gap-1">
            <label for="lora-edit-scale" class="text-sm font-medium text-gray-700">Scale (0–2)</label>
            <InputNumber v-model="editForm.scale" inputId="lora-edit-scale" :min="0" :max="2" :step="0.05" :minFractionDigits="2" class="w-full" />
          </div>
          <label for="lora-edit-active" class="flex items-center gap-2 text-sm text-gray-700">
            <Checkbox inputId="lora-edit-active" v-model="editForm.isActive" binary /> Kích hoạt (agent gán adapter này sẽ dùng nó)
          </label>
        </form>
        <template #footer>
          <Button label="Hủy" severity="secondary" outlined @click="editVisible = false" />
          <Button label="Lưu" icon="pi pi-check" :loading="savingEdit" @click="saveEdit" />
        </template>
      </Dialog>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { useConfirm } from 'primevue/useconfirm';
import { useToast } from 'primevue/usetoast';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';

interface LoraAdapter {
  id: string;
  name: string;
  description?: string | null;
  scale: number;
  targetModelId?: string | null;
  fileSizeBytes: number;
  isActive: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
}

const confirm = useConfirm();
const toast = useToast();

const adapters = ref<LoraAdapter[]>([]);
const loading = ref(false);
const pageError = ref('');
const featureOff = ref(false);
const search = ref('');

const filteredAdapters = computed(() => {
  const query = search.value.trim().toLowerCase();
  if (!query) return adapters.value;
  return adapters.value.filter((a) =>
    a.name.toLowerCase().includes(query)
    || (a.description ?? '').toLowerCase().includes(query)
    || (a.targetModelId ?? '').toLowerCase().includes(query));
});

const formatBytes = (bytes: number): string => {
  if (bytes >= 1_073_741_824) return `${(bytes / 1_073_741_824).toFixed(1)} GB`;
  if (bytes >= 1_048_576) return `${(bytes / 1_048_576).toFixed(1)} MB`;
  if (bytes >= 1_024) return `${(bytes / 1_024).toFixed(0)} KB`;
  return `${bytes} B`;
};

const load = async () => {
  loading.value = adapters.value.length === 0;
  pageError.value = '';
  try {
    const response = await http.get(ApiFactory.LORA.BASE);
    if (response.ok) adapters.value = await response.json();
    else pageError.value = await readApiError(response, 'Không thể tải danh sách adapter');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể tải danh sách adapter.');
  } finally {
    loading.value = false;
  }
};

// --- Upload ------------------------------------------------------------------
const uploadVisible = ref(false);
const uploading = ref(false);
const uploadError = ref('');
const uploadForm = ref({ name: '', description: '', scale: 1 as number | null, targetModelId: '' });
const pickedFile = ref<File | null>(null);

const openUpload = () => {
  uploadForm.value = { name: '', description: '', scale: 1, targetModelId: '' };
  pickedFile.value = null;
  uploadError.value = '';
  uploadVisible.value = true;
};

const onFilePicked = (event: Event) => {
  const input = event.target as HTMLInputElement;
  pickedFile.value = input.files?.[0] ?? null;
  if (!uploadForm.value.name && pickedFile.value) {
    uploadForm.value.name = pickedFile.value.name.replace(/\.(gguf|bin|safetensors)$/i, '');
  }
};

const upload = async () => {
  uploadError.value = '';
  const name = uploadForm.value.name.trim();
  if (!name) { uploadError.value = 'Vui lòng nhập tên adapter.'; return; }
  if (!pickedFile.value) { uploadError.value = 'Vui lòng chọn tệp adapter.'; return; }

  const body = new FormData();
  body.append('name', name);
  if (uploadForm.value.description.trim()) body.append('description', uploadForm.value.description.trim());
  if (uploadForm.value.scale !== null) body.append('scale', String(uploadForm.value.scale));
  if (uploadForm.value.targetModelId.trim()) body.append('targetModelId', uploadForm.value.targetModelId.trim());
  body.append('file', pickedFile.value);

  uploading.value = true;
  try {
    const response = await http.post(ApiFactory.LORA.BASE, body);
    if (response.status === 501) {
      featureOff.value = true;
      uploadError.value = 'Tính năng LoRA đang tắt phía máy chủ.';
      return;
    }
    if (!response.ok) {
      uploadError.value = await readApiError(response, 'Không thể tải lên adapter');
      return;
    }
    uploadVisible.value = false;
    toast.add({ severity: 'success', summary: 'Đã đăng ký adapter', detail: name, life: 3000 });
    await load();
  } catch (cause) {
    uploadError.value = errorMessage(cause, 'Không thể tải lên adapter.');
  } finally {
    uploading.value = false;
  }
};

// --- Edit --------------------------------------------------------------------
const editVisible = ref(false);
const savingEdit = ref(false);
const editError = ref('');
const editForm = ref({ id: '', name: '', scale: 1 as number | null, isActive: true });

const openEdit = (adapter: LoraAdapter) => {
  editForm.value = { id: adapter.id, name: adapter.name, scale: adapter.scale, isActive: adapter.isActive };
  editError.value = '';
  editVisible.value = true;
};

const saveEdit = async () => {
  editError.value = '';
  const name = editForm.value.name.trim();
  if (!name) { editError.value = 'Vui lòng nhập tên adapter.'; return; }
  savingEdit.value = true;
  try {
    const response = await http.put(ApiFactory.LORA.BY_ID(editForm.value.id), {
      name,
      scale: editForm.value.scale,
      isActive: editForm.value.isActive
    });
    if (response.status === 501) { featureOff.value = true; editError.value = 'Tính năng LoRA đang tắt phía máy chủ.'; return; }
    if (!response.ok) {
      editError.value = await readApiError(response, 'Không thể cập nhật adapter');
      return;
    }
    editVisible.value = false;
    toast.add({ severity: 'success', summary: 'Đã cập nhật adapter', life: 3000 });
    await load();
  } catch (cause) {
    editError.value = errorMessage(cause, 'Không thể cập nhật adapter.');
  } finally {
    savingEdit.value = false;
  }
};

// --- Delete ------------------------------------------------------------------
const deletingId = ref<string | null>(null);

const confirmDelete = (adapter: LoraAdapter) => {
  confirm.require({
    header: 'Xóa adapter',
    message: `Xóa adapter "${adapter.name}"? Tệp trọng số cũng bị xóa; agent đang gán sẽ chạy không adapter.`,
    icon: 'pi pi-exclamation-triangle',
    acceptLabel: 'Xóa',
    rejectLabel: 'Hủy',
    acceptProps: { severity: 'danger' },
    rejectProps: { severity: 'secondary', outlined: true },
    accept: () => { void performDelete(adapter); }
  });
};

const performDelete = async (adapter: LoraAdapter) => {
  deletingId.value = adapter.id;
  try {
    const response = await http.delete(ApiFactory.LORA.BY_ID(adapter.id));
    if (response.ok) {
      toast.add({ severity: 'success', summary: 'Đã xóa adapter', detail: adapter.name, life: 3000 });
      await load();
    } else if (response.status === 501) {
      featureOff.value = true;
      toast.add({ severity: 'warn', summary: 'Tính năng LoRA đang tắt phía máy chủ.', life: 4000 });
    } else {
      toast.add({ severity: 'error', summary: 'Không thể xóa', detail: await readApiError(response, 'Không thể xóa adapter'), life: 6000 });
    }
  } catch (cause) {
    toast.add({ severity: 'error', summary: 'Không thể xóa', detail: errorMessage(cause, 'Không thể xóa adapter.'), life: 6000 });
  } finally {
    deletingId.value = null;
  }
};

onMounted(() => { void load(); });
</script>
