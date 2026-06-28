import { defineStore } from 'pinia';
import { ref } from 'vue';
import { http } from '@/api/http';

export const useAuthStore = defineStore('auth', () => {
  const currentUser = ref<any>(null);
  const isAuthenticated = ref(false);

  async function fetchCurrentUser() {
    try {
      const response = await http.get('/api/auth/me');
      if (response.ok) {
        currentUser.value = await response.json();
        isAuthenticated.value = true;
        return true;
      }
    } catch (error) {
      console.error('Failed to fetch user', error);
    }
    
    currentUser.value = null;
    isAuthenticated.value = false;
    return false;
  }

  async function logout() {
    try {
      await http.post('/api/auth/logout');
    } catch (e) {
      // ignore
    }
    currentUser.value = null;
    isAuthenticated.value = false;
  }

  return { currentUser, isAuthenticated, fetchCurrentUser, logout };
});
