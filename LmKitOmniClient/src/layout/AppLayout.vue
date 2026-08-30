<template>
  <div class="flex h-screen bg-chatgpt-dark text-chatgpt-text font-sans">
    
    <!-- Sidebar -->
    <aside class="w-64 bg-gray-100 flex flex-col hidden md:flex transition-all duration-300" aria-label="Thanh bên ứng dụng">
      <div class="p-3 pb-0">
        <div class="text-xs text-gray-500 font-semibold mb-2 px-3 uppercase tracking-wider">Không gian làm việc</div>
        <router-link to="/chat" class="w-full flex items-center gap-3 px-3 py-3 hover:bg-chatgpt-light font-medium rounded-md transition-colors cursor-pointer" active-class="bg-chatgpt-light border border-gray-200">
          <i class="pi pi-sparkles"></i>
          <span>Trợ lý AI</span>
        </router-link>
        
        <router-link to="/documents" class="w-full flex items-center gap-3 px-3 py-3 hover:bg-chatgpt-light font-medium rounded-md transition-colors cursor-pointer mt-1" active-class="bg-chatgpt-light border border-gray-200">
          <i class="pi pi-file-pdf"></i>
          <span>Kho tài liệu (RAG)</span>
        </router-link>

        <router-link to="/memory" class="w-full flex items-center gap-3 px-3 py-3 hover:bg-chatgpt-light font-medium rounded-md transition-colors cursor-pointer mt-1" active-class="bg-chatgpt-light border border-gray-200">
          <i class="pi pi-history"></i>
          <span>Bộ nhớ trợ lý</span>
        </router-link>

        <router-link to="/agents" class="w-full flex items-center gap-3 px-3 py-3 hover:bg-chatgpt-light font-medium rounded-md transition-colors cursor-pointer mt-1" active-class="bg-chatgpt-light border border-gray-200">
          <i class="pi pi-microchip-ai"></i>
          <span>Agents</span>
        </router-link>

        <router-link to="/schedules" class="w-full flex items-center gap-3 px-3 py-3 hover:bg-chatgpt-light font-medium rounded-md transition-colors cursor-pointer mt-1" active-class="bg-chatgpt-light border border-gray-200">
          <i class="pi pi-calendar-clock"></i>
          <span>Lịch tác vụ</span>
        </router-link>

        <router-link to="/research" class="w-full flex items-center gap-3 px-3 py-3 hover:bg-chatgpt-light font-medium rounded-md transition-colors cursor-pointer mt-1" active-class="bg-chatgpt-light border border-gray-200">
          <i class="pi pi-compass"></i>
          <span>Nghiên cứu</span>
        </router-link>

        <router-link v-if="isAdmin" to="/admin/users" class="w-full flex items-center gap-3 px-3 py-3 hover:bg-chatgpt-light font-medium rounded-md transition-colors cursor-pointer mt-1" active-class="bg-chatgpt-light border border-gray-200">
          <i class="pi pi-users"></i>
          <span>Quản lý User</span>
        </router-link>
      </div>
      
      <div class="flex-1 overflow-y-auto p-3 mt-2 flex flex-col">
        <div class="text-xs text-gray-500 font-semibold mb-2 px-3 uppercase tracking-wider flex items-center justify-between">
          <span>Lịch sử trò chuyện</span>
          <button @click="newChat" class="hover:text-gray-900 transition-colors rounded-md hover:bg-gray-200 w-11 h-11 flex items-center justify-center" aria-label="Tạo phiên chat mới">
            <i class="pi pi-plus"></i>
          </button>
        </div>
        <div class="relative mb-2 px-1">
          <i class="pi pi-search absolute left-4 top-1/2 -translate-y-1/2 text-gray-500 text-xs" aria-hidden="true"></i>
          <input
            v-model="searchQuery"
            type="search"
            class="w-full min-h-11 rounded-lg border border-gray-200 bg-white pl-9 pr-3 text-sm text-gray-900 placeholder:text-gray-500 focus:border-sky-500"
            placeholder="Tìm kiếm đoạn chat"
            aria-label="Tìm kiếm đoạn chat" />
        </div>
        <div v-if="searchLoading" class="px-3 py-2 text-xs text-gray-500 italic" role="status">Đang tìm kiếm...</div>
        <template v-else>
          <div v-if="displayedSessions.length === 0" class="px-3 py-2 text-xs text-gray-500 italic">
            {{ isSearching ? 'Không tìm thấy đoạn chat nào.' : 'Chưa có phiên chat nào.' }}
          </div>
          <div v-for="session in displayedSessions" :key="session.id" class="w-full flex items-center justify-between gap-3 px-3 py-2 text-gray-700 hover:text-gray-900 font-medium hover:bg-chatgpt-light rounded-md transition-colors text-sm truncate group mt-1">
            <template v-if="editingSessionId === session.id">
              <input
                :ref="focusRenameInput"
                v-model="editingTitle"
                type="text"
                class="flex-1 min-w-0 min-h-11 rounded-md border border-sky-500 bg-white px-2 text-sm text-gray-900"
                :aria-label="`Tên mới cho đoạn chat ${session.title || 'mới'}`"
                @keydown.enter.prevent="saveRename(session.id)"
                @keydown.esc.prevent="cancelRename"
                @blur="cancelRename" />
            </template>
            <template v-else>
              <button type="button" class="flex items-center gap-3 truncate flex-1 cursor-pointer text-left min-h-11" @click="selectSession(session.id)">
                <i class="pi pi-message text-gray-500 group-hover:text-gray-700"></i>
                <span class="truncate text-left flex-1">{{ session.title || 'Đoạn chat mới' }}</span>
              </button>
              <button @click.stop="startRename(session)" class="text-gray-500 hover:text-gray-900 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 w-11 h-11 rounded hover:bg-gray-200/50 transition-all flex-shrink-0 cursor-pointer" :aria-label="`Đổi tên đoạn chat ${session.title || 'mới'}`">
                <i class="pi pi-pencil text-xs"></i>
              </button>
              <button @click.stop="deleteSession(session.id)" class="text-gray-500 hover:text-red-600 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 w-11 h-11 rounded hover:bg-gray-200/50 transition-all flex-shrink-0 cursor-pointer" :aria-label="`Xóa đoạn chat ${session.title || 'mới'}`">
                <i class="pi pi-trash text-xs"></i>
              </button>
            </template>
          </div>
        </template>
      </div>
      
      <div class="p-3 border-t border-gray-200 flex items-center gap-1">
        <button @click="openSettings" class="flex-1 min-h-11 flex items-center gap-3 overflow-hidden px-2 py-2 hover:bg-chatgpt-light rounded-md transition-colors text-left cursor-pointer group">
          <div class="w-8 h-8 rounded-full bg-gradient-to-r from-purple-500 to-pink-500 flex items-center justify-center flex-shrink-0 shadow-sm">
            <span class="text-xs font-bold text-white">{{ userInitials }}</span>
          </div>
          <div class="overflow-hidden flex-1">
            <div class="text-sm font-medium truncate text-gray-900">{{ userName }}</div>
            <div class="text-xs text-gray-500 truncate">{{ userEmail }}</div>
          </div>
          <i class="pi pi-cog text-gray-500 opacity-0 group-hover:opacity-100 transition-opacity hover:text-gray-900"></i>
        </button>
        
        <button @click="logout" class="w-11 h-11 flex items-center justify-center text-gray-500 hover:text-red-600 hover:bg-chatgpt-light rounded-md transition-colors cursor-pointer flex-shrink-0" aria-label="Đăng xuất">
          <i class="pi pi-sign-out"></i>
        </button>
      </div>
    </aside>

    <!-- Main Content Area -->
    <main class="flex-1 flex flex-col relative bg-chatgpt-dark min-w-0">
      
      <!-- Header (mobile toggle + notification bell) -->
      <header class="flex items-center justify-between gap-2 p-4 border-b border-gray-200 bg-gray-100">
        <button @click="mobileNavOpen = !mobileNavOpen" :aria-expanded="mobileNavOpen" aria-controls="mobile-navigation" class="w-11 h-11 md:hidden" :aria-label="mobileNavOpen ? 'Đóng menu điều hướng' : 'Mở menu điều hướng'"><i class="pi pi-bars text-xl"></i></button>
        <span class="font-medium">Nền tảng Trợ lý AI</span>
        <div class="flex items-center gap-1">
          <div ref="notificationRoot" class="relative" @keydown.escape="closeNotifications(true)">
            <button
              ref="notificationButton"
              @click="toggleNotifications"
              :aria-expanded="notificationsOpen"
              aria-haspopup="true"
              aria-controls="notification-panel"
              aria-label="Thông báo"
              class="relative w-11 h-11 flex items-center justify-center rounded-md hover:bg-gray-200 transition-colors cursor-pointer">
              <i class="pi pi-bell text-xl"></i>
              <span v-if="unreadCount > 0" aria-hidden="true" class="absolute top-1 right-0.5 min-w-[18px] h-[18px] px-1 rounded-full bg-red-600 text-white text-[10px] font-bold flex items-center justify-center">{{ unreadCount > 9 ? '9+' : unreadCount }}</span>
            </button>

            <div v-if="notificationsOpen" id="notification-panel" role="region" aria-label="Danh sách thông báo" class="absolute right-0 top-full mt-2 w-80 max-w-[calc(100vw-2rem)] bg-white border border-gray-200 rounded-xl shadow-lg z-50 overflow-hidden">
              <div class="flex items-center justify-between gap-2 px-4 py-3 border-b border-gray-100">
                <span class="text-sm font-semibold text-gray-900">Thông báo</span>
                <button
                  @click="markAllNotificationsRead"
                  :disabled="notificationsBusy || unreadCount === 0"
                  class="min-h-11 px-2 text-xs font-medium text-sky-700 hover:text-sky-900 disabled:text-gray-400 disabled:cursor-not-allowed rounded-md hover:bg-gray-100 transition-colors cursor-pointer">
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
                    class="w-full min-h-11 flex items-start gap-3 px-4 py-3 text-left border-b border-gray-100 last:border-b-0 hover:bg-gray-50 transition-colors cursor-pointer">
                    <span class="mt-1.5 w-2 h-2 rounded-full flex-shrink-0" :class="notification.isRead ? 'bg-transparent' : 'bg-sky-600'" aria-hidden="true"></span>
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
          <button @click="newChat" class="w-11 h-11 md:hidden" aria-label="Tạo phiên chat mới"><i class="pi pi-plus text-xl"></i></button>
        </div>
      </header>

      <nav v-if="mobileNavOpen" id="mobile-navigation" class="md:hidden bg-gray-100 border-b border-gray-200 p-3 grid gap-1" aria-label="Điều hướng di động">
        <router-link to="/chat" @click="mobileNavOpen = false" class="min-h-11 flex items-center px-3 py-2 rounded hover:bg-chatgpt-light"><i class="pi pi-sparkles mr-2"></i>Trợ lý AI</router-link>
        <router-link to="/documents" @click="mobileNavOpen = false" class="min-h-11 flex items-center px-3 py-2 rounded hover:bg-chatgpt-light"><i class="pi pi-file-pdf mr-2"></i>Kho tài liệu</router-link>
        <router-link to="/memory" @click="mobileNavOpen = false" class="min-h-11 flex items-center px-3 py-2 rounded hover:bg-chatgpt-light"><i class="pi pi-history mr-2"></i>Bộ nhớ trợ lý</router-link>
        <router-link to="/agents" @click="mobileNavOpen = false" class="min-h-11 flex items-center px-3 py-2 rounded hover:bg-chatgpt-light"><i class="pi pi-microchip-ai mr-2"></i>Agents</router-link>
        <router-link to="/schedules" @click="mobileNavOpen = false" class="min-h-11 flex items-center px-3 py-2 rounded hover:bg-chatgpt-light"><i class="pi pi-calendar-clock mr-2"></i>Lịch tác vụ</router-link>
        <router-link to="/research" @click="mobileNavOpen = false" class="min-h-11 flex items-center px-3 py-2 rounded hover:bg-chatgpt-light"><i class="pi pi-compass mr-2"></i>Nghiên cứu</router-link>
        <router-link v-if="isAdmin" to="/admin/users" @click="mobileNavOpen = false" class="min-h-11 flex items-center px-3 py-2 rounded hover:bg-chatgpt-light"><i class="pi pi-users mr-2"></i>Quản lý User</router-link>
        <button @click="openMobileSettings" class="min-h-11 text-left px-3 py-2 rounded hover:bg-chatgpt-light"><i class="pi pi-cog mr-2"></i>Cấu hình</button>
        <button @click="logout" class="min-h-11 text-left px-3 py-2 rounded text-red-700 hover:bg-red-50"><i class="pi pi-sign-out mr-2"></i>Đăng xuất</button>
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

    <!-- Settings Modal -->
    <Dialog v-model:visible="showSettingsModal" modal header="Cấu hình hệ thống" :style="{ width: '65vw' }" :breakpoints="{ '1199px': '75vw', '575px': '90vw' }" :pt="{ root: 'overflow-hidden', content: 'p-0' }">
      <div class="flex h-[60vh]">
        <!-- Sidebar Tabs -->
        <div class="w-64 bg-gray-100 border-r border-gray-200 flex flex-col pt-2">
          <div class="flex-1 overflow-y-auto p-2">
            <button @click="activeTab = 'mcp'" :class="['w-full min-h-11 text-left px-3 py-2.5 rounded-lg mb-1 flex items-center gap-3 transition-colors text-sm font-medium', activeTab === 'mcp' ? 'bg-chatgpt-brand/10 text-sky-700' : 'text-gray-600 hover:bg-gray-200/50']">
              <i class="pi pi-server"></i> Máy chủ MCP
            </button>
          </div>
          <div class="p-2 border-t border-gray-200">
            <button @click="logout" class="w-full min-h-11 text-left px-3 py-2.5 rounded-lg flex items-center gap-3 text-red-700 hover:bg-red-400/10 transition-colors text-sm font-medium">
              <i class="pi pi-sign-out"></i> Đăng xuất
            </button>
          </div>
        </div>

        <!-- Content Area -->
        <div class="flex-1 flex flex-col bg-gray-50">
          <div class="flex-1 overflow-y-auto p-6 text-gray-700">
            <div v-if="activeTab === 'mcp'" class="animate-fade-in">
              <h3 class="text-xl font-medium text-gray-900 mb-2">Máy chủ Model Context Protocol (MCP)</h3>
              <p class="text-sm text-gray-600 mb-6">Kết nối MCP Streamable HTTP theo tenant. Header bí mật được mã hóa và không bao giờ trả lại giao diện.</p>
              <div v-if="!isAdmin" class="p-5 border border-amber-200 rounded-xl bg-amber-50 text-amber-700 text-sm">Chỉ Tenant Admin được quản lý máy chủ MCP.</div>
              <template v-else>
                <form @submit.prevent="createMcpServer" class="grid gap-3 p-4 bg-white border border-gray-200 rounded-xl mb-5">
                  <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
                    <div class="grid gap-1"><label for="mcp-name" class="text-sm font-medium">Tên máy chủ</label><InputText id="mcp-name" v-model="mcpForm.name" required placeholder="Ví dụ crm-tools" /></div>
                    <div class="grid gap-1"><label for="mcp-url" class="text-sm font-medium">URL máy chủ</label><InputText id="mcp-url" v-model="mcpForm.url" required type="url" placeholder="https://mcp.example.com" /></div>
                  </div>
                  <label for="mcp-headers" class="text-sm font-medium">Header JSON tùy chọn</label>
                  <Textarea id="mcp-headers" v-model="mcpForm.headersJson" rows="3" placeholder='Ví dụ {"Authorization":"Bearer ..."}' />
                  <label for="mcp-trust-readonly" class="flex items-start gap-2 text-sm text-amber-800 rounded-lg border border-amber-200 bg-amber-50 p-3">
                    <Checkbox inputId="mcp-trust-readonly" v-model="mcpForm.trustReadOnlyAnnotations" binary />
                    <span>Tin cậy khai báo <code>readOnlyHint</code> của máy chủ này. Chỉ bật khi đã xác minh nhà cung cấp; nếu tắt, mọi MCP tool đều cần phê duyệt.</span>
                  </label>
                  <div class="flex items-center justify-between gap-3">
                    <label for="mcp-active" class="flex items-center gap-2 text-sm"><Checkbox inputId="mcp-active" v-model="mcpForm.isActive" binary /> Kích hoạt</label>
                    <Button type="submit" label="Thêm máy chủ" icon="pi pi-plus" :loading="mcpSaving" class="!min-h-11 !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800" />
                  </div>
                </form>
                <div v-if="mcpError" role="alert" class="mb-4 p-3 rounded-lg bg-red-50 border border-red-200 text-red-700 text-sm">{{ mcpError }}</div>
                <div v-if="mcpServers.length === 0" class="p-8 border border-gray-100 rounded-xl bg-gray-200/50 text-center">
                  <i class="pi pi-database text-4xl text-gray-600 mb-3"></i>
                  <p class="text-gray-500 text-sm">Chưa có máy chủ MCP nào được kết nối.</p>
                </div>
                <div v-for="server in mcpServers" :key="server.id" class="flex items-center justify-between gap-3 p-4 mb-2 bg-white border border-gray-200 rounded-xl">
                  <div class="min-w-0">
                    <div class="font-medium truncate">{{ server.name }}</div>
                    <div class="text-xs text-gray-500 truncate">{{ server.url }}</div>
                    <div class="text-xs mt-1" :class="server.isActive ? 'text-green-600' : 'text-gray-400'">{{ server.isActive ? 'Đang hoạt động' : 'Đã tắt' }} · {{ server.hasHeaders ? 'Có header bảo mật' : 'Không có header' }} · {{ server.trustReadOnlyAnnotations ? 'Tin cậy read-only' : 'Mọi tool cần duyệt' }}</div>
                  </div>
                  <Button icon="pi pi-trash" severity="danger" text rounded aria-label="Xóa máy chủ MCP" class="!w-11 !h-11" @click="deleteMcpServer(server.id)" />
                </div>
              </template>
            </div>
            
          </div>
        </div>
      </div>
    </Dialog>
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
const userEmail = computed(() => authStore.currentUser?.email || '');
const userRole = computed(() => authStore.currentUser?.role || 'Member');

const isAdmin = computed(() => userRole.value === 'Admin');

const showSettingsModal = ref(false);
const activeTab = ref('mcp');
interface McpServer { id: string; name: string; url: string; isActive: boolean; hasHeaders: boolean; trustReadOnlyAnnotations: boolean }
const mcpServers = ref<McpServer[]>([]);
const mcpSaving = ref(false);
const mcpError = ref('');
const appError = ref('');
const mcpForm = ref({ name: '', url: '', headersJson: '', isActive: true, trustReadOnlyAnnotations: false });

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

const openSettings = async () => {
  showSettingsModal.value = true;
  if (isAdmin.value) await loadMcpServers();
};

const openMobileSettings = async () => {
  mobileNavOpen.value = false;
  await openSettings();
};

const newChat = async () => {
  mobileNavOpen.value = false;
  await router.push('/chat?new=' + Date.now());
};

const loadMcpServers = async () => {
  mcpError.value = '';
  try {
    const response = await http.get('/api/mcp-servers');
    if (response.ok) mcpServers.value = await response.json();
    else mcpError.value = await readApiError(response, 'Không thể tải cấu hình MCP');
  } catch (cause) {
    mcpError.value = errorMessage(cause, 'Không thể tải cấu hình MCP.');
  }
};

const createMcpServer = async () => {
  mcpError.value = '';
  let headers: Record<string, string> | undefined;
  try {
    headers = mcpForm.value.headersJson.trim() ? JSON.parse(mcpForm.value.headersJson) : undefined;
  } catch {
    mcpError.value = 'Header JSON không hợp lệ.';
    return;
  }
  mcpSaving.value = true;
  try {
    const response = await http.post('/api/mcp-servers', {
      name: mcpForm.value.name,
      url: mcpForm.value.url,
      headers,
      replaceHeaders: true,
      isActive: mcpForm.value.isActive,
      trustReadOnlyAnnotations: mcpForm.value.trustReadOnlyAnnotations
    });
    if (!response.ok) {
      mcpError.value = await readApiError(response, 'Không thể thêm máy chủ MCP');
      return;
    }
    mcpForm.value = { name: '', url: '', headersJson: '', isActive: true, trustReadOnlyAnnotations: false };
    await loadMcpServers();
  } catch (cause) {
    mcpError.value = errorMessage(cause, 'Không thể thêm máy chủ MCP.');
  } finally {
    mcpSaving.value = false;
  }
};

const deleteMcpServer = async (id: string) => {
  if (!confirm('Xóa máy chủ MCP này?')) return;
  mcpError.value = '';
  try {
    const response = await http.delete(`/api/mcp-servers/${id}`);
    if (response.ok) mcpServers.value = mcpServers.value.filter(server => server.id !== id);
    else mcpError.value = await readApiError(response, 'Không thể xóa máy chủ MCP');
  } catch (cause) {
    mcpError.value = errorMessage(cause, 'Không thể xóa máy chủ MCP.');
  }
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
