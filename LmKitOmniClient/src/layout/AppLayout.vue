<template>
  <div class="flex h-screen bg-chatgpt-dark text-chatgpt-text font-sans">
    
    <!-- Sidebar -->
    <div class="w-64 bg-gray-100 flex flex-col hidden md:flex transition-all duration-300">
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

        <router-link v-if="isAdmin" to="/admin/users" class="w-full flex items-center gap-3 px-3 py-3 hover:bg-chatgpt-light font-medium rounded-md transition-colors cursor-pointer mt-1" active-class="bg-chatgpt-light border border-gray-200">
          <i class="pi pi-users"></i>
          <span>Quản lý User</span>
        </router-link>
      </div>
      
      <div class="flex-1 overflow-y-auto p-3 mt-2 flex flex-col">
        <div class="text-xs text-gray-500 font-semibold mb-2 px-3 uppercase tracking-wider flex items-center justify-between">
          <span>Lịch sử trò chuyện</span>
          <button @click="router.push('/chat?new=' + Date.now())" class="hover:text-gray-900 transition-colors rounded-md hover:bg-gray-200 w-6 h-6 flex items-center justify-center" title="Tạo phiên chat mới">
            <i class="pi pi-plus"></i>
          </button>
        </div>
        <div v-if="chatSessions.length === 0" class="px-3 py-2 text-xs text-gray-500 italic">
          Chưa có phiên chat nào.
        </div>
        <div v-for="session in chatSessions" :key="session.id" class="w-full flex items-center justify-between gap-3 px-3 py-2 text-gray-700 hover:text-gray-900 font-medium hover:bg-chatgpt-light rounded-md transition-colors text-sm truncate group mt-1">
          <div class="flex items-center gap-3 truncate flex-1 cursor-pointer" @click="selectSession(session.id)">
            <i class="pi pi-message text-gray-500 group-hover:text-gray-700"></i>
            <span class="truncate text-left flex-1">{{ session.title || 'Đoạn chat mới' }}</span>
          </div>
          <button @click.stop="deleteSession(session.id)" class="text-gray-500 hover:text-red-400 opacity-0 group-hover:opacity-100 p-1 rounded hover:bg-gray-200/50 transition-all flex-shrink-0 cursor-pointer" title="Xóa đoạn chat">
            <i class="pi pi-trash text-xs"></i>
          </button>
        </div>
      </div>
      
      <div class="p-3 border-t border-gray-200 flex items-center gap-1">
        <button @click="showSettingsModal = true" class="flex-1 flex items-center gap-3 overflow-hidden px-2 py-2 hover:bg-chatgpt-light rounded-md transition-colors text-left cursor-pointer group">
          <div class="w-8 h-8 rounded-full bg-gradient-to-r from-purple-500 to-pink-500 flex items-center justify-center flex-shrink-0 shadow-sm">
            <span class="text-xs font-bold text-white">{{ userInitials }}</span>
          </div>
          <div class="overflow-hidden flex-1">
            <div class="text-sm font-medium truncate text-gray-900">{{ userName }}</div>
            <div class="text-xs text-gray-500 truncate">{{ userEmail }}</div>
          </div>
          <i class="pi pi-cog text-gray-500 opacity-0 group-hover:opacity-100 transition-opacity hover:text-gray-900"></i>
        </button>
        
        <button @click="logout" class="p-3 text-gray-500 hover:text-red-400 hover:bg-chatgpt-light rounded-md transition-colors cursor-pointer flex-shrink-0" title="Đăng xuất">
          <i class="pi pi-sign-out"></i>
        </button>
      </div>
    </div>

    <!-- Main Content Area -->
    <div class="flex-1 flex flex-col relative bg-chatgpt-dark min-w-0">
      
      <!-- Header (Mobile Toggle) -->
      <header class="md:hidden flex items-center justify-between p-4 border-b border-gray-200 bg-gray-100">
        <button><i class="pi pi-bars text-xl"></i></button>
        <span class="font-medium">Nền tảng Trợ lý AI</span>
        <button><i class="pi pi-plus text-xl"></i></button>
      </header>

      <!-- Router View -->
      <router-view v-slot="{ Component }">
        <transition name="fade" mode="out-in">
          <component :is="Component" />
        </transition>
      </router-view>
      
    </div>

    <!-- Settings Modal -->
    <Dialog v-model:visible="showSettingsModal" modal header="Cấu hình hệ thống" :style="{ width: '65vw' }" :breakpoints="{ '1199px': '75vw', '575px': '90vw' }" :pt="{ root: 'overflow-hidden', content: 'p-0' }">
      <div class="flex h-[60vh]">
        <!-- Sidebar Tabs -->
        <div class="w-64 bg-gray-100 border-r border-gray-200 flex flex-col pt-2">
          <div class="flex-1 overflow-y-auto p-2">
            <button @click="activeTab = 'mcp'" :class="['w-full text-left px-3 py-2.5 rounded-lg mb-1 flex items-center gap-3 transition-colors text-sm font-medium', activeTab === 'mcp' ? 'bg-chatgpt-brand/10 text-sky-700' : 'text-gray-600 hover:bg-gray-200/50']">
              <i class="pi pi-server"></i> Máy chủ MCP
            </button>
            <button @click="activeTab = 'api'" :class="['w-full text-left px-3 py-2.5 rounded-lg mb-1 flex items-center gap-3 transition-colors text-sm font-medium', activeTab === 'api' ? 'bg-chatgpt-brand/10 text-sky-700' : 'text-gray-600 hover:bg-gray-200/50']">
              <i class="pi pi-key"></i> Mã Khóa API
            </button>
            <button @click="activeTab = 'widget'" :class="['w-full text-left px-3 py-2.5 rounded-lg mb-1 flex items-center gap-3 transition-colors text-sm font-medium', activeTab === 'widget' ? 'bg-chatgpt-brand/10 text-sky-700' : 'text-gray-600 hover:bg-gray-200/50']">
              <i class="pi pi-objects-column"></i> Widget Plugin
            </button>
          </div>
          <div class="p-2 border-t border-gray-200">
            <button @click="logout" class="w-full text-left px-3 py-2.5 rounded-lg flex items-center gap-3 text-red-400 hover:bg-red-400/10 transition-colors text-sm font-medium">
              <i class="pi pi-sign-out"></i> Đăng xuất
            </button>
          </div>
        </div>

        <!-- Content Area -->
        <div class="flex-1 flex flex-col bg-gray-50">
          <div class="flex-1 overflow-y-auto p-6 text-gray-700">
            <div v-if="activeTab === 'mcp'" class="animate-fade-in">
              <h3 class="text-xl font-medium text-gray-900 mb-2">Máy chủ Model Context Protocol (MCP)</h3>
              <p class="text-sm text-gray-600 mb-6">Kết nối nền tảng Trợ lý AI với các nguồn dữ liệu bên ngoài.</p>
              <div class="p-8 border border-gray-100 rounded-xl bg-gray-200/50 text-center">
                <i class="pi pi-database text-4xl text-gray-600 mb-3"></i>
                <p class="text-gray-500 text-sm">Chưa có máy chủ MCP nào được kết nối.</p>
              </div>
            </div>
            
            <div v-if="activeTab === 'api'" class="animate-fade-in">
              <h3 class="text-xl font-medium text-gray-900 mb-2">Mã Khóa API (API Keys)</h3>
              <p class="text-sm text-gray-600 mb-6">Quản lý khóa bí mật để sử dụng các công cụ LM-Kit.</p>
              <div class="p-8 border border-gray-100 rounded-xl bg-gray-200/50 text-center">
                <i class="pi pi-key text-4xl text-gray-600 mb-3"></i>
                <p class="text-gray-500 text-sm">Cấu hình API Keys sẽ xuất hiện ở đây.</p>
              </div>
            </div>
            
            <div v-if="activeTab === 'widget'" class="animate-fade-in">
              <h3 class="text-xl font-medium text-gray-900 mb-2">Widget Plugin</h3>
              <p class="text-sm text-gray-600 mb-6">Bật/tắt các tiện ích mở rộng giao diện của người dùng.</p>
              <div class="p-8 border border-gray-100 rounded-xl bg-gray-200/50 text-center">
                <i class="pi pi-objects-column text-4xl text-gray-600 mb-3"></i>
                <p class="text-gray-500 text-sm">Hệ thống Widget đang được phát triển.</p>
              </div>
            </div>
          </div>
        </div>
      </div>
    </Dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, computed } from 'vue';
import { useRouter } from 'vue-router';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';

interface ChatSession {
  id: string;
  title: string;
  createdAt: string;
}

const router = useRouter();
const userName = ref('Người dùng');
const userEmail = ref('user@lmkit.net');
const userRole = ref('Member');

const isAdmin = computed(() => userRole.value === 'Admin');

const showSettingsModal = ref(false);
const activeTab = ref('mcp');

const chatSessions = ref<ChatSession[]>([]);

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
  const userJson = localStorage.getItem('hermes_user');
  if (userJson) {
    try {
      const user = JSON.parse(userJson);
      userName.value = user.fullName || user.username || 'User';
      userEmail.value = user.email || '';
      userRole.value = user.role || 'Member';
      
      // Load sessions after getting user info
      loadChatSessions();
      
      // Lắng nghe sự kiện khi có phiên chat mới được tạo
      window.addEventListener('chat-session-created', loadChatSessions);
    } catch (e) {
      console.error(e);
    }
  }
});

const logout = () => {
  localStorage.removeItem('hermes_token');
  localStorage.removeItem('hermes_user');
  router.push('/login');
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
