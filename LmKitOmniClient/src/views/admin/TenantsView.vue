<template>
  <div class="flex-1 overflow-y-auto bg-gray-50 p-4 md:p-6">
    <div class="max-w-5xl mx-auto">
      <header class="mb-4 flex items-center gap-4">
        <div class="w-10 h-10 rounded-lg bg-[--color-gov-red] flex items-center justify-center shadow-md flex-shrink-0">
          <i class="pi pi-building text-white text-sm" aria-hidden="true"></i>
        </div>
        <div>
          <h1 class="text-xl font-bold text-gray-900 tracking-tight">Tenant Management</h1>
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
            <div class="font-semibold text-gray-900 text-sm">{{ data.name }}</div>
            <div class="text-xs text-gray-400 mt-0.5">Tạo {{ absoluteDate(data.createdAt) }}</div>
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

      <Dialog v-model:visible="dialogVisible" modal :header="editingId ? 'Đổi tên tenant' : 'Thêm tenant'" class="w-[28rem] max-w-[95vw]">
        <form class="grid gap-3" @submit.prevent="save">
          <div v-if="formError" role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
            {{ formError }}
          </div>
          <div class="grid gap-1">
            <label for="tenant-name" class="text-sm font-medium text-gray-700">Tên tenant <span class="text-red-500">*</span></label>
            <InputText id="tenant-name" v-model="form.name" required maxlength="200" placeholder="Ví dụ: Chi cục Bảo vệ môi trường" class="w-full" autofocus />
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
import { onMounted, ref } from 'vue';
import { useConfirm } from 'primevue/useconfirm';
import { useToast } from 'primevue/usetoast';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import { useServerPage } from '@/composables/useServerPage';

interface TenantRow {
  id: string;
  name: string;
  createdAt: string;
  userCount: number;
  databaseConnectionCount: number;
}

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
const form = ref({ name: '' });

const openCreate = () => {
  editingId.value = null;
  form.value = { name: '' };
  formError.value = '';
  dialogVisible.value = true;
};

const openEdit = (tenant: TenantRow) => {
  editingId.value = tenant.id;
  form.value = { name: tenant.name };
  formError.value = '';
  dialogVisible.value = true;
};

const save = async () => {
  formError.value = '';
  const name = form.value.name.trim();
  if (!name) { formError.value = 'Vui lòng nhập tên tenant.'; return; }
  saving.value = true;
  try {
    const response = editingId.value
      ? await http.put(ApiFactory.TENANTS.BY_ID(editingId.value), { name })
      : await http.post(ApiFactory.TENANTS.BASE, { name });
    if (!response.ok) {
      formError.value = await readApiError(response, 'Không thể lưu tenant');
      return;
    }
    dialogVisible.value = false;
    toast.add({ severity: 'success', summary: editingId.value ? 'Đã đổi tên tenant' : 'Đã thêm tenant', life: 3000 });
    await list.reload();
  } catch (cause) {
    formError.value = errorMessage(cause, 'Không thể lưu tenant.');
  } finally {
    saving.value = false;
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
