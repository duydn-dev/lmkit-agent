<template>
  <div class="flex-1 overflow-y-auto bg-gray-50 p-4 md:p-6">
    <div class="max-w-5xl mx-auto">
      <header class="mb-4 flex items-center gap-4">
        <div class="w-10 h-10 rounded-lg bg-gov-blue-dark flex items-center justify-center shadow-md flex-shrink-0">
          <i class="pi pi-building text-white text-sm" aria-hidden="true"></i>
        </div>
        <div>
          <h1 class="text-xl font-bold text-gray-900 tracking-tight">Quản lý Tenant</h1>
          <p class="text-sm text-gray-500">Đơn vị/tổ chức sử dụng hệ thống. Chỉ tenant RỖNG mới xóa được — không bao giờ cascade dữ liệu.</p>
        </div>
      </header>

      <div v-if="list.error.value" role="alert" class="mb-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        {{ list.error.value }}
      </div>

      <div class="mb-3 flex flex-wrap items-center justify-between gap-3">
        <span class="relative">
          <i class="pi pi-search absolute left-3 top-1/2 -translate-y-1/2 text-gray-400 text-xs" aria-hidden="true"></i>
          <InputText v-model="list.search.value" placeholder="Tìm theo tên tenant…" class="!pl-8 w-72 max-w-full" aria-label="Tìm kiếm tenant" @input="list.onSearchInput" />
        </span>
        <div class="flex items-center gap-2">
          <Button icon="pi pi-refresh" severity="secondary" outlined :loading="list.loading.value" aria-label="Tải lại" @click="list.reload" />
          <Button label="Thêm tenant" icon="pi pi-plus" @click="openCreate" />
        </div>
      </div>

      <DataTable
        :value="list.rows.value"
        lazy
        paginator
        :rows="list.pageSize.value"
        :first="list.first.value"
        :totalRecords="list.totalRecords.value"
        :loading="list.loading.value"
        :rowsPerPageOptions="[10, 20, 50]"
        dataKey="id"
        class="bg-white rounded-lg border border-gray-200 overflow-hidden"
        @page="list.onPage">
        <template #empty>
          <div class="p-8 text-center text-gray-500 text-sm">
            <i class="pi pi-building text-3xl text-gray-300 block mb-3" aria-hidden="true"></i>
            {{ list.search.value ? 'Không tìm thấy tenant phù hợp.' : 'Chưa có tenant nào.' }}
          </div>
        </template>

        <Column field="name" header="Tên tenant">
          <template #body="{ data }">
            <div class="flex items-center gap-3">
              <div class="w-9 h-9 rounded-lg border border-gray-200 bg-gray-50 flex items-center justify-center overflow-hidden flex-shrink-0">
                <img v-if="data.hasLogo" :src="rowLogoUrl(data)" :alt="`Logo ${data.name}`" class="w-full h-full object-contain" />
                <i v-else class="pi pi-building text-gray-300 text-sm" aria-hidden="true"></i>
              </div>
              <div class="min-w-0">
                <div class="font-semibold text-gray-900 text-sm truncate">{{ data.name }}</div>
                <div class="text-xs text-gray-400 mt-0.5 truncate">
                  <span v-if="data.agentDisplayName" class="text-gray-500">🤖 {{ data.agentDisplayName }} · </span>Tạo {{ absoluteDate(data.createdAt) }}
                </div>
              </div>
            </div>
          </template>
        </Column>
        <Column field="userCount" header="Người dùng" :style="{ width: '130px' }">
          <template #body="{ data }"><Tag :value="`${data.userCount} user`" severity="secondary" /></template>
        </Column>
        <Column field="databaseConnectionCount" header="Kết nối CSDL" :style="{ width: '140px' }">
          <template #body="{ data }"><Tag :value="`${data.databaseConnectionCount} kết nối`" severity="info" /></template>
        </Column>
        <Column header="Thao tác" :style="{ width: '120px' }">
          <template #body="{ data }">
            <div class="flex items-center gap-1">
              <Button icon="pi pi-pencil" text rounded severity="secondary" aria-label="Đổi tên tenant" @click="openEdit(data)" />
              <Button icon="pi pi-trash" text rounded severity="danger" :loading="deletingId === data.id" aria-label="Xóa tenant" @click="confirmDelete(data)" />
            </div>
          </template>
        </Column>
      </DataTable>

      <Dialog v-model:visible="dialogVisible" modal :header="editingId ? 'Sửa tenant' : 'Thêm tenant'" class="w-[30rem] max-w-[95vw]">
        <form class="grid gap-3" @submit.prevent="save">
          <div v-if="formError" role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
            {{ formError }}
          </div>
          <div class="grid gap-1">
            <label for="tenant-name" class="text-sm font-medium text-gray-700">Tên tenant <span class="text-red-500">*</span></label>
            <InputText id="tenant-name" v-model="form.name" required maxlength="200" placeholder="Ví dụ: Chi cục Bảo vệ môi trường" class="w-full" autofocus />
          </div>
          <div class="grid gap-1">
            <label for="tenant-agent" class="text-sm font-medium text-gray-700">Tên trợ lý AI</label>
            <InputText id="tenant-agent" v-model="form.agentDisplayName" maxlength="100" placeholder="Ví dụ: Trợ lý CILA (để trống dùng mặc định)" class="w-full" />
            <small class="text-xs text-gray-400">Hiển thị trên header và trong khung chat cho người dùng thuộc tenant này.</small>
          </div>

          <div class="grid gap-1.5">
            <span class="text-sm font-medium text-gray-700">Logo đơn vị</span>
            <template v-if="editingId">
              <div class="flex items-center gap-3">
                <div class="w-14 h-14 rounded-lg border border-gray-200 bg-gray-50 flex items-center justify-center overflow-hidden flex-shrink-0">
                  <img v-if="logoPreviewUrl" :src="logoPreviewUrl" alt="Logo hiện tại" class="w-full h-full object-contain" />
                  <i v-else class="pi pi-image text-gray-300 text-xl" aria-hidden="true"></i>
                </div>
                <div class="flex flex-col gap-1.5 min-w-0">
                  <div class="flex items-center gap-2">
                    <Button label="Tải logo" icon="pi pi-upload" size="small" outlined :loading="logoBusy" @click="triggerLogoPick" />
                    <Button v-if="editingHasLogo" label="Gỡ" icon="pi pi-trash" size="small" severity="danger" text :loading="logoBusy" @click="removeLogo" />
                  </div>
                  <small class="text-xs text-gray-400">PNG, JPG, WEBP, GIF, BMP · tối đa 2MB.</small>
                </div>
                <input ref="logoInput" type="file" accept="image/png,image/jpeg,image/webp,image/gif,image/bmp" class="hidden" @change="onLogoChange" />
              </div>
              <small v-if="logoError" role="alert" class="text-xs text-red-600">{{ logoError }}</small>
            </template>
            <small v-else class="text-xs text-gray-400">Lưu tenant trước, rồi mở lại để tải logo.</small>
          </div>
        </form>
        <template #footer>
          <Button label="Hủy" severity="secondary" outlined @click="dialogVisible = false" />
          <Button :label="editingId ? 'Lưu' : 'Thêm'" icon="pi pi-check" :loading="saving" @click="save" />
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
import { useServerPage } from '@/composables/useServerPage';

interface TenantRow {
  id: string;
  name: string;
  agentDisplayName?: string | null;
  hasLogo?: boolean;
  logoUpdatedAt?: string | null;
  createdAt: string;
  userCount: number;
  databaseConnectionCount: number;
}

const MAX_LOGO_BYTES = 2 * 1024 * 1024; // 2 MB — khớp giới hạn phía server
const ALLOWED_LOGO_TYPES = ['image/png', 'image/jpeg', 'image/webp', 'image/gif', 'image/bmp'];

const confirm = useConfirm();
const toast = useToast();
const list = useServerPage<TenantRow>(ApiFactory.TENANTS.BASE, { errorLabel: 'danh sách tenant' });

const absoluteDate = (value: string): string => {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleDateString('vi-VN');
};

const dialogVisible = ref(false);
const editingId = ref<string | null>(null);
const saving = ref(false);
const formError = ref('');
const form = ref<{ name: string; agentDisplayName: string }>({ name: '', agentDisplayName: '' });

// --- Logo (chỉ thao tác khi tenant đã tồn tại) ------------------------------
const logoInput = ref<HTMLInputElement | null>(null);
const logoBusy = ref(false);
const logoError = ref('');
const editingHasLogo = ref(false);
/** Đổi khi upload/xóa logo → ép <img> tải lại (bỏ cache) trong lúc dialog đang mở. */
const logoCacheBust = ref(Date.now());

const logoPreviewUrl = computed(() =>
  editingId.value && editingHasLogo.value
    ? `${ApiFactory.TENANTS.LOGO(editingId.value)}?v=${logoCacheBust.value}`
    : '');

/** URL logo cho thumbnail ở bảng — cache-buster theo mốc cập nhật của chính row. */
const rowLogoUrl = (row: TenantRow) =>
  `${ApiFactory.TENANTS.LOGO(row.id)}?v=${row.logoUpdatedAt ? Date.parse(row.logoUpdatedAt) : 0}`;

const openCreate = () => {
  editingId.value = null;
  form.value = { name: '', agentDisplayName: '' };
  editingHasLogo.value = false;
  logoError.value = '';
  formError.value = '';
  dialogVisible.value = true;
};

const openEdit = (tenant: TenantRow) => {
  editingId.value = tenant.id;
  form.value = { name: tenant.name, agentDisplayName: tenant.agentDisplayName ?? '' };
  editingHasLogo.value = !!tenant.hasLogo;
  logoCacheBust.value = tenant.logoUpdatedAt ? Date.parse(tenant.logoUpdatedAt) : Date.now();
  logoError.value = '';
  formError.value = '';
  dialogVisible.value = true;
};

const save = async () => {
  formError.value = '';
  const name = form.value.name.trim();
  if (!name) { formError.value = 'Vui lòng nhập tên tenant.'; return; }
  const payload = { name, agentDisplayName: form.value.agentDisplayName.trim() || null };
  saving.value = true;
  try {
    const response = editingId.value
      ? await http.put(ApiFactory.TENANTS.BY_ID(editingId.value), payload)
      : await http.post(ApiFactory.TENANTS.BASE, payload);
    if (!response.ok) {
      formError.value = await readApiError(response, 'Không thể lưu tenant');
      return;
    }
    dialogVisible.value = false;
    toast.add({ severity: 'success', summary: editingId.value ? 'Đã lưu tenant' : 'Đã thêm tenant', life: 3000 });
    await list.reload();
  } catch (cause) {
    formError.value = errorMessage(cause, 'Không thể lưu tenant.');
  } finally {
    saving.value = false;
  }
};

const triggerLogoPick = () => logoInput.value?.click();

const onLogoChange = async (event: Event) => {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  input.value = ''; // cho phép chọn lại đúng file vừa chọn
  if (!file || !editingId.value) return;

  logoError.value = '';
  if (!ALLOWED_LOGO_TYPES.includes(file.type)) {
    logoError.value = 'Định dạng phải là PNG, JPG, WEBP, GIF hoặc BMP.';
    return;
  }
  if (file.size > MAX_LOGO_BYTES) {
    logoError.value = 'Logo tối đa 2MB.';
    return;
  }

  logoBusy.value = true;
  try {
    const fd = new FormData();
    fd.append('logo', file);
    const response = await http.post(ApiFactory.TENANTS.LOGO(editingId.value), fd);
    if (response.ok) {
      editingHasLogo.value = true;
      logoCacheBust.value = Date.now();
      toast.add({ severity: 'success', summary: 'Đã cập nhật logo', life: 3000 });
      await list.reload();
    } else {
      logoError.value = await readApiError(response, 'Không thể tải logo');
    }
  } catch (cause) {
    logoError.value = errorMessage(cause, 'Không thể tải logo.');
  } finally {
    logoBusy.value = false;
  }
};

const removeLogo = async () => {
  if (!editingId.value) return;
  logoBusy.value = true;
  logoError.value = '';
  try {
    const response = await http.delete(ApiFactory.TENANTS.LOGO(editingId.value));
    if (response.ok) {
      editingHasLogo.value = false;
      logoCacheBust.value = Date.now();
      toast.add({ severity: 'success', summary: 'Đã gỡ logo', life: 3000 });
      await list.reload();
    } else {
      logoError.value = await readApiError(response, 'Không thể gỡ logo');
    }
  } catch (cause) {
    logoError.value = errorMessage(cause, 'Không thể gỡ logo.');
  } finally {
    logoBusy.value = false;
  }
};

const deletingId = ref<string | null>(null);

const confirmDelete = (tenant: TenantRow) => {
  confirm.require({
    header: 'Xóa tenant',
    message: `Xóa tenant "${tenant.name}"? Chỉ xóa được khi tenant không còn user, phiên chat, API key hay kết nối CSDL.`,
    icon: 'pi pi-exclamation-triangle',
    acceptLabel: 'Xóa',
    rejectLabel: 'Hủy',
    acceptProps: { severity: 'danger' },
    rejectProps: { severity: 'secondary', outlined: true },
    accept: () => { void performDelete(tenant); }
  });
};

const performDelete = async (tenant: TenantRow) => {
  deletingId.value = tenant.id;
  try {
    const response = await http.delete(ApiFactory.TENANTS.BY_ID(tenant.id));
    if (response.ok) {
      toast.add({ severity: 'success', summary: 'Đã xóa tenant', detail: tenant.name, life: 3000 });
      await list.reload();
    } else {
      toast.add({ severity: 'error', summary: 'Không thể xóa', detail: await readApiError(response, 'Không thể xóa tenant'), life: 6000 });
    }
  } catch (cause) {
    toast.add({ severity: 'error', summary: 'Không thể xóa', detail: errorMessage(cause, 'Không thể xóa tenant.'), life: 6000 });
  } finally {
    deletingId.value = null;
  }
};

onMounted(() => { void list.load(); });
</script>
