<template>
  <div class="flex h-screen bg-gray-50 text-gray-900 font-sans">
    
    <!-- Sidebar -->
    <aside class="w-[260px] bg-white border-r border-gray-200 flex flex-col hidden md:flex transition-all duration-300" aria-label="Thanh bên ứng dụng">
      <!-- Brand -->
      <div class="px-5 py-4 border-b border-gray-100">
        <div class="flex items-center gap-3">
          <div class="w-9 h-9 rounded-lg bg-gradient-to-br from-sky-500 to-blue-600 flex items-center justify-center shadow-sm">
            <i class="pi pi-sparkles text-white text-sm"></i>
          </div>
          <div class="min-w-0">
            <div class="text-sm font-bold text-gray-900 truncate">CILA - AI Agent</div>
            <div class="text-[11px] text-gray-500 truncate leading-tight">Trung tâm Thông tin lưu trữ<br>Tài nguyên môi trường quốc gia</div>
          </div>
        </div>
      </div>

      <!-- Navigation -->
      <nav class="flex-1 overflow-y-auto py-3 px-3" aria-label="Điều hướng">
        <template v-for="group in visibleNavGroups" :key="group.title">
          <div class="text-[10px] text-gray-400 font-semibold mb-1.5 mt-4 first:mt-0 px-2 uppercase tracking-widest">{{ group.title }}</div>
          <router-link
            v-for="item in group.items"
            :key="item.to"
            :to="item.to"
            class="w-full flex items-center gap-2.5 px-2.5 py-2 hover:bg-gray-50 text-gray-600 hover:text-gray-900 text-[13px] font-medium rounded-lg transition-colors cursor-pointer group/item"
            active-class="!bg-sky-50 !text-sky-700 border-l-2 border-sky-500 -ml-[2px] pl-[12px]">
            <i :class="item.icon + ' text-gray-400 group-hover/item:text-gray-600'" aria-hidden="true"></i>
            <span>{{ item.label }}</span>
          </router-link>
        </template>
      </nav>
      
      <!-- Chat History -->
      <div class="flex-1 overflow-y-auto border-t border-gray-100">
        <div class="p-3">
          <div class="text-[10px] text-gray-400 font-semibold mb-1.5 px-2 uppercase tracking-widest flex items-center justify-between">
            <span>Lịch sử chat</span>
            <button @click="newChat" class="hover:text-gray-600 transition-colors rounded-md hover:bg-gray-100 w-8 h-8 flex items-center justify-center" aria-label="Tạo phiên chat mới">
              <i class="pi pi-plus text-xs"></i>
            </button>
          </div>
          <div class="relative mb-2">
            <i class="pi pi-search absolute left-2.5 top-1/2 -translate-y-1/2 text-gray-400 text-xs" aria-hidden="true"></i>
            <input
              v-model="searchQuery"
              type="search"
              class="w-full h-9 rounded-lg border border-gray-200 bg-gray-50 pl-8 pr-3 text-xs text-gray-900 placeholder:text-gray-400 focus:border-sky-400 focus:bg-white transition-colors"
              placeholder="Tìm kiếm..."
              aria-label="Tìm kiếm đoạn chat" />
          </div>
        </div>
        <div v-if="searchLoading" class="px-3 py-2 text-xs text-gray-400 italic" role="status">Đang tìm kiếm...</div>
        <template v-else>
          <div v-if="displayedSessions.length === 0" class="px-3 py-2 text-xs text-gray-400 italic text-center">
            {{ isSearching ? 'Không tìm thấy.' : 'Chưa có đoạn chat.' }}
          </div>
          <div v-for="session in displayedSessions" :key="session.id" class="w-full flex items-center gap-2 px-3 py-1.5 text-gray-600 hover:text-gray-900 hover:bg-gray-50 rounded-lg transition-colors text-xs truncate group mx-1 my-0.5">
            <template v-if="editingSessionId === session.id">
              <input
                :ref="focusRenameInput"
                v-model="editingTitle"
                type="text"
                class="flex-1 min-w-0 h-8 rounded-md border border-sky-400 bg-white px-2 text-xs text-gray-900"
                :aria-label="`Tên mới cho đoạn chat ${session.title || 'mới'}`"
                @keydown.enter.prevent="saveRename(session.id)"
                @keydown.esc.prevent="cancelRename"
                @blur="cancelRename" />
            </template>
            <template v-else>
              <button type="button" class="flex items-center gap-2 truncate flex-1 cursor-pointer text-left min-h-8" @click="selectSession(session.id)">
                <i class="pi pi-message text-gray-400 text-[11px]"></i>
                <span class="truncate text-left flex-1">{{ session.title || 'Đoạn chat mới' }}</span>
              </button>
              <button @click.stop="startRename(session)" class="text-gray-400 hover:text-gray-700 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 w-7 h-7 rounded hover:bg-gray-200/60 transition-all flex-shrink-0 cursor-pointer flex items-center justify-center" :aria-label="`Đổi tên đoạn chat ${session.title || 'mới'}`">
                <i class="pi pi-pencil text-[10px]"></i>
              </button>
              <button @click.stop="deleteSession(session.id)" class="text-gray-400 hover:text-red-500 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 w-7 h-7 rounded hover:bg-gray-200/60 transition-all flex-shrink-0 cursor-pointer flex items-center justify-center" :aria-label="`Xóa đoạn chat ${session.title || 'mới'}`">
                <i class="pi pi-trash text-[10px]"></i>
              </button>
            </template>
          </div>
        </template>
      </div>
      
      <!-- User Profile -->
      <div class="p-3 border-t border-gray-100">
        <button type="button" class="w-full flex items-center gap-2.5 px-2 py-2 rounded-lg hover:bg-gray-50 transition-colors cursor-pointer group" @click="openSettings" :aria-expanded="false">
          <div class="w-8 h-8 rounded-full bg-gradient-to-br from-sky-500 to-blue-600 flex items-center justify-center flex-shrink-0">
            <span class="text-[11px] font-bold text-white">{{ userInitials }}</span>
          </div>
          <div class="min-w-0 flex-1">
            <div class="text-xs font-semibold text-gray-900 truncate">{{ userName }}</div>
            <div class="text-[10px] text-gray-400 truncate">{{ userRole }}</div>
          </div>
          <div class="flex items-center gap-1">
            <i v-if="isAdmin" class="pi pi-cog text-gray-400 opacity-0 group-hover:opacity-100 transition-opacity hover:text-gray-600 text-xs" aria-hidden="true"></i>
            <button type="button" @click.stop="logout" class="w-7 h-7 flex items-center justify-center text-gray-400 hover:text-red-500 hover:bg-red-50 rounded transition-colors cursor-pointer" aria-label="Đăng xuất">
              <i class="pi pi-sign-out text-xs"></i>
            </button>
          </div>
        </button>
      </div>
    </aside>

    <!-- Main Content Area -->
    <main class="flex-1 flex flex-col relative bg-chatgpt-dark min-w-0">
      
      <!-- Header -->
      <header class="flex items-center justify-between gap-3 px-5 py-3 border-b border-gray-200 bg-white">
        <button @click="mobileNavOpen = !mobileNavOpen" :aria-expanded="mobileNavOpen" aria-controls="mobile-navigation" class="w-10 h-10 md:hidden flex items-center justify-center rounded-lg hover:bg-gray-100 transition-colors" :aria-label="mobileNavOpen ? 'Đóng menu điều hướng' : 'Mở menu điều hướng'"><i class="pi pi-bars text-lg text-gray-600"></i></button>
        <div class="flex-1"></div>
        <div class="flex items-center gap-1.5">
          <div ref="notificationRoot" class="relative" @keydown.escape="closeNotifications(true)">
            <button
              ref="notificationButton"
              @click="toggleNotifications"
              :aria-expanded="notificationsOpen"
              aria-haspopup="true"
              aria-controls="notification-panel"
              aria-label="Thông báo"
              class="relative w-10 h-10 flex items-center justify-center rounded-lg hover:bg-gray-100 transition-colors cursor-pointer">
              <i class="pi pi-bell text-lg text-gray-600"></i>
              <span v-if="unreadCount > 0" aria-hidden="true" class="absolute top-1.5 right-1.5 min-w-[16px] h-[16px] px-1 rounded-full bg-red-500 text-white text-[9px] font-bold flex items-center justify-center">{{ unreadCount > 9 ? '9+' : unreadCount }}</span>
            </button>

            <div v-if="notificationsOpen" id="notification-panel" role="region" aria-label="Danh sách thông báo" class="absolute right-0 top-full mt-2 w-80 max-w-[calc(100vw-2rem)] bg-white border border-gray-200 rounded-xl shadow-xl z-50 overflow-hidden">
              <div class="flex items-center justify-between gap-2 px-4 py-3 border-b border-gray-100">
                <span class="text-sm font-semibold text-gray-900">Thông báo</span>
                <button
                  @click="markAllNotificationsRead"
                  :disabled="notificationsBusy || unreadCount === 0"
                  class="px-2 py-1 text-xs font-medium text-sky-600 hover:text-sky-800 disabled:text-gray-400 disabled:cursor-not-allowed rounded-md hover:bg-gray-100 transition-colors cursor-pointer">
                  Đọc tất cả
                </button>
              </div>
              <div class="max-h-80 overflow-y-auto">
                <div v-if="notificationsLoading" class="px-4 py-6 text-sm text-gray-500 text-center" role="status">Đang tải thông báo...</div>
                <div v-else-if="notifications.length === 0" class="px-4 py-8 text-center">
                  <i class="pi pi-bell-slash text-2xl text-gray-300 mb-2" aria-hidden="true"></i>
                  <p class="text-sm text-gray-500">Không có thông báo nào.</p>
                </div>
                <template v-else>
                  <button
                    v-for="notification in notifications"
                    :key="notification.id"
                    @click="markNotificationRead(notification)"
                    :title="notification.isRead ? undefined : 'Đánh dấu đã đọc'"
                    class="w-full min-h-11 flex items-start gap-3 px-4 py-3 text-left border-b border-gray-50 last:border-b-0 hover:bg-gray-50 transition-colors cursor-pointer">
                    <span class="mt-1.5 w-2 h-2 rounded-full flex-shrink-0" :class="notification.isRead ? 'bg-transparent' : 'bg-sky-500'" aria-hidden="true"></span>
                    <span class="min-w-0 flex-1">
                      <span class="block text-sm truncate" :class="notification.isRead ? 'text-gray-500' : 'font-semibold text-gray-900'">{{ notification.title }}</span>
                      <span v-if="notification.body" class="block text-xs text-gray-500 line-clamp-2 mt-0.5">{{ notification.body }}</span>
                      <span class="block text-[11px] text-gray-400 mt-1">{{ relativeTime(notification.createdAt) }}<template v-if="!notification.isRead"> · Nhấn để đánh dấu đã đọc</template></span>
                    </span>
                  </button>
                </template>
              </div>
            </div>
          </div>
          <button @click="newChat" class="w-10 h-10 md:hidden flex items-center justify-center rounded-lg hover:bg-gray-100 transition-colors" aria-label="Tạo phiên chat mới"><i class="pi pi-plus text-lg text-gray-600"></i></button>
        </div>
      </header>

      <!-- Mobile Navigation -->
      <nav v-if="mobileNavOpen" id="mobile-navigation" class="md:hidden bg-white border-b border-gray-200 p-3 grid gap-1" aria-label="Điều hướng di động">
        <template v-for="group in visibleNavGroups" :key="group.title">
          <div class="text-[10px] text-gray-400 font-semibold mt-2 first:mt-0 px-2 uppercase tracking-widest">{{ group.title }}</div>
          <router-link
            v-for="item in group.items"
            :key="item.to"
            :to="item.to"
            @click="mobileNavOpen = false"
            class="min-h-10 flex items-center gap-2 px-2.5 py-2 rounded-lg hover:bg-gray-50 text-gray-600 hover:text-gray-900 text-sm">
            <i :class="item.icon + ' text-gray-400'" aria-hidden="true"></i>{{ item.label }}
          </router-link>
        </template>
        <button @click="logout" class="min-h-10 text-left px-2.5 py-2 rounded-lg text-red-600 hover:bg-red-50 text-sm flex items-center gap-2"><i class="pi pi-sign-out text-gray-400"></i>Đăng xuất</button>
      </nav>

      <div v-if="appError" role="alert" class="m-3 mb-0 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        {{ appError }}
      </div>

      <!-- Router View -->
      <router-view v-slot="{ Component }">
        <transition name="fade" mode="out-in">
          <component :is="Component" />
        </transition>
      </router-view>
      
    </main>

  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed, watch } from 'vue';
import { useRouter } from 'vue-router';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import { useAuthStore } from '@/store/auth.store';

interface ChatSession {
  id: string;
  title: string;
  createdAt: string;
}

const router = useRouter();
const authStore = useAuthStore();
const userName = computed(() => authStore.currentUser?.fullName || authStore.currentUser?.email || 'Người dùng');
const userRole = computed(() => authStore.currentUser?.role || 'Member');

const isAdmin = computed(() => userRole.value === 'Admin');

// --- Grouped navigation --------------------------------------------------------
// Data-driven so the desktop sidebar and the mobile drawer render the exact same
// items from one source. Labels are kept stable (existing e2e/a11y selectors rely
// on "Bộ nhớ trợ lý", "Quản lý User", "Kho tài liệu"). The "Quản trị" group is
// admin-only and filtered out for members.
interface NavItem { to: string; icon: string; label: string }
interface NavGroup { title: string; items: NavItem[]; adminOnly?: boolean }

const navGroups: NavGroup[] = [
  {
    title: 'Không gian làm việc',
    items: [
      { to: '/chat', icon: 'pi pi-sparkles', label: 'AI Agent' },
      { to: '/projects', icon: 'pi pi-folder', label: 'Dự án' },
      { to: '/documents', icon: 'pi pi-file-pdf', label: 'Kho tài liệu (RAG)' },
      { to: '/memory', icon: 'pi pi-history', label: 'Bộ nhớ trợ lý' },
      { to: '/settings/custom-instructions', icon: 'pi pi-user-edit', label: 'Hướng dẫn tùy chỉnh' },
      { to: '/agents', icon: 'pi pi-microchip-ai', label: 'Agents' },
      { to: '/agents/content-creation', icon: 'pi pi-pen-to-square', label: 'Tạo nội dung' },
      { to: '/agent-mode', icon: 'pi pi-bolt', label: 'Agent tự hành' },
      { to: '/schedules', icon: 'pi pi-calendar-clock', label: 'Lịch tác vụ' },
      { to: '/research', icon: 'pi pi-compass', label: 'Nghiên cứu' }
    ]
  },
  {
    title: 'Công cụ AI',
    items: [
      { to: '/tools/text', icon: 'pi pi-align-left', label: 'Phân tích văn bản' },
      { to: '/tools/vision', icon: 'pi pi-image', label: 'Thị giác ảnh' }
    ]
  },
  {
    title: 'Vận hành',
    items: [
      { to: '/approvals', icon: 'pi pi-check-square', label: 'Phê duyệt tác vụ' },
      { to: '/api-keys', icon: 'pi pi-key', label: 'API Keys' }
    ]
  },
  {
    title: 'Quản trị',
    adminOnly: true,
    items: [
      { to: '/admin', icon: 'pi pi-th-large', label: 'Bảng điều khiển' },
      { to: '/admin/users', icon: 'pi pi-users', label: 'Quản lý User' },
      { to: '/admin/mcp-servers', icon: 'pi pi-server', label: 'Máy chủ MCP' },
      { to: '/admin/knowledge', icon: 'pi pi-database', label: 'Cơ sở tri thức' },
      { to: '/admin/databases', icon: 'pi pi-server', label: 'Quản lý Database' },
      { to: '/admin/widget', icon: 'pi pi-qrcode', label: 'Widget nhúng' },
      { to: '/admin/audit', icon: 'pi pi-shield', label: 'Nhật ký hoạt động' }
    ]
  }
];

const visibleNavGroups = computed(() => navGroups.filter((group) => !group.adminOnly || isAdmin.value));

const appError = ref('');

const chatSessions = ref<ChatSession[]>([]);
const mobileNavOpen = ref(false);

// --- Notification bell ------------------------------------------------------
// The bell must NEVER break the app shell: every network failure here is
// swallowed and logged with console.warn (not console.error, so browser
// error-free quality gates stay green).

interface AppNotification {
  id: string;
  type: string;
  title: string;
  body: string;
  isRead: boolean;
  createdAt: string;
}

const NOTIFICATION_POLL_MS = 60_000;

const notifications = ref<AppNotification[]>([]);
const unreadCount = ref(0);
const notificationsOpen = ref(false);
const notificationsLoading = ref(false);
const notificationsBusy = ref(false);
const notificationRoot = ref<HTMLElement | null>(null);
const notificationButton = ref<HTMLButtonElement | null>(null);
let notificationTimer: ReturnType<typeof setInterval> | undefined;

/** 60s background poll: only refreshes the unread badge (LIST(unreadOnly)). */
const pollUnreadNotifications = async () => {
  try {
    const response = await http.get(ApiFactory.NOTIFICATIONS.LIST(true));
    if (response.ok) {
      const items = await response.json() as AppNotification[];
      unreadCount.value = items.length;
    } else {
      console.warn('[notifications] không thể tải số thông báo chưa đọc', response.status);
    }
  } catch (cause) {
    console.warn('[notifications] không thể tải số thông báo chưa đọc', cause);
  }
};

/** Full refresh used when the dropdown is open: latest list + unread badge. */
const refreshNotifications = async () => {
  notificationsLoading.value = notifications.value.length === 0;
  try {
    const response = await http.get(ApiFactory.NOTIFICATIONS.LIST());
    if (response.ok) {
      notifications.value = await response.json() as AppNotification[];
      unreadCount.value = notifications.value.filter((item) => !item.isRead).length;
    } else {
      console.warn('[notifications] không thể tải danh sách thông báo', response.status);
    }
  } catch (cause) {
    console.warn('[notifications] không thể tải danh sách thông báo', cause);
  } finally {
    notificationsLoading.value = false;
  }
};

const toggleNotifications = async () => {
  notificationsOpen.value = !notificationsOpen.value;
  if (notificationsOpen.value) await refreshNotifications();
};

const closeNotifications = (focusButton = false) => {
  if (!notificationsOpen.value) return;
  notificationsOpen.value = false;
  if (focusButton) notificationButton.value?.focus();
};

const markNotificationRead = async (notification: AppNotification) => {
  if (notification.isRead || notificationsBusy.value) return;
  notificationsBusy.value = true;
  try {
    const response = await http.post(ApiFactory.NOTIFICATIONS.MARK_READ(notification.id));
    if (response.ok) await refreshNotifications();
    else console.warn('[notifications] không thể đánh dấu đã đọc', response.status);
  } catch (cause) {
    console.warn('[notifications] không thể đánh dấu đã đọc', cause);
  } finally {
    notificationsBusy.value = false;
  }
};

const markAllNotificationsRead = async () => {
  if (notificationsBusy.value) return;
  notificationsBusy.value = true;
  try {
    const response = await http.post(ApiFactory.NOTIFICATIONS.READ_ALL);
    if (response.ok) await refreshNotifications();
    else console.warn('[notifications] không thể đọc tất cả thông báo', response.status);
  } catch (cause) {
    console.warn('[notifications] không thể đọc tất cả thông báo', cause);
  } finally {
    notificationsBusy.value = false;
  }
};

/** Closes the dropdown when the user clicks anywhere outside the bell. */
const onDocumentClick = (event: MouseEvent) => {
  if (!notificationsOpen.value) return;
  const root = notificationRoot.value;
  if (root && event.target instanceof Node && !root.contains(event.target)) {
    closeNotifications();
  }
};

const relativeTime = (iso: string): string => {
  const time = new Date(iso).getTime();
  if (Number.isNaN(time)) return '';
  const minutes = Math.floor((Date.now() - time) / 60_000);
  if (minutes < 1) return 'Vừa xong';
  if (minutes < 60) return `${minutes} phút trước`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} giờ trước`;
  const days = Math.floor(hours / 24);
  if (days < 7) return `${days} ngày trước`;
  return new Date(iso).toLocaleDateString('vi-VN');
};

// --- Session history search -------------------------------------------------

const searchQuery = ref('');
/** `null` = no active search: the sidebar shows the normal session list. */
const searchResults = ref<ChatSession[] | null>(null);
const searchLoading = ref(false);
let searchDebounce: ReturnType<typeof setTimeout> | undefined;
/** Monotonic id so a stale response can never overwrite a newer search. */
let searchRequestId = 0;

const isSearching = computed(() => searchQuery.value.trim().length > 0);
const displayedSessions = computed(() => searchResults.value ?? chatSessions.value);

const runSearch = async (query: string) => {
  const requestId = ++searchRequestId;
  try {
    const response = await http.get(ApiFactory.CHAT.SEARCH_SESSIONS(query));
    if (requestId !== searchRequestId) return;
    if (response.ok) {
      searchResults.value = await response.json();
    } else {
      searchResults.value = [];
      appError.value = await readApiError(response, 'Không thể tìm kiếm đoạn chat');
    }
  } catch (error) {
    if (requestId !== searchRequestId) return;
    searchResults.value = [];
    appError.value = errorMessage(error, 'Không thể tìm kiếm đoạn chat.');
  } finally {
    if (requestId === searchRequestId) searchLoading.value = false;
  }
};

watch(searchQuery, (query) => {
  if (searchDebounce) clearTimeout(searchDebounce);
  const trimmed = query.trim();
  if (!trimmed) {
    // Clearing the box restores the normal list and invalidates in-flight results.
    searchRequestId += 1;
    searchResults.value = null;
    searchLoading.value = false;
    return;
  }
  searchLoading.value = true;
  searchDebounce = setTimeout(() => { void runSearch(trimmed); }, 300);
});

// --- Inline session rename ----------------------------------------------------

const editingSessionId = ref<string | null>(null);
const editingTitle = ref('');

const startRename = (session: ChatSession) => {
  editingSessionId.value = session.id;
  editingTitle.value = session.title || '';
};

const cancelRename = () => {
  editingSessionId.value = null;
  editingTitle.value = '';
};

/** Template function-ref: focuses the rename input as soon as it mounts. */
const focusRenameInput = (el: unknown) => {
  if (el instanceof HTMLInputElement && document.activeElement !== el) el.focus();
};

const saveRename = async (id: string) => {
  if (editingSessionId.value !== id) return;
  const title = editingTitle.value.trim();
  // Close the editor synchronously so the pending blur can't double-fire.
  cancelRename();
  const current = displayedSessions.value.find((session) => session.id === id);
  if (!title || !current || title === current.title) return;
  appError.value = '';
  try {
    const response = await http.patch(ApiFactory.CHAT.RENAME_SESSION(id), { title });
    if (response.ok) {
      // 204: reflect the new title locally in both the full and search lists.
      const apply = (sessions: ChatSession[]) => {
        for (const session of sessions) {
          if (session.id === id) session.title = title;
        }
      };
      apply(chatSessions.value);
      if (searchResults.value) apply(searchResults.value);
    } else {
      appError.value = await readApiError(response, 'Không thể đổi tên đoạn chat');
    }
  } catch (error) {
    appError.value = errorMessage(error, 'Không thể đổi tên đoạn chat.');
  }
};

// The gear on the user card is an admin-only shortcut to the management hub;
// members have no settings surface, so it is a no-op for them and the cog
// affordance is hidden. MCP management lives at /admin/mcp-servers.
const openSettings = async () => {
  if (isAdmin.value) await router.push('/admin');
};

const newChat = async () => {
  mobileNavOpen.value = false;
  await router.push('/chat?new=' + Date.now());
};

const selectSession = (id: string) => {
  router.push(`/chat?id=${id}`);
};

const deleteSession = async (id: string) => {
  if (!confirm("Bạn có chắc chắn muốn xóa đoạn chat này không?")) return;
  appError.value = '';
  try {
    const response = await http.delete(ApiFactory.CHAT.DELETE_SESSION(id));
    if (response.ok) {
      const route = router.currentRoute.value;
      if (route.query.id === id) {
        router.push('/chat?new=' + Date.now());
      }
      loadChatSessions();
    } else appError.value = await readApiError(response, 'Không thể xóa đoạn chat');
  } catch (error) {
    appError.value = errorMessage(error, 'Không thể xóa đoạn chat.');
  }
};

const loadChatSessions = async () => {
  appError.value = '';
  try {
    const response = await http.get(ApiFactory.CHAT.SESSIONS);
    if (response.ok) {
      chatSessions.value = await response.json();
    } else appError.value = await readApiError(response, 'Không thể tải lịch sử trò chuyện');
  } catch (error) {
    appError.value = errorMessage(error, 'Không thể tải lịch sử trò chuyện.');
  }
};

const userInitials = computed(() => {
  return userName.value.substring(0, 2).toUpperCase();
});

onMounted(() => {
  loadChatSessions();
  window.addEventListener('chat-session-created', loadChatSessions);
  document.addEventListener('click', onDocumentClick);
  void pollUnreadNotifications();
  notificationTimer = setInterval(() => { void pollUnreadNotifications(); }, NOTIFICATION_POLL_MS);
});

onUnmounted(() => {
  window.removeEventListener('chat-session-created', loadChatSessions);
  document.removeEventListener('click', onDocumentClick);
  if (notificationTimer) clearInterval(notificationTimer);
  if (searchDebounce) clearTimeout(searchDebounce);
});

const logout = async () => {
  await authStore.logout();
  await router.push('/login');
};
</script>

<style scoped>
.fade-enter-active,
.fade-leave-active {
  transition: opacity 0.2s ease;
}
.fade-enter-from,
.fade-leave-to {
  opacity: 0;
}
</style>
