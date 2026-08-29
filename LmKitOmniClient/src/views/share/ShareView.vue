<template>
  <div class="flex-1 min-h-0 overflow-y-auto bg-chatgpt-dark text-chatgpt-text font-sans">
    <div class="max-w-3xl mx-auto w-full px-4 py-8">

      <!-- Brand header (no authenticated app chrome) -->
      <header class="flex items-center gap-3 pb-6 border-b border-gray-200 mb-8">
        <div class="w-10 h-10 rounded-full bg-chatgpt-brand flex items-center justify-center shadow-lg shadow-chatgpt-brand/20 flex-shrink-0">
          <i class="pi pi-sparkles text-lg text-white" aria-hidden="true"></i>
        </div>
        <div class="min-w-0">
          <div class="font-semibold text-gray-900">Trợ lý AI — LM-Kit.NET</div>
          <div class="text-xs text-gray-500">Đoạn chat được chia sẻ công khai (chỉ xem)</div>
        </div>
      </header>

      <!-- Loading -->
      <div v-if="state === 'loading'" class="flex flex-col items-center justify-center py-20 text-gray-500" role="status">
        <i class="pi pi-spin pi-spinner text-3xl mb-3" aria-hidden="true"></i>
        <p class="text-sm">Đang tải đoạn chat được chia sẻ...</p>
      </div>

      <!-- Invalid / revoked link -->
      <div v-else-if="state === 'not-found'" class="flex flex-col items-center justify-center py-20 text-center">
        <div class="w-14 h-14 rounded-full bg-gray-200/70 flex items-center justify-center mb-4">
          <i class="pi pi-link text-2xl text-gray-500" aria-hidden="true"></i>
        </div>
        <h1 class="text-xl font-semibold text-gray-900 mb-2">Không tìm thấy đoạn chat</h1>
        <p class="text-sm text-gray-600 max-w-md" role="alert">Liên kết không tồn tại hoặc đã bị thu hồi.</p>
      </div>

      <!-- Unexpected failure -->
      <div v-else-if="state === 'error'" class="flex flex-col items-center justify-center py-20 text-center">
        <div class="w-14 h-14 rounded-full bg-red-50 flex items-center justify-center mb-4">
          <i class="pi pi-exclamation-triangle text-2xl text-red-500" aria-hidden="true"></i>
        </div>
        <h1 class="text-xl font-semibold text-gray-900 mb-2">Đã có lỗi xảy ra</h1>
        <p class="text-sm text-gray-600 max-w-md" role="alert">Không thể tải đoạn chat được chia sẻ. Vui lòng thử lại sau.</p>
      </div>

      <!-- Shared conversation -->
      <template v-else-if="conversation">
        <h1 class="text-2xl font-bold text-gray-900 mb-1 break-words">{{ conversation.title || 'Đoạn chat được chia sẻ' }}</h1>
        <p v-if="formattedCreatedAt" class="text-xs text-gray-500 mb-8">Tạo lúc {{ formattedCreatedAt }}</p>

        <p v-if="displayMessages.length === 0" class="text-sm text-gray-600 italic">Đoạn chat này chưa có tin nhắn nào.</p>

        <div v-else role="log" aria-label="Nội dung đoạn chat được chia sẻ">
          <div v-for="(msg, index) in displayMessages" :key="index" class="flex flex-col mb-8">

            <!-- User message -->
            <div v-if="msg.isUser" class="flex justify-end w-full">
              <div class="bg-white text-gray-900 px-5 py-3 rounded-3xl rounded-tr-sm shadow-sm max-w-[80%]">
                <div class="text-base font-medium whitespace-pre-wrap break-words">{{ msg.content }}</div>
              </div>
            </div>

            <!-- Assistant message -->
            <div v-else class="flex w-full gap-4">
              <div class="flex-shrink-0 mt-1">
                <div class="w-8 h-8 rounded-full bg-chatgpt-dark border border-gray-200 flex items-center justify-center shadow-sm">
                  <i class="pi pi-sparkles text-sm text-gray-700" aria-hidden="true"></i>
                </div>
              </div>
              <div class="flex flex-col flex-1 min-w-0">
                <div class="font-semibold mb-1 text-sm text-gray-700">Trợ lý AI</div>
                <GenerativeUiRenderer :content="msg.content" />
              </div>
            </div>
          </div>
        </div>

        <footer class="mt-10 pt-4 border-t border-gray-200 text-center text-xs text-gray-500">
          Nội dung do Trợ lý AI tạo ra có thể chứa sai sót. Vui lòng kiểm tra lại các thông tin quan trọng.
        </footer>
      </template>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { useRoute } from 'vue-router';
import { API_BASE_URL } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import GenerativeUiRenderer from '@/components/chat/GenerativeUiRenderer.vue';
import { getCleanUserContent, parseStoredAssistantContent } from '@/composables/useChatStream';

/** Payload of the PUBLIC `GET /api/share/chat/{token}` endpoint. */
interface SharedMessage {
  role: string;
  content: string;
  createdAt: string;
}

interface SharedConversation {
  title: string;
  createdAt: string;
  messages: SharedMessage[];
}

const route = useRoute();

const state = ref<'loading' | 'ready' | 'not-found' | 'error'>('loading');
const conversation = ref<SharedConversation | null>(null);

/**
 * IMPORTANT: this page is anonymous. It deliberately uses a PLAIN `fetch`
 * against the same API base as `http` — going through `http` would trigger
 * the 401 → refresh → redirect-to-/login machinery on invalid tokens.
 */
const loadSharedConversation = async () => {
  const token = route.params.token;
  if (typeof token !== 'string' || !token.trim()) {
    state.value = 'not-found';
    return;
  }

  try {
    const response = await fetch(`${API_BASE_URL}${ApiFactory.SHARE.GET_SHARED_CHAT(token)}`);
    if (response.status === 404) {
      state.value = 'not-found';
      return;
    }
    if (!response.ok) {
      state.value = 'error';
      return;
    }
    const data = await response.json() as SharedConversation;
    conversation.value = {
      title: typeof data.title === 'string' ? data.title : '',
      createdAt: typeof data.createdAt === 'string' ? data.createdAt : '',
      messages: Array.isArray(data.messages) ? data.messages : []
    };
    state.value = 'ready';
  } catch {
    state.value = 'error';
  }
};

onMounted(loadSharedConversation);

const formattedCreatedAt = computed(() => {
  const raw = conversation.value?.createdAt;
  if (!raw) return '';
  const date = new Date(raw);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleString('vi-VN');
});

/**
 * Messages prepared for display: user content is cleaned of attached-file
 * blocks, assistant content is stripped of [THINKING]/[WEB_SEARCH]/agent-log
 * markers via the SAME utilities the authenticated chat uses. Assistant HTML
 * is rendered by GenerativeUiRenderer, whose only v-html goes through
 * formatSafeMessage.
 */
const displayMessages = computed(() => {
  const messages = conversation.value?.messages ?? [];
  return messages
    .filter((msg) => typeof msg.content === 'string')
    .map((msg) => {
      const isUser = (msg.role || '').toLowerCase() === 'user';
      return {
        isUser,
        content: isUser
          ? getCleanUserContent(msg.content)
          : parseStoredAssistantContent(msg.content).content
      };
    });
});
</script>
