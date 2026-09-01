<template>
  <div class="min-h-screen flex items-center justify-center bg-chatgpt-dark p-4 font-sans relative overflow-hidden">
    <!-- Background Effects -->
    <div class="absolute top-1/4 left-1/4 w-96 h-96 bg-chatgpt-brand/20 rounded-full blur-[100px] pointer-events-none"></div>
    <div class="absolute bottom-1/4 right-1/4 w-96 h-96 bg-purple-500/20 rounded-full blur-[100px] pointer-events-none"></div>
    
    <div class="w-full max-w-md relative z-10">
      <div class="bg-white/80 backdrop-blur-xl rounded-2xl shadow-2xl border border-gray-200 p-8">
        
        <!-- Logo/Header -->
        <div class="text-center mb-8">
          <div class="w-16 h-16 rounded-full bg-chatgpt-brand flex items-center justify-center mx-auto mb-4 shadow-lg shadow-chatgpt-brand/30">
            <i class="pi pi-sparkles text-3xl text-white"></i>
          </div>
          <h1 class="text-2xl font-bold text-gray-900 mb-2">Đăng nhập vào Trợ lý AI</h1>
          <p class="text-gray-600 text-sm">Hệ thống Multi-Agent được phát triển trên LM-Kit.NET</p>
        </div>
        
        <!-- Error Message -->
        <div v-if="errorMessage" role="alert" aria-live="assertive" class="mb-6 p-3 rounded-lg bg-red-50 border border-red-200 text-red-700 text-sm text-center flex items-center justify-center gap-2">
          <i class="pi pi-exclamation-circle"></i>
          {{ errorMessage }}
        </div>

        <!-- Login Form -->
        <form @submit.prevent="handleLogin" class="space-y-5">
          <div>
            <label for="login-email" class="block text-sm font-medium text-gray-700 mb-1.5">Email / Tài khoản</label>
            <IconField>
              <InputIcon class="pi pi-user" />
              <InputText id="login-email" v-model="email" type="email" autocomplete="username" placeholder="Nhập email của bạn..." fluid required />
            </IconField>
          </div>
          
          <div>
            <label for="login-password" class="block text-sm font-medium text-gray-700 mb-1.5">Mật khẩu</label>
            <InputText id="login-password" v-model="password" type="password" autocomplete="current-password" placeholder="••••••••" fluid required />
          </div>
          
          <Button 
            type="submit" 
            :loading="isLoading" 
            label="Đăng Nhập" 
            icon="pi pi-sign-in" 
            fluid 
            severity="info" 
            class="mt-2 !min-h-11 !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800"
          />
        </form>
        
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import { useRouter } from 'vue-router';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage as toErrorMessage, readApiError } from '@/api/errors';
import { useAuthStore } from '@/store/auth.store';

const router = useRouter();
const authStore = useAuthStore();
const email = ref('');
const password = ref('');
const isLoading = ref(false);
const errorMessage = ref('');

const handleLogin = async () => {
  if (!email.value || !password.value) return;
  
  isLoading.value = true;
  errorMessage.value = '';
  
  try {
    const response = await http.post(ApiFactory.AUTH.LOGIN, { email: email.value, password: password.value });
    
    if (response.ok) {
      if (await authStore.fetchCurrentUser()) {
        router.push('/');
      } else {
        errorMessage.value = 'Đăng nhập thành công nhưng không thể thiết lập phiên làm việc.';
      }
    } else {
      errorMessage.value = await readApiError(response, 'Đăng nhập thất bại. Kiểm tra lại thông tin');
    }
  } catch (error) {
    errorMessage.value = toErrorMessage(error, 'Không thể kết nối đến máy chủ.');
  } finally {
    isLoading.value = false;
  }
};
</script>
