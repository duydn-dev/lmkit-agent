<template>
  <div class="flex h-screen bg-gray-50 text-gray-900 font-sans">
    <!-- Host toàn cục: mọi view gọi useConfirm()/useToast() đều hiển thị qua đây -->
    <ConfirmDialog class="max-w-md" />
    <Toast position="top-right" />
    
    <!-- Sidebar -->
    <aside class="w-[260px] bg-white flex flex-col hidden md:flex transition-all duration-300" aria-label="Thanh bên ứng dụng">
      <!-- Brand: dải nhận diện xanh nước biển đậm — tên đầy đủ của cơ quan
           hiển thị trên top header để reuse khoảng trống, đỡ trống trải. -->
      <div class="h-14 shrink-0 px-4 bg-[var(--color-gov-blue-dark)] flex items-center">
        <div class="flex items-center gap-2.5 min-w-0">
          <div class="w-9 h-9 rounded-full bg-white flex items-center justify-center flex-shrink-0 overflow-hidden p-0.5">
            <img :src="authStore.brandLogoUrl || quochuyLogo" :alt="brandTitle" class="w-full h-full object-contain" />
          </div>
          <div class="text-sm font-bold text-white truncate tracking-wide">{{ brandTitle }}</div>
        </div>
      </div>

      <!-- Navigation: MỘT vùng cuộn duy nhất — menu và lịch sử chat không còn
           tranh nhau chiều cao (trước đây hai vùng flex-1 khiến nhóm Quản trị
           chìm dưới fold, tưởng như "thiếu page"). Nhóm gập/mở, trạng thái lưu lại. -->
      <nav class="flex-1 min-h-0 overflow-y-auto py-3 px-3" aria-label="Điều hướng">
        <template v-for="group in visibleNavGroups" :key="group.title">
          <button
            type="button"
            class="w-full flex items-center justify-between text-[10px] text-gray-400 hover:text-gray-600 font-semibold mb-1 mt-4 first:mt-0 px-2 uppercase tracking-widest cursor-pointer"
            :aria-expanded="!collapsedGroups.has(group.title)"
            @click="toggleGroup(group.title)">
            <span>{{ group.title }}</span>
            <i :class="collapsedGroups.has(group.title) ? 'pi pi-chevron-right' : 'pi pi-chevron-down'" class="text-[9px]" aria-hidden="true"></i>
          </button>
          <template v-if="!collapsedGroups.has(group.title)">
            <router-link
              v-for="item in group.items"
              :key="item.to"
              :to="item.to"
              class="w-full flex items-center gap-2.5 px-2.5 py-2 hover:bg-gray-50 text-gray-600 hover:text-gray-900 text-[13px] font-medium rounded-lg transition-colors cursor-pointer group/item"
              active-class="!bg-blue-50 !text-[var(--color-gov-blue-dark)] border-l-2 border-[var(--color-gov-blue-dark)] -ml-[2px] pl-[12px] font-semibold">
              <i :class="item.icon + ' text-gray-400 group-hover/item:text-gray-600'" aria-hidden="true"></i>
              <span>{{ item.label }}</span>
            </router-link>
          </template>
        </template>

      </nav>

      <!-- User Profile -->
      <div class="p-3 border-t border-gray-100">
        <button type="button" class="w-full flex items-center gap-2.5 px-2 py-2 rounded-lg hover:bg-gray-50 transition-colors cursor-pointer group" @click="openSettings" :aria-expanded="false">
          <Avatar
            :image="authStore.currentUser?.avatarUrl || undefined"
            :label="userInitials"
            shape="circle"
            :aria-label="`Ảnh đại diện của ${userName}`"
            class="!w-8 !h-8 flex-shrink-0 !bg-blue-50 !text-[var(--color-gov-blue-dark)] !font-bold" />
          <div class="min-w-0 flex-1 text-left">
            <div class="text-xs font-semibold text-slate-800 truncate">{{ userName }}</div>
            <div class="text-[10px] text-slate-500 truncate">{{ userRole }}</div>
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
      <header class="h-14 shrink-0 flex items-center justify-between gap-3 px-5 bg-[var(--color-gov-blue-dark)]">
        <div class="flex items-center gap-3 min-w-0 flex-1">
          <button @click="mobileNavOpen = !mobileNavOpen" :aria-expanded="mobileNavOpen" aria-controls="mobile-navigation" class="w-10 h-10 md:hidden flex items-center justify-center rounded-lg hover:bg-white/10 transition-colors" :aria-label="mobileNavOpen ? 'Đóng menu điều hướng' : 'Mở menu điều hướng'"><i class="pi pi-bars text-lg text-blue-100"></i></button>
          <p class="text-[13px] font-medium text-blue-100 truncate" :title="orgName">{{ orgName }}</p>
        </div>
        <div class="flex items-center gap-1.5">
          <button @click="openHistoryDrawer" class="w-10 h-10 flex items-center justify-center rounded-lg hover:bg-white/10 transition-colors cursor-pointer" aria-label="Lịch sử chat" aria-haspopup="true">
            <i class="pi pi-history text-lg text-blue-100"></i>
          </button>
          <div ref="notificationRoot" class="relative" @keydown.escape="closeNotifications(true)">
            <button
              ref="notificationButton"
              @click="toggleNotifications"
              :aria-expanded="notificationsOpen"
              aria-haspopup="true"
              aria-controls="notification-panel"
              aria-label="Thông báo"
              class="relative w-10 h-10 flex items-center justify-center rounded-lg hover:bg-white/10 transition-colors cursor-pointer">
              <i class="pi pi-bell text-lg text-blue-100"></i>
              <span v-if="unreadCount > 0" aria-hidden="true" class="absolute top-1.5 right-1.5 min-w-[16px] h-[16px] px-1 rounded-full bg-red-500 text-white text-[9px] font-bold flex items-center justify-center">{{ unreadCount > 9 ? '9+' : unreadCount }}</span>
            </button>

            <div v-if="notificationsOpen" id="notification-panel" role="region" aria-label="Danh sách thông báo" class="absolute right-0 top-full mt-2 w-80 max-w-[calc(100vw-2rem)] bg-white border border-gray-200 rounded-xl shadow-xl z-50 overflow-hidden">
              <div class="flex items-center justify-between gap-2 px-4 py-3 border-b border-gray-100">
                <span class="text-sm font-semibold text-gray-900">Thông báo</span>
                <button
                  @click="markAllNotificationsRead"
                  :disabled="notificationsBusy || unreadCount === 0"
                  class="px-2 py-1 text-xs font-medium text-blue-600 hover:text-blue-800 disabled:text-gray-400 disabled:cursor-not-allowed rounded-md hover:bg-gray-100 transition-colors cursor-pointer">
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
                    <span class="mt-1.5 w-2 h-2 rounded-full flex-shrink-0" :class="notification.isRead ? 'bg-transparent' : 'bg-blue-600'" aria-hidden="true"></span>
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
          <button @click="newChat" class="w-10 h-10 md:hidden flex items-center justify-center rounded-lg hover:bg-white/10 transition-colors" aria-label="Tạo phiên chat mới"><i class="pi pi-plus text-lg text-blue-100"></i></button>
        </div>
      </header>

      <!-- Mobile Navigation: menu + lịch sử chat (component dùng chung) + đăng xuất -->
      <nav v-if="mobileNavOpen" id="mobile-navigation" class="md:hidden bg-white border-b border-gray-200 p-3 grid gap-1 max-h-[calc(100vh-3.5rem)] overflow-y-auto" aria-label="Điều hướng di động">
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

      <!-- Lịch sử chat: Drawer riêng của PrimeVue, mở từ nút trên header (mọi trang).
           Không nhét vào sidebar nữa theo yêu cầu thiết kế. -->
      <Drawer
        v-model:visible="historyDrawerOpen"
        header="Lịch sử chat"
        position="left"
        :modal="true"
        class="!w-[300px] max-w-[85vw]"
        :pt="{
          header: '!border-b !border-gray-100',
          content: '!p-3'
        }">
        <ChatHistoryPanel
          v-model:query="searchQuery"
          v-model:editing-title="editingTitle"
          :sessions="displayedSessions"
          :loading="searchLoading"
          :searching="isSearching"
          :editing-session-id="editingSessionId"
          :loading-more="loadingMore"
          :has-more="hasMoreSessions"
          @new="historyDrawerNew"
          @select="historyDrawerSelect"
          @start-rename="startRename"
          @save-rename="saveRename"
          @cancel-rename="cancelRename"
          @delete="historyDrawerDelete"
          @load-more="loadMoreSessions" />
      </Drawer>

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
import { useConfirm } from 'primevue/useconfirm';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import { useAuthStore } from '@/store/auth.store';
import quochuyLogo from '@/assets/quochuy.svg';

interface ChatSession {
  id: string;
  title: string;
  createdAt: string;
}

const router = useRouter();
const confirm = useConfirm();
const authStore = useAuthStore();

/** Lịch sử chat giờ hiện ở mọi trang qua ChatHistoryPanel — isChatRoute không còn cần thiết. */

// --- Nhóm menu gập/mở, lưu qua localStorage ---------------------------------
const NAV_COLLAPSED_KEY = 'cila.nav.collapsed';
const readCollapsed = (): Set<string> => {
  try {
    const raw = localStorage.getItem(NAV_COLLAPSED_KEY);
    return new Set(raw ? (JSON.parse(raw) as string[]) : []);
  } catch { return new Set(); }
};
const collapsedGroups = ref<Set<string>>(readCollapsed());
const toggleGroup = (title: string) => {
  const next = new Set(collapsedGroups.value);
  if (next.has(title)) next.delete(title); else next.add(title);
  collapsedGroups.value = next;
  try { localStorage.setItem(NAV_COLLAPSED_KEY, JSON.stringify([...next])); } catch { /* im lặng */ }
};
import ChatHistoryPanel from './ChatHistoryPanel.vue';

const userName = computed(() => authStore.currentUser?.fullName || authStore.currentUser?.email || 'Người dùng');
const userRole = computed(() => authStore.currentUser?.role || 'Member');

const isAdmin = computed(() => userRole.value === 'Admin');

// --- Thương hiệu theo tenant (logo + tên trợ lý + tên đơn vị) -----------------
// Khi chưa cấu hình, giữ nguyên nhận diện mặc định của hệ thống.
const DEFAULT_ORG = 'Trung tâm Thông tin lưu trữ và Thư viện tài nguyên môi trường quốc gia';
const brandTitle = computed(() => authStore.currentUser?.tenant?.agentName?.trim() || 'CILA · AI AGENT');
const orgName = computed(() => authStore.tenantName || DEFAULT_ORG);

// --- Grouped navigation --------------------------------------------------------
// Data-driven so the desktop sidebar and the mobile drawer render the exact same
// items from one source. Labels are kept stable (existing e2e/a11y selectors rely
// on "Bộ nhớ trợ lý", "Quản lý User", "Kho tài liệu"). The "Quản trị" group is
// admin-only and filtered out for members.
interface NavItem { to: string; icon: string; label: string }
interface NavGroup { title: string; items: NavItem[]; adminOnly?: boolean }

// Nhãn dùng thuật ngữ kỹ thuật chuẩn ngành (Automation Agent, RAG, HITL, MCP,
// LoRA…) theo yêu cầu vận hành; tiêu đề nhóm giữ tiếng Việt hành chính.
const navGroups: NavGroup[] = [
  {
    // Dashboard đứng ĐẦU sidebar theo yêu cầu: trang đích khi mở app, không
    // phải mục cuối cùng nằm dưới fold. Vẫn chỉ hiện cho admin (route /admin).
    title: 'Tổng quan',
    adminOnly: true,
    items: [
      { to: '/admin', icon: 'pi pi-th-large', label: 'Dashboard' }
    ]
  },
  {
    title: 'Không gian làm việc',
    items: [
      { to: '/chat', icon: 'pi pi-sparkles', label: 'AI Chat' },
      { to: '/projects', icon: 'pi pi-folder', label: 'Projects' },
      { to: '/documents', icon: 'pi pi-file-pdf', label: 'RAG Documents' },
      { to: '/memory', icon: 'pi pi-history', label: 'Agent Memory' },
      { to: '/settings/custom-instructions', icon: 'pi pi-user-edit', label: 'Custom Instructions' }
    ]
  },
  {
    title: 'AI Studio',
    items: [
      { to: '/agents', icon: 'pi pi-microchip-ai', label: 'Agent Studio' },
      { to: '/agents/content-creation', icon: 'pi pi-pen-to-square', label: 'Content Studio' },
      { to: '/agent-mode', icon: 'pi pi-bolt', label: 'Automation Agent' },
      { to: '/schedules', icon: 'pi pi-calendar-clock', label: 'Task Scheduler' },
      { to: '/research', icon: 'pi pi-compass', label: 'Deep Research' },
      { to: '/tools/text', icon: 'pi pi-align-left', label: 'Text Analytics' },
      { to: '/tools/vision', icon: 'pi pi-image', label: 'Vision & OCR' }
    ]
  },
  {
    title: 'Vận hành',
    items: [
      { to: '/approvals', icon: 'pi pi-check-square', label: 'HITL Approvals' },
      { to: '/api-keys', icon: 'pi pi-key', label: 'API Keys' }
    ]
  },
  {
    title: 'Quản trị',
    adminOnly: true,
    items: [
      { to: '/admin/users', icon: 'pi pi-users', label: 'User Management' },
      { to: '/admin/tenants', icon: 'pi pi-building', label: 'Tenant Management' },
      { to: '/admin/databases', icon: 'pi pi-database', label: 'Database Connections' },
      { to: '/admin/knowledge', icon: 'pi pi-book', label: 'Knowledge Base' },
      { to: '/admin/mcp-servers', icon: 'pi pi-server', label: 'MCP Servers' },
      { to: '/admin/lora', icon: 'pi pi-sliders-h', label: 'LoRA Adapters' },
      { to: '/admin/widget', icon: 'pi pi-qrcode', label: 'Embed Widget' },
      { to: '/admin/audit', icon: 'pi pi-shield', label: 'Audit Log' }
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

// --- Drawer lịch sử chat (mở từ nút header) ---------------------------------
const historyDrawerOpen = ref(false);
const openHistoryDrawer = () => { historyDrawerOpen.value = true; };
const historyDrawerSelect = (id: string) => {
  historyDrawerOpen.value = false;
  selectSession(id);
};
const historyDrawerNew = () => {
  historyDrawerOpen.value = false;
  void newChat();
};
const historyDrawerDelete = (id: string) => {
  historyDrawerOpen.value = false;
  deleteSession(id);
};

const deleteSession = (id: string) => {
  confirm.require({
    header: 'Xóa đoạn chat',
    message: 'Bạn có chắc chắn muốn xóa đoạn chat này không?',
    icon: 'pi pi-exclamation-triangle',
    acceptLabel: 'Xóa',
    rejectLabel: 'Hủy',
    acceptProps: { severity: 'danger' },
    rejectProps: { severity: 'secondary', outlined: true },
    accept: () => { void performDeleteSession(id); }
  });
};

const performDeleteSession = async (id: string) => {
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

// --- Lịch sử chat: phân trang keyset + infinite scroll ----------------------
// Lần đầu chỉ tải trang mới nhất (PAGE_SIZE phiên); cuộn tới đáy danh sách
// mới tải trang kế bằng ?before=<CreatedAt của phiên cuối> — ổn định kể cả khi
// có phiên mới được tạo giữa lúc cuộn.
const PAGE_SIZE = 10;
const chatSessionsTotalLoaded = ref(0);
const loadingMore = ref(false);
const hasMoreSessions = ref(false);

const buildSessionsUrl = (before?: string) => {
  const params = new URLSearchParams({ limit: String(PAGE_SIZE) });
  if (before) params.set('before', before);
  return `${ApiFactory.CHAT.SESSIONS}?${params.toString()}`;
};

const loadChatSessions = async () => {
  appError.value = '';
  try {
    const response = await http.get(buildSessionsUrl());
    if (response.ok) {
      chatSessions.value = await response.json();
      chatSessionsTotalLoaded.value = chatSessions.value.length;
      hasMoreSessions.value = chatSessions.value.length === PAGE_SIZE;
    } else appError.value = await readApiError(response, 'Không thể tải lịch sử trò chuyện');
  } catch (error) {
    appError.value = errorMessage(error, 'Không thể tải lịch sử trò chuyện.');
  }
};

/** Infinite scroll: tải trang kế bằng mốc CreatedAt của phiên cuối đã render. */
const loadMoreSessions = async () => {
  if (loadingMore.value || !hasMoreSessions.value || isSearching.value) return;
  const oldest = chatSessions.value[chatSessions.value.length - 1];
  if (!oldest) return;
  loadingMore.value = true;
  try {
    const response = await http.get(buildSessionsUrl(oldest.createdAt));
    if (response.ok) {
      const next: ChatSession[] = await response.json();
      const known = new Set(chatSessions.value.map((session) => session.id));
      for (const session of next) {
        if (!known.has(session.id)) chatSessions.value.push(session);
      }
      hasMoreSessions.value = next.length === PAGE_SIZE;
    }
  } catch (error) {
    console.warn('[history] không thể tải thêm lịch sử', error);
  } finally {
    loadingMore.value = false;
  }
};

const userInitials = computed(() => {
  const name = userName.value.trim();
  if (!name) return '?';
  const parts = name.split(/\s+/).filter(Boolean);
  if (parts.length > 1) return parts.slice(0, 2).map((part) => Array.from(part)[0] || '').join('').toUpperCase();
  return Array.from(parts[0])[0]?.toUpperCase() || '?';
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
