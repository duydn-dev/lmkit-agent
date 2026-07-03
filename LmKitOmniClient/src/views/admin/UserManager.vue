<template>
  <div class="flex-1 overflow-y-auto bg-gray-50 p-6">
    <div class="max-w-6xl mx-auto">
      <div class="flex justify-between items-center mb-6">
        <div>
          <h1 class="text-2xl font-bold text-gray-900">Quản lý Người dùng</h1>
          <p class="text-gray-500 mt-1">Cấp tài khoản và phân quyền trong hệ thống</p>
        </div>
        <button @click="openNewDialog" class="bg-chatgpt-brand hover:bg-chatgpt-brand/90 text-white px-4 py-2 rounded-md font-medium transition-colors shadow-sm flex items-center gap-2">
          <i class="pi pi-user-plus"></i>
          Thêm người dùng
        </button>
      </div>

      <!-- Data Table -->
      <div class="bg-white rounded-xl shadow-sm border border-gray-200 overflow-hidden">
        <DataTable :value="users" :loading="loading" :paginator="true" :rows="10" 
                  dataKey="id" class="p-datatable-sm"
                  :rowHover="true" filterDisplay="menu" responsiveLayout="scroll"
                  emptyMessage="Không tìm thấy người dùng nào.">
          
          <Column field="email" header="Email" :sortable="true" style="min-width: 14rem">
            <template #body="{ data }">
              <span class="font-medium text-gray-900">{{ data.email }}</span>
            </template>
          </Column>
          
          <Column field="fullName" header="Họ và Tên" :sortable="true" style="min-width: 12rem"></Column>
          
          <Column field="role" header="Quyền" :sortable="true" style="min-width: 10rem">
            <template #body="{ data }">
              <span :class="['px-2 py-1 rounded-full text-xs font-medium', 
                data.role === 'Admin' ? 'bg-purple-100 text-purple-700' : 'bg-blue-100 text-blue-700']">
                {{ data.role }}
              </span>
            </template>
          </Column>

          <Column field="isActive" header="Trạng thái" :sortable="true" style="min-width: 8rem">
            <template #body="{ data }">
              <span v-if="data.isActive" class="text-green-600 flex items-center gap-1.5"><i class="pi pi-check-circle text-xs"></i> Hoạt động</span>
              <span v-else class="text-red-500 flex items-center gap-1.5"><i class="pi pi-lock text-xs"></i> Đã khóa</span>
            </template>
          </Column>

          <Column header="Thao tác" :exportable="false" style="min-width: 8rem" alignFrozen="right" :frozen="true">
            <template #body="{ data }">
              <div class="flex gap-2">
                <button @click="editUser(data)" class="p-2 text-gray-500 hover:text-blue-600 hover:bg-blue-50 rounded transition-colors" title="Sửa quyền">
                  <i class="pi pi-pencil"></i>
                </button>
                <button @click="toggleUserStatus(data)" :class="['p-2 rounded transition-colors', data.isActive ? 'text-gray-500 hover:text-red-600 hover:bg-red-50' : 'text-gray-500 hover:text-green-600 hover:bg-green-50']" :title="data.isActive ? 'Khóa tài khoản' : 'Mở khóa'">
                  <i :class="data.isActive ? 'pi pi-lock' : 'pi pi-lock-open'"></i>
                </button>
              </div>
            </template>
          </Column>
        </DataTable>
      </div>

      <!-- User Dialog -->
      <Dialog v-model:visible="userDialog" :style="{width: '450px'}" :header="isEditing ? 'Chỉnh sửa Quyền' : 'Tạo Tài khoản mới'" :modal="true" class="p-fluid">
        <div class="flex flex-col gap-4 mt-4">
          <div class="flex flex-col gap-2">
            <label for="email" class="font-medium text-sm text-gray-700">Email</label>
            <InputText id="email" v-model.trim="userForm.email" required="true" autofocus :disabled="isEditing" />
          </div>
          
          <div class="flex flex-col gap-2" v-if="!isEditing">
            <label for="password" class="font-medium text-sm text-gray-700">Mật khẩu</label>
            <InputText id="password" type="password" v-model="userForm.password" required="true" />
          </div>

          <div class="flex flex-col gap-2">
            <label for="fullName" class="font-medium text-sm text-gray-700">Họ và Tên</label>
            <InputText id="fullName" v-model.trim="userForm.fullName" required="true" :disabled="isEditing" />
          </div>

          <div class="flex flex-col gap-2">
            <label for="role" class="font-medium text-sm text-gray-700">Quyền hạn</label>
            <Dropdown id="role" v-model="userForm.role" :options="roleOptions" optionLabel="label" optionValue="value" placeholder="Chọn chức vụ" />
          </div>
        </div>

        <template #footer>
          <div class="flex justify-end gap-2 mt-4">
            <button @click="hideDialog" class="px-4 py-2 text-gray-600 hover:bg-gray-100 rounded-md transition-colors font-medium">Hủy</button>
            <button @click="saveUser" class="px-4 py-2 bg-chatgpt-brand hover:bg-chatgpt-brand/90 text-white rounded-md transition-colors font-medium">Lưu lại</button>
          </div>
        </template>
      </Dialog>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { http } from '@/api/http';
import DataTable from 'primevue/datatable';
import Column from 'primevue/column';
import Dialog from 'primevue/dialog';
import InputText from 'primevue/inputtext';
import Dropdown from 'primevue/dropdown';

interface User {
  id: string;
  email: string;
  fullName: string;
  role: string;
  isActive: boolean;
}

const users = ref<User[]>([]);
const loading = ref(true);

const userDialog = ref(false);
const isEditing = ref(false);
const userForm = ref({
  id: '',
  email: '',
  password: '',
  fullName: '',
  role: 'Member'
});

const roleOptions = [
  { label: 'Member', value: 'Member' },
  { label: 'Admin', value: 'Admin' }
];

const loadUsers = async () => {
  loading.value = true;
  try {
    const res = await http.get('/api/users');
    if (res.ok) {
      users.value = await res.json();
    } else {
      console.error("Lỗi khi tải danh sách người dùng");
    }
  } catch (error) {
    console.error(error);
  } finally {
    loading.value = false;
  }
};

const openNewDialog = () => {
  isEditing.value = false;
  userForm.value = {
    id: '',
    email: '',
    password: '',
    fullName: '',
    role: 'Member'
  };
  userDialog.value = true;
};

const editUser = (user: User) => {
  isEditing.value = true;
  userForm.value = {
    id: user.id,
    email: user.email,
    password: '',
    fullName: user.fullName,
    role: user.role
  };
  userDialog.value = true;
};

const hideDialog = () => {
  userDialog.value = false;
};

const saveUser = async () => {
  try {
    if (isEditing.value) {
      // Chỉ cập nhật Role
      const res = await http.put(`/api/users/${userForm.value.id}/role`, { role: userForm.value.role });
      if (res.ok) {
        userDialog.value = false;
        loadUsers();
      }
    } else {
      // Tạo mới
      const res = await http.post('/api/users', userForm.value);
      if (res.ok) {
        userDialog.value = false;
        loadUsers();
      } else {
        const error = await res.json();
        alert("Lỗi: " + error.message);
      }
    }
  } catch (error) {
    console.error(error);
    alert("Có lỗi xảy ra khi lưu.");
  }
};

const toggleUserStatus = async (user: User) => {
  if (!confirm(`Bạn có chắc muốn ${user.isActive ? 'khóa' : 'mở khóa'} người dùng này không?`)) return;
  
  try {
    const res = await http.put(`/api/users/${user.id}/toggle-status`);
    if (res.ok) {
      loadUsers();
    }
  } catch (error) {
    console.error(error);
  }
};

onMounted(() => {
  loadUsers();
});
</script>
