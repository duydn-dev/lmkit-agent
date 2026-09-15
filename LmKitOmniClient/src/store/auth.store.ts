import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { http } from '@/api/http';

export type UserRole = 'Admin' | 'Member';

/** Fallback agent name when the tenant has not configured one (mirrors backend "CILA Agent"). */
export const DEFAULT_AGENT_NAME = 'CILA - AI Agent';

/**
 * Per-tenant branding carried on `GET /api/auth/me` (backend: AuthController.BuildTenantBrandingAsync).
 * `agentName`/`logoUrl` are null when the tenant has not set them, and the UI falls back to defaults.
 */
export interface TenantBranding {
  name: string;
  agentName: string | null;
  logoUrl: string | null;
}

/**
 * Shape returned by `GET /api/auth/me` (and the login response).
 * Backend source: AuthController.GetCurrentUser -> { Id, Email, FullName, Role, TenantId, Tenant }.
 * Used by the admin route guard (role check) and the app shell header.
 */
export interface User {
  id: string;
  email: string;
  fullName: string;
  /** Optional profile image; Avatar falls back to initials when absent. */
  avatarUrl?: string;
  role: UserRole;
  tenantId: string;
  /** Per-tenant branding (agent name + logo); may be absent for legacy payloads. */
  tenant?: TenantBranding | null;
}

export const useAuthStore = defineStore('auth', () => {
  const currentUser = ref<User | null>(null);
  const isAuthenticated = ref(false);

  /** Agent display name for chat + header, falling back to the system default. */
  const agentName = computed(() => currentUser.value?.tenant?.agentName?.trim() || DEFAULT_AGENT_NAME);
  /** Same-origin, cookie-authenticated tenant logo URL (with cache-buster), or null. */
  const brandLogoUrl = computed(() => currentUser.value?.tenant?.logoUrl || null);
  /** Tenant/organization display name, or empty string when unavailable. */
  const tenantName = computed(() => currentUser.value?.tenant?.name?.trim() || '');

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

  return { currentUser, isAuthenticated, agentName, brandLogoUrl, tenantName, fetchCurrentUser, logout };
});
