import { createRouter, createWebHistory } from 'vue-router';
import AppLayout from '../layout/AppLayout.vue';
import { useAuthStore } from '@/store/auth.store';

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/',
      component: AppLayout,
      redirect: '/chat',
      children: [
        {
          path: '/chat',
          name: 'Chat',
          component: () => import('../views/chat/ChatView.vue')
        },
        {
          path: '/documents',
          name: 'Documents',
          component: () => import('../views/documents/DocumentView.vue'),
          meta: { requiresAuth: true }
        },
        {
          path: '/memory',
          name: 'Memory',
          component: () => import('../views/memory/MemoryView.vue'),
          meta: { requiresAuth: true }
        },
        {
          path: '/admin/users',
          name: 'AdminUsers',
          component: () => import('../views/admin/UserManager.vue'),
          meta: { requiresAuth: true, requiresAdmin: true }
        }
      ]
    },
    {
      path: '/login',
      name: 'Login',
      component: () => import('../views/auth/LoginView.vue'),
      meta: { requiresAuth: false }
    },
    {
      path: '/share/:token',
      name: 'SharedChat',
      component: () => import('../views/share/ShareView.vue'),
      // PUBLIC read-only page for shared conversations. `skipAuthCheck` keeps
      // the guard from calling /api/auth/me for anonymous viewers: that call
      // goes through `http`, whose failed-refresh handling would hard-redirect
      // them to /login before the shared content could render.
      meta: { requiresAuth: false, skipAuthCheck: true }
    },
    {
      path: '/widget/chat',
      name: 'WidgetChat',
      component: () => import('../views/widget/ChatWidgetView.vue'),
      // Public widget credentials are not implemented yet. Do not expose an
      // apparently anonymous route backed by authenticated chat APIs.
      meta: { requiresAuth: true }
    }
  ]
});

let isAuthChecked = false;

router.beforeEach(async (to, _from, next) => {
  const authStore = useAuthStore();
  const requiresAuth = to.matched.some(record => record.meta.requiresAuth !== false);
  const requiresAdmin = to.matched.some(record => record.meta.requiresAdmin === true);
  const skipAuthCheck = to.matched.some(record => record.meta.skipAuthCheck === true);

  if (!isAuthChecked && !skipAuthCheck) {
    await authStore.fetchCurrentUser();
    isAuthChecked = true;
  }

  if (requiresAuth && !authStore.isAuthenticated) {
    next('/login');
  } else if (to.path === '/login' && authStore.isAuthenticated) {
    next('/');
  } else if (requiresAdmin && authStore.currentUser?.role !== 'Admin') {
    next('/'); // Không có quyền, chuyển về trang chủ
  } else {
    next();
  }
});

export default router;
