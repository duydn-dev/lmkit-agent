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
        <div v-if="chatSessions.length === 0" class="px-3 py-2 text-xs text-gray-500 italic">
          Chưa có phiên chat nào.
        </div>
        <div v-for="session in chatSessions" :key="session.id" class="w-full flex items-center justify-between gap-3 px-3 py-2 text-gray-700 hover:text-gray-900 font-medium hover:bg-chatgpt-light rounded-md transition-colors text-sm truncate group mt-1">
          <button type="button" class="flex items-center gap-3 truncate flex-1 cursor-pointer text-left min-h-11" @click="selectSession(session.id)">
            <i class="pi pi-message text-gray-500 group-hover:text-gray-700"></i>
            <span class="truncate text-left flex-1">{{ session.title || 'Đoạn chat mới' }}</span>
          </button>
          <button @click.stop="deleteSession(session.id)" class="text-gray-500 hover:text-red-600 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 w-11 h-11 rounded hover:bg-gray-200/50 transition-all flex-shrink-0 cursor-pointer" :aria-label="`Xóa đoạn chat ${session.title || 'mới'}`">
            <i class="pi pi-trash text-xs"></i>
          </button>
        </div>
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
      
      <!-- Header (Mobile Toggle) -->
      <header class="md:hidden flex items-center justify-between p-4 border-b border-gray-200 bg-gray-100">
        <button @click="mobileNavOpen = !mobileNavOpen" :aria-expanded="mobileNavOpen" aria-controls="mobile-navigation" class="w-11 h-11" :aria-label="mobileNavOpen ? 'Đóng menu điều hướng' : 'Mở menu điều hướng'"><i class="pi pi-bars text-xl"></i></button>
        <span class="font-medium">Nền tảng Trợ lý AI</span>
        <button @click="newChat" class="w-11 h-11" aria-label="Tạo phiên chat mới"><i class="pi pi-plus text-xl"></i></button>
      </header>

      <nav v-if="mobileNavOpen" id="mobile-navigation" class="md:hidden bg-gray-100 border-b border-gray-200 p-3 grid gap-1" aria-label="Điều hướng di động">
        <router-link to="/chat" @click="mobileNavOpen = false" class="min-h-11 flex items-center px-3 py-2 rounded hover:bg-chatgpt-light"><i class="pi pi-sparkles mr-2"></i>Trợ lý AI</router-link>
        <router-link to="/documents" @click="mobileNavOpen = false" class="min-h-11 flex items-center px-3 py-2 rounded hover:bg-chatgpt-light"><i class="pi pi-file-pdf mr-2"></i>Kho tài liệu</router-link>
        <router-link to="/memory" @click="mobileNavOpen = false" class="min-h-11 flex items-center px-3 py-2 rounded hover:bg-chatgpt-light"><i class="pi pi-history mr-2"></i>Bộ nhớ trợ lý</router-link>
        <router-link v-if="isAdmin" to="/admin/users" @click="mobileNavOpen = false" class="min-h-11 flex items-center px-3 py-2 rounded hover:bg-chatgpt-light"><i class="pi pi-users mr-2"></i>Quản lý User</router-link>
        <button @click="openMobileSettings" class="min-h-11 text-left px-3 py-2 rounded hover:bg-chatgpt-light"><i class="pi pi-cog mr-2"></i>Cấu hình</button>
        <button @click="logout" class="min-h-11 text-left px-3 py-2 rounded text-red-600 hover:bg-red-50"><i class="pi pi-sign-out mr-2"></i>Đăng xuất</button>
      </nav>

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
            <button @click="logout" class="w-full min-h-11 text-left px-3 py-2.5 rounded-lg flex items-center gap-3 text-red-600 hover:bg-red-400/10 transition-colors text-sm font-medium">
              <i class="pi pi-sign-out"></i> Đăng xuất
            </button>
          </div>
        </div>

        <!-- Content Area -->
        <div class="flex-1 flex flex-col bg-gray-50">
          <div class="flex-1 overflow-y-auto p-6 text-gray-700">
            <div v-if="activeTab === 'mcp'" class="animate-fade-in">
              <h3 class="text-xl font-medium text-gray-900 mb-2">Máy chủ Model Context Protocol (MCP)</h3>
              <p class="text-sm text-gray-600 mb-6">Kết nối REST MCP adapter theo tenant. Header bí mật được mã hóa và không bao giờ trả lại giao diện.</p>
              <div v-if="!isAdmin" class="p-5 border border-amber-200 rounded-xl bg-amber-50 text-amber-700 text-sm">Chỉ Tenant Admin được quản lý máy chủ MCP.</div>
              <template v-else>
                <form @submit.prevent="createMcpServer" class="grid gap-3 p-4 bg-white border border-gray-200 rounded-xl mb-5">
                  <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
                    <div class="grid gap-1"><label for="mcp-name" class="text-sm font-medium">Tên máy chủ</label><InputText id="mcp-name" v-model="mcpForm.name" required placeholder="Ví dụ crm-tools" /></div>
                    <div class="grid gap-1"><label for="mcp-url" class="text-sm font-medium">URL máy chủ</label><InputText id="mcp-url" v-model="mcpForm.url" required type="url" placeholder="https://mcp.example.com" /></div>
                  </div>
                  <label for="mcp-headers" class="text-sm font-medium">Header JSON tùy chọn</label>
                  <Textarea id="mcp-headers" v-model="mcpForm.headersJson" rows="3" placeholder='Ví dụ {"Authorization":"Bearer ..."}' />
                  <div class="flex items-center justify-between gap-3">
                    <label for="mcp-active" class="flex items-center gap-2 text-sm"><Checkbox inputId="mcp-active" v-model="mcpForm.isActive" binary /> Kích hoạt</label>
                    <Button type="submit" label="Thêm máy chủ" icon="pi pi-plus" :loading="mcpSaving" class="!min-h-11" />
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
                    <div class="text-xs mt-1" :class="server.isActive ? 'text-green-600' : 'text-gray-400'">{{ server.isActive ? 'Đang hoạt động' : 'Đã tắt' }} · {{ server.hasHeaders ? 'Có header bảo mật' : 'Không có header' }}</div>
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
import { ref, onMounted, onUnmounted, computed } from 'vue';
import { useRouter } from 'vue-router';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
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
interface McpServer { id: string; name: string; url: string; isActive: boolean; hasHeaders: boolean }
const mcpServers = ref<McpServer[]>([]);
const mcpSaving = ref(false);
const mcpError = ref('');
const mcpForm = ref({ name: '', url: '', headersJson: '', isActive: true });

const chatSessions = ref<ChatSession[]>([]);
const mobileNavOpen = ref(false);

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
  const response = await http.get('/api/mcp-servers');
  if (response.ok) mcpServers.value = await response.json();
  else mcpError.value = 'Không thể tải cấu hình MCP.';
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
      isActive: mcpForm.value.isActive
    });
    if (!response.ok) {
      mcpError.value = await response.text() || 'Không thể thêm máy chủ MCP.';
      return;
    }
    mcpForm.value = { name: '', url: '', headersJson: '', isActive: true };
    await loadMcpServers();
  } finally {
    mcpSaving.value = false;
  }
};

const deleteMcpServer = async (id: string) => {
  if (!confirm('Xóa máy chủ MCP này?')) return;
  const response = await http.delete(`/api/mcp-servers/${id}`);
  if (response.ok) mcpServers.value = mcpServers.value.filter(server => server.id !== id);
  else mcpError.value = 'Không thể xóa máy chủ MCP.';
};

const selectSession = (id: string) => {
  router.push(`/chat?id=${id}`);
};

const deleteSession = async (id: string) => {
  if (!confirm("Bạn có chắc chắn muốn xóa đoạn chat này không?")) return;
  try {
    const response = await http.delete(ApiFactory.CHAT.DELETE_SESSION(id));
    if (response.ok) {
      const route = router.currentRoute.value;
      if (route.query.id === id) {
        router.push('/chat?new=' + Date.now());
      }
      loadChatSessions();
    }
  } catch (error) {
    console.error("Failed to delete session", error);
  }
};

const loadChatSessions = async () => {
  try {
    const response = await http.get(ApiFactory.CHAT.SESSIONS);
    if (response.ok) {
      chatSessions.value = await response.json();
    }
  } catch (error) {
    console.error("Failed to load chat sessions", error);
  }
};

const userInitials = computed(() => {
  return userName.value.substring(0, 2).toUpperCase();
});

onMounted(() => {
  loadChatSessions();
  window.addEventListener('chat-session-created', loadChatSessions);
});

onUnmounted(() => {
  window.removeEventListener('chat-session-created', loadChatSessions);
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
