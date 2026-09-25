<template>
  <div class="flex-1 overflow-y-auto bg-gray-50 p-4 md:p-6">
    <div class="max-w-6xl mx-auto">
      <header class="mb-4 flex items-center gap-4">
        <div class="w-10 h-10 rounded-lg bg-gov-blue-dark flex items-center justify-center shadow-md flex-shrink-0">
          <i class="pi pi-users text-white text-sm" aria-hidden="true"></i>
        </div>
        <div class="flex-1">
          <h1 class="text-xl font-bold text-gray-900 tracking-tight">Quản lý tài khoản</h1>
          <p class="text-sm text-gray-500">Cấp tài khoản, phân quyền và khóa/mở khóa người dùng trong tenant.</p>
        </div>
      </header>

      <div v-if="list.error.value" role="alert" class="mb-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        {{ list.error.value }}
      </div>

      <div class="mb-3 flex flex-wrap items-center justify-between gap-3">
        <span class="relative">
          <i class="pi pi-search absolute left-3 top-1/2 -translate-y-1/2 text-gray-400 text-xs" aria-hidden="true"></i>
          <InputText v-model="list.search.value" placeholder="Tìm theo email, họ tên…" class="!pl-8 w-72 max-w-full" aria-label="Tìm kiếm người dùng" @input="list.onSearchInput" />
        </span>
        <div class="flex items-center gap-2">
          <Button icon="pi pi-refresh" severity="secondary" outlined :loading="list.loading.value" aria-label="Tải lại" @click="list.reload" />
          <Button label="Thêm người dùng" icon="pi pi-user-plus" @click="openNewDialog" />
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
        :rowHover="true"
        class="bg-white rounded-lg border border-gray-200 overflow-hidden"
        @page="list.onPage">
        <template #empty>
          <div class="p-8 text-center text-gray-500 text-sm">
            <i class="pi pi-users text-3xl text-gray-300 block mb-3" aria-hidden="true"></i>
            {{ list.search.value ? 'Không tìm thấy người dùng phù hợp.' : 'Chưa có người dùng nào.' }}
          </div>
        </template>

        <Column field="email" header="Email" style="min-width: 14rem">
          <template #body="{ data }">
            <span class="font-medium text-gray-900">{{ data.email }}</span>
            <div class="text-xs text-gray-400 mt-0.5">Tạo {{ absoluteDate(data.createdAt) }}</div>
          </template>
        </Column>
        <Column field="fullName" header="Họ và Tên" style="min-width: 11rem" />
        <Column field="tenantName" header="Tenant" style="min-width: 12rem">
          <template #body="{ data }">
            <span class="text-sm text-gray-600">{{ data.tenantName || '—' }}</span>
          </template>
        </Column>
        <Column field="role" header="Quyền" style="min-width: 7rem">
          <template #body="{ data }">
            <!-- Quyền là phân loại, không phải lỗi: Admin = info (xanh đậm), Member = secondary (xám). -->
            <Tag :value="data.role" :severity="data.role === 'Admin' ? 'info' : 'secondary'" />
          </template>
        </Column>
        <Column field="isActive" header="Trạng thái" style="min-width: 9rem">
          <template #body="{ data }">
            <div class="flex flex-wrap gap-1">
              <Tag :value="data.isActive ? 'Hoạt động' : 'Đã khóa'" :severity="data.isActive ? 'success' : 'danger'" />
              <Tag v-if="isLockedOut(data)" value="Khóa tạm (đăng nhập sai)" severity="warn" v-tooltip.top="lockoutHint(data)" />
            </div>
          </template>
        </Column>
        <Column header="Thao tác" style="min-width: 8rem">
          <template #body="{ data }">
            <div class="flex items-center gap-1">
              <Button icon="pi pi-pencil" text rounded severity="secondary" :aria-label="`Sửa quyền của ${data.email}`" v-tooltip.top="'Sửa quyền'" @click="editUser(data)" />
              <Button
                :icon="data.isActive ? 'pi pi-lock' : 'pi pi-lock-open'"
                text rounded
                :severity="data.isActive ? 'danger' : 'success'"
                :aria-label="`${data.isActive ? 'Khóa' : 'Mở khóa'} tài khoản ${data.email}`"
                v-tooltip.top="data.isActive ? 'Khóa tài khoản' : 'Mở khóa tài khoản'"
                @click="confirmToggle(data)" />
            </div>
          </template>
        </Column>
      </DataTable>

      <Dialog v-model:visible="userDialog" modal :header="isEditing ? 'Chỉnh sửa quyền' : 'Tạo tài khoản mới'" class="w-[30rem] max-w-[95vw]">
        <form class="grid gap-3" @submit.prevent="saveUser">
          <div v-if="formError" role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
            {{ formError }}
          </div>
          <div class="grid gap-1">
            <label for="user-email" class="text-sm font-medium text-gray-700">Email <span v-if="!isEditing" class="text-red-500">*</span></label>
            <InputText id="user-email" v-model.trim="userForm.email" type="email" :required="!isEditing" :disabled="isEditing" autofocus class="w-full" />
          </div>
          <div v-if="!isEditing" class="grid gap-1">
            <label for="user-password" class="text-sm font-medium text-gray-700">Mật khẩu <span class="text-red-500">*</span></label>
            <InputText id="user-password" v-model="userForm.password" type="password" required autocomplete="new-password" class="w-full" />
            <p class="text-xs text-gray-500">Tối thiểu 12 ký tự, gồm chữ hoa, chữ thường và số.</p>
          </div>
          <div class="grid gap-1">
            <label for="user-fullname" class="text-sm font-medium text-gray-700">Họ và Tên <span v-if="!isEditing" class="text-red-500">*</span></label>
            <InputText id="user-fullname" v-model.trim="userForm.fullName" :required="!isEditing" :disabled="isEditing" class="w-full" />
          </div>
          <div v-if="!isEditing" class="grid gap-1">
            <label for="user-tenant" class="text-sm font-medium text-gray-700">Tenant</label>
            <Select
              v-model="userForm.tenantId"
              :options="tenantOptions"
              optionLabel="name"
              optionValue="id"
              inputId="user-tenant"
              placeholder="Chọn tenant cho tài khoản"
              class="w-full"
              filter
              filterPlaceholder="Tìm tenant…"
            />
            <p class="text-xs text-gray-500">Bỏ chọn → user vào tenant mặc định của bạn.</p>
          </div>
          <div class="grid gap-1">
            <label for="user-role" class="text-sm font-medium text-gray-700">Quyền hạn</label>
            <Select v-model="userForm.role" :options="roleOptions" optionLabel="label" optionValue="value" inputId="user-role" class="w-full" />
            <p v-if="isEditing" class="text-xs text-gray-500">Email/họ tên là định danh tài khoản — chỉ quyền hạn chỉnh được tại đây.</p>
          </div>
        </form>
        <template #footer>
          <Button label="Hủy" severity="secondary" outlined @click="userDialog = false" />
          <Button :label="isEditing ? 'Lưu quyền' : 'Tạo tài khoản'" icon="pi pi-check" :loading="saving" @click="saveUser" />
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
import { errorMessage, readApiError } from '@/api/errors';
import { useServerPage } from '@/composables/useServerPage';

interface User {
  id: string;
  email: string;
  fullName: string;
  role: string;
  isActive: boolean;
  createdAt: string;
  failedLoginAttempts: number;
  lockoutEnd?: string | null;
  tenantId?: string;
  tenantName?: string;
}

interface TenantOption { id: string; name: string }

const confirm = useConfirm();
const toast = useToast();
const list = useServerPage<User>('/api/users', { errorLabel: 'danh sách người dùng' });

const roleOptions = [
  { label: 'Member', value: 'Member' },
  { label: 'Admin', value: 'Admin' }
];

const absoluteDate = (value: string): string => {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleDateString('vi-VN');
};

const isLockedOut = (user: User): boolean =>
  !!user.lockoutEnd && new Date(user.lockoutEnd).getTime() > Date.now();
const lockoutHint = (user: User): string =>
  `Sai mật khẩu ${user.failedLoginAttempts} lần — tự mở khóa lúc ${new Date(user.lockoutEnd as string).toLocaleTimeString('vi-VN')}`;

const userDialog = ref(false);
const isEditing = ref(false);
const saving = ref(false);
const formError = ref('');
const tenantOptions = ref<TenantOption[]>([]);
const userForm = ref({ id: '', email: '', password: '', fullName: '', role: 'Member', tenantId: '' as string });

const openNewDialog = () => {
  isEditing.value = false;
  userForm.value = { id: '', email: '', password: '', fullName: '', role: 'Member', tenantId: '' };
  formError.value = '';
  userDialog.value = true;
  // Danh sách tenant để gán user mới (admin đa tenant). Lười tải 1 lần/phiên.
  if (tenantOptions.value.length === 0) {
    void http.get('/api/tenants/options').then(async (res) => {
      if (res.ok) tenantOptions.value = await res.json();
    });
  }
};

const editUser = (user: User) => {
  isEditing.value = true;
  userForm.value = { id: user.id, email: user.email, password: '', fullName: user.fullName, role: user.role, tenantId: user.tenantId ?? '' };
  formError.value = '';
  userDialog.value = true;
};

const saveUser = async () => {
  formError.value = '';
  saving.value = true;
  try {
    if (isEditing.value) {
      const res = await http.put(`/api/users/${userForm.value.id}/role`, { role: userForm.value.role });
      if (!res.ok) {
        formError.value = await readApiError(res, 'Không thể cập nhật quyền người dùng');
        return;
      }
      toast.add({ severity: 'success', summary: 'Đã cập nhật quyền', detail: userForm.value.email, life: 3000 });
    } else {
      if (!userForm.value.email.trim() || !userForm.value.password || !userForm.value.fullName.trim()) {
        formError.value = 'Vui lòng nhập đủ email, mật khẩu và họ tên.';
        return;
      }
      const res = await http.post('/api/users', {
        ...userForm.value,
        // Rỗng → API tự gán vào tenant của admin.
        tenantId: userForm.value.tenantId || undefined
      });
      if (!res.ok) {
        formError.value = await readApiError(res, 'Không thể tạo người dùng');
        return;
      }
      toast.add({ severity: 'success', summary: 'Đã tạo tài khoản', detail: userForm.value.email, life: 3000 });
    }
    userDialog.value = false;
    await list.reload();
  } catch (error) {
    formError.value = errorMessage(error, 'Có lỗi xảy ra khi lưu người dùng.');
  } finally {
    saving.value = false;
  }
};

const confirmToggle = (user: User) => {
  const locking = user.isActive;
  confirm.require({
    header: locking ? 'Khóa tài khoản' : 'Mở khóa tài khoản',
    message: locking
      ? `Khóa "${user.email}"? Người dùng sẽ không đăng nhập được và phiên hiện tại bị thu hồi.`
      : `Mở khóa "${user.email}"?`,
    icon: 'pi pi-exclamation-triangle',
    acceptLabel: locking ? 'Khóa' : 'Mở khóa',
    rejectLabel: 'Hủy',
    acceptProps: { severity: locking ? 'danger' : 'success' },
    rejectProps: { severity: 'secondary', outlined: true },
    accept: () => { void performToggle(user); }
  });
};

const performToggle = async (user: User) => {
  try {
    const res = await http.put(`/api/users/${user.id}/toggle-status`);
    if (res.ok) {
      toast.add({ severity: 'success', summary: user.isActive ? 'Đã khóa tài khoản' : 'Đã mở khóa tài khoản', detail: user.email, life: 3000 });
      await list.reload();
    } else {
      toast.add({ severity: 'error', summary: 'Không thể cập nhật trạng thái', detail: await readApiError(res, 'Không thể cập nhật trạng thái người dùng'), life: 6000 });
    }
  } catch (error) {
    toast.add({ severity: 'error', summary: 'Không thể cập nhật trạng thái', detail: errorMessage(error, 'Không thể cập nhật trạng thái người dùng.'), life: 6000 });
  }
};

onMounted(() => { void list.load(); });
</script>
