import { defineStore } from 'pinia';
import { ref } from 'vue';
import { http } from '@/api/http';

export type UserRole = 'Admin' | 'Member';

/**
 * Shape returned by `GET /api/auth/me` (and the login response).
 * Backend source: AuthController.GetCurrentUser -> { Id, Email, FullName, Role, TenantId }.
 * Used by the admin route guard (role check) and the app shell header.
 */
export interface User {
  id: string;
  email: string;
  fullName: string;
  role: UserRole;
  tenantId: string;
}

export const useAuthStore = defineStore('auth', () => {
  const currentUser = ref<User | null>(null);
  const isAuthenticated = ref(false);

  async function fetchCurrentUser() {
    try {
      const response = await http.get('/api/auth/me');
      if (response.ok) {
        currentUser.value = await response.json() as User;
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
    } catch {
      // ignore
    }
    currentUser.value = null;
    isAuthenticated.value = false;
  }

  return { currentUser, isAuthenticated, fetchCurrentUser, logout };
});
