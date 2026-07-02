<template>
  <div class="flex-1 flex flex-col relative w-full h-full">
    <!-- Chat History -->
    <div ref="chatContainer" class="flex-1 overflow-y-auto scroll-smooth">
      <div v-if="messages.length === 0" class="h-full flex flex-col items-center justify-center text-center px-4">
        <div class="w-16 h-16 rounded-full bg-chatgpt-brand flex items-center justify-center mb-6 shadow-lg shadow-chatgpt-brand/20">
          <i class="pi pi-sparkles text-2xl text-white"></i>
        </div>
        <h1 class="text-3xl font-bold mb-2">Hôm nay tôi có thể giúp gì cho bạn?</h1>
        <p class="text-gray-600 max-w-md">Tôi là Trợ lý AI đa phương thức xây dựng trên LM-Kit.NET, có khả năng phân tích PDF, lưu trữ Vector và nhiều hơn thế.</p>
      </div>

      <div v-else class="max-w-3xl mx-auto w-full py-6 pb-32">
        <div v-for="(msg, index) in messages" :key="index" class="flex flex-col mb-8">
          
          <!-- User Message -->
          <div v-if="msg.role === 'user'" class="flex justify-end w-full group">
            <div class="flex flex-col items-end max-w-[80%]">
              <!-- User Attached Files -->
              <div v-if="msg.attachedFiles && msg.attachedFiles.length > 0" class="flex flex-wrap gap-2 mb-2 justify-end">
                <div v-for="(file, fi) in msg.attachedFiles" :key="fi" class="flex items-center gap-2 px-3 py-1.5 rounded-xl bg-blue-50 border border-blue-100 text-sm text-blue-700">
                  <i class="pi pi-file text-xs"></i>
                  <span class="max-w-[150px] truncate">{{ file }}</span>
                </div>
              </div>
              <div class="bg-white text-gray-900 px-5 py-3 rounded-3xl rounded-tr-sm shadow-sm">
                <div class="text-base font-medium whitespace-pre-wrap break-words">{{ getCleanUserContent(msg.content) }}</div>
              </div>
              <!-- User Action Buttons -->
              <div class="flex items-center gap-2 mt-2 opacity-0 group-hover:opacity-100 transition-opacity text-gray-500">
                <button class="p-1 hover:text-gray-900 transition-colors" title="Copy"><i class="pi pi-copy text-sm"></i></button>
                <button class="p-1 hover:text-gray-900 transition-colors" title="Edit"><i class="pi pi-pencil text-sm"></i></button>
              </div>
            </div>
          </div>

          <!-- Assistant Message -->
          <div v-else class="flex w-full group gap-4">
            <!-- Assistant Avatar -->
            <div class="flex-shrink-0 mt-1">
              <div class="w-8 h-8 rounded-full bg-chatgpt-dark border border-gray-200 flex items-center justify-center shadow-sm">
                <i class="pi pi-sparkles text-sm text-gray-700"></i>
              </div>
            </div>
            
            <div class="flex flex-col flex-1 min-w-0">
              <div class="font-semibold mb-1 text-sm text-gray-700">Trợ lý AI</div>

              <!-- Thinking Steps (Chain of Thought UI) -->
              <div v-if="msg.thinkingSteps && msg.thinkingSteps.length > 0" class="mb-4 flex flex-col gap-1 p-3 rounded-xl bg-gradient-to-br from-gray-50 to-gray-100 border border-gray-200 w-fit min-w-[280px] max-w-[90%]">
                <div class="text-[11px] font-semibold text-gray-400 uppercase tracking-wider mb-1">Quá trình suy luận</div>
                <div v-for="(step, idx) in msg.thinkingSteps" :key="idx" 
                  class="text-[13px] flex items-start gap-2 py-0.5 transition-all duration-300"
                  :class="idx === msg.thinkingSteps.length - 1 && msg.isTyping && !msg.content ? 'text-gray-700' : 'text-emerald-600'">
                  <span class="mt-0.5 flex-shrink-0">
                    <i class="pi pi-spin pi-spinner text-blue-500" v-if="idx === msg.thinkingSteps.length - 1 && msg.isTyping && !msg.content"></i>
                    <i class="pi pi-check-circle text-emerald-500" v-else></i>
                  </span>
                  <span class="leading-snug">{{ step }}</span>
                </div>
              </div>

              <!-- Web Search Chip -->
              <div v-if="msg.webUrls && msg.webUrls.length > 0" class="mb-3 flex items-center gap-2 cursor-pointer group/chip w-max" @click="openDrawer(msg.webUrls)">
                <div class="bg-blue-50 hover:bg-blue-100 text-gray-700 border border-gray-200 px-3 py-1.5 rounded-full flex items-center gap-2 transition-colors shadow-sm inline-flex">
                  <i class="pi pi-search text-xs"></i>
                  <span class="text-sm font-medium">Read {{ msg.webUrls.length }} web pages</span>
                  <div class="flex -space-x-1.5 ml-1">
                    <img v-for="(url, i) in msg.webUrls.slice(0, 3)" :key="i" :src="`https://www.google.com/s2/favicons?domain=${getHostname(url)}&sz=32`" class="w-5 h-5 rounded-full border border-[#202123] bg-white object-contain p-0.5" />
                  </div>
                </div>
              </div>

              <div class="text-base font-medium leading-relaxed whitespace-pre-wrap break-words text-gray-800 markdown-body" v-html="formatMessage(msg.content)"></div>
              
              <!-- Typing Indicator -->
              <div v-if="msg.isTyping" class="flex gap-1 mt-2">
                <div class="w-2 h-2 rounded-full bg-gray-500 animate-bounce"></div>
                <div class="w-2 h-2 rounded-full bg-gray-500 animate-bounce" style="animation-delay: 0.1s"></div>
                <div class="w-2 h-2 rounded-full bg-gray-500 animate-bounce" style="animation-delay: 0.2s"></div>
              </div>

              <!-- HITL Approval Card -->
              <div v-if="msg.hitlTaskId" class="mt-4 p-4 bg-orange-50 border border-orange-200 rounded-xl shadow-sm max-w-md">
                <div class="flex items-center gap-2 text-orange-800 font-semibold mb-2">
                  <i class="pi pi-exclamation-triangle"></i>
                  Yêu cầu xác nhận (Human-in-the-loop)
                </div>
                <div class="text-sm text-orange-700 mb-4">
                  Agent đang cố gắng thực thi một công cụ nhạy cảm. Hệ thống đã tạm dừng để chờ bạn phê duyệt.
                </div>
                <div class="flex gap-2" v-if="!msg.hitlResolved">
                  <button @click="approveTask(msg)" class="flex-1 px-4 py-2 bg-orange-600 hover:bg-orange-700 text-white text-sm font-medium rounded-lg transition-colors">
                    Phê duyệt
                  </button>
                  <button @click="rejectTask(msg)" class="flex-1 px-4 py-2 bg-white hover:bg-gray-50 text-gray-700 border border-gray-300 text-sm font-medium rounded-lg transition-colors">
                    Từ chối
                  </button>
                </div>
                <div v-else class="text-sm font-medium" :class="msg.hitlResolved === 'Approved' ? 'text-green-600' : 'text-red-600'">
                  Đã {{ msg.hitlResolved === 'Approved' ? 'Phê duyệt' : 'Từ chối' }} thao tác này.
                </div>
              </div>

              <!-- Assistant Action Buttons -->
              <div v-if="!msg.isTyping" class="flex items-center gap-2 mt-3 text-gray-500">
                <button class="p-1.5 hover:text-gray-900 hover:bg-gray-200/50 rounded-md transition-colors" title="Copy"><i class="pi pi-copy text-sm"></i></button>
                <button class="p-1.5 hover:text-gray-900 hover:bg-gray-200/50 rounded-md transition-colors" title="Retry"><i class="pi pi-refresh text-sm"></i></button>
                <button class="p-1.5 hover:text-gray-900 hover:bg-gray-200/50 rounded-md transition-colors" title="Good response"><i class="pi pi-thumbs-up text-sm"></i></button>
                <button class="p-1.5 hover:text-gray-900 hover:bg-gray-200/50 rounded-md transition-colors" title="Bad response"><i class="pi pi-thumbs-down text-sm"></i></button>
                <button class="p-1.5 hover:text-gray-900 hover:bg-gray-200/50 rounded-md transition-colors" title="Share"><i class="pi pi-share-alt text-sm"></i></button>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Input Area -->
    <!-- Hidden File Input -->
    <input type="file" ref="fileInputRef" class="hidden" multiple
      accept=".pdf,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.txt,.md,.jpg,.jpeg,.png,.bmp,.webp"
      @change="handleFileSelect" />

    <div class="absolute bottom-0 left-0 right-0 bg-gradient-to-t from-chatgpt-dark via-chatgpt-dark to-transparent pt-10 pb-6 px-4">
      <div class="max-w-3xl mx-auto relative group">
        <div class="relative flex flex-col bg-white border border-gray-200 rounded-[28px] p-2 shadow-sm">
          <!-- Attached Files Preview -->
          <div v-if="attachedFiles.length > 0" class="flex flex-wrap gap-2 px-3 pt-2">
            <div v-for="(file, index) in attachedFiles" :key="index"
              class="flex items-center gap-2 px-3 py-1.5 rounded-xl bg-blue-50 border border-blue-100 text-sm group/file">
              <i :class="getFileIconForInput(file.name)" class="text-xs"></i>
              <span class="max-w-[120px] truncate text-blue-700">{{ file.name }}</span>
              <span class="text-[10px] text-blue-400">({{ formatFileSize(file.size) }})</span>
              <button @click="removeFile(index)" class="ml-1 text-gray-400 hover:text-red-500 transition-colors">
                <i class="pi pi-times text-xs"></i>
              </button>
            </div>
          </div>
          
          <!-- Text Area -->
          <div class="px-3 pt-2">
            <Textarea 
              v-model="inputMessage" 
              @keydown.enter.prevent="sendMessage"
              class="w-full max-h-48 !bg-transparent !border-0 focus:!ring-0 resize-none !outline-none !shadow-none text-gray-800 text-base" 
              rows="1"
              autoResize
              placeholder="Nhắn tin cho Trợ lý AI..." />
          </div>
          
          <!-- Bottom Toolbar -->
          <div class="flex items-center justify-between mt-2 px-1 pb-1">
            <!-- Left Toggles (DeepThink / Search) -->
            <div class="flex items-center gap-2">
              <button class="flex items-center gap-2 px-3.5 py-1.5 rounded-full border border-gray-200 text-sm font-medium text-gray-600 hover:bg-gray-50 transition-colors">
                <i class="pi pi-sparkles"></i> DeepThink
              </button>
              <button class="flex items-center gap-2 px-3.5 py-1.5 rounded-full border border-sky-200 text-sm font-medium text-sky-600 bg-sky-50/50 hover:bg-sky-100 transition-colors">
                <i class="pi pi-globe"></i> Search
              </button>
            </div>
            
            <!-- Right Actions -->
            <div class="flex items-center gap-1.5">
              <button @click="triggerFileInput" class="w-9 h-9 flex items-center justify-center text-gray-600 hover:text-gray-900 transition-colors rounded-full hover:bg-gray-100" title="Đính kèm file">
                <i class="pi pi-paperclip text-lg"></i>
              </button>
              <Button 
                icon="pi pi-arrow-up" 
                @click="sendMessage"
                :disabled="(!inputMessage.trim() && attachedFiles.length === 0) || isGenerating"
                severity="info"
                rounded
                class="!w-9 !h-9"
              />
            </div>
          </div>
        </div>
        <div class="text-center text-xs text-gray-500 mt-3">
          Trợ lý AI phát triển bởi LM-Kit.NET có thể mắc sai lầm. Vui lòng kiểm tra lại các thông tin quan trọng.
        </div>
      </div>
    </div>

    <!-- Drawer: Web Search References -->
    <Drawer v-model:visible="isDrawerOpen" position="right" :style="{ width: '350px' }" class="bg-gray-50">
      <template #header>
        <h3 class="font-medium text-gray-900 flex items-center gap-2">
          <i class="pi pi-globe text-chatgpt-brand"></i> Nguồn tham khảo
        </h3>
      </template>
      <div class="flex flex-col gap-3 mt-2">
        <a v-for="(url, index) in drawerUrls" :key="index" :href="url" target="_blank" class="block p-3 rounded-xl border border-gray-100 bg-gray-200/50 hover:bg-gray-200 hover:border-gray-300 transition-all group">
          <div class="flex items-start gap-3">
            <div class="w-8 h-8 rounded-lg bg-white shadow-sm flex-shrink-0 flex items-center justify-center overflow-hidden">
                <img :src="`https://www.google.com/s2/favicons?domain=${getHostname(url)}&sz=32`" class="w-5 h-5 object-contain" />
            </div>
            <div class="flex-1 min-w-0">
              <div class="text-sm font-medium text-gray-800 truncate group-hover:text-cyan-400 transition-colors">{{ getCleanHostname(url) }}</div>
              <div class="text-xs text-gray-500 truncate mt-1">{{ url }}</div>
            </div>
          </div>
        </a>
      </div>
    </Drawer>
  </div>
</template>

<script setup lang="ts">
import { ref, nextTick, watch, onMounted } from 'vue';
import { useRoute } from 'vue-router';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';

interface Message {
  role: 'user' | 'assistant';
  content: string;
  isTyping?: boolean;
  webUrls?: string[];
  thinkingSteps?: string[];
  attachedFiles?: string[];
}

const inputMessage = ref('');
const messages = ref<Message[]>([]);
const isGenerating = ref(false);
const chatContainer = ref<HTMLElement | null>(null);
const currentSessionId = ref<string | null>(null);
const attachedFiles = ref<File[]>([]);
const fileInputRef = ref<HTMLInputElement | null>(null);

const loadMessages = async () => {
  if (!currentSessionId.value) return;
  try {
    const response = await http.get(ApiFactory.CHAT.GET_MESSAGES(currentSessionId.value));
    if (response.ok) {
      const data = await response.json();
      messages.value = data.map((m: any) => {
        let content = m.content;
        let webUrls: string[] | undefined = undefined;
        let thinkingSteps: string[] | undefined = undefined;

        // Xóa các log rác nếu có lưu nhầm
        content = content.replace(/\[Agent invoked:.*?\][\n\r]*/g, '');

        if (content.includes('[THINKING]:')) {
          const thinkingMatches = content.match(/\[THINKING\]:([^\n\r]+)/g);
          if (thinkingMatches) {
            thinkingSteps = thinkingMatches.map((match: string) => match.replace('[THINKING]:', '').trim());
            content = content.replace(/\[THINKING\]:[^\n\r]+[\n\r]*/g, '').trimStart();
          }
        }

        if (content.includes('[WEB_SEARCH]:')) {
          const match = content.match(/\[WEB_SEARCH\]:([^\n\r]+)/);
          if (match) {
            webUrls = match[1].split('|').filter((u: string) => u);
            content = content.replace(/\[WEB_SEARCH\]:[^\n\r]+[\n\r]*/, '').trimStart();
          }
        }

        return {
          role: m.role.toLowerCase(),
          content: content,
          webUrls: webUrls,
          thinkingSteps: thinkingSteps
        };
      });
      await scrollToBottom();
    }
  } catch (error) {
    console.error("Failed to load messages", error);
  }
};
const route = useRoute();

const isDrawerOpen = ref(false);
const drawerUrls = ref<string[]>([]);

const openDrawer = (urls: string[]) => {
  drawerUrls.value = urls;
  isDrawerOpen.value = true;
};

const getHostname = (urlStr: string) => {
    try { return new URL(urlStr).hostname; } catch { return ''; }
};

const getCleanHostname = (urlStr: string) => {
    try { return new URL(urlStr).hostname.replace('www.', ''); } catch { return 'Website'; }
};

onMounted(() => {
  if (route.query.id) {
    currentSessionId.value = route.query.id as string;
    loadMessages();
  }
});

watch(() => route.query.id, (newId) => {
  if (newId && typeof newId === 'string') {
    currentSessionId.value = newId;
    loadMessages();
  } else if (route.query.new) {
    currentSessionId.value = null;
    messages.value = [];
  }
});

const scrollToBottom = async () => {
  await nextTick();
  if (chatContainer.value) {
    chatContainer.value.scrollTop = chatContainer.value.scrollHeight;
  }
};

const formatMessage = (text: string) => {
  return text.replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>').replace(/\n/g, '<br/>');
};

const triggerFileInput = () => {
  fileInputRef.value?.click();
};

const handleFileSelect = (event: Event) => {
  const input = event.target as HTMLInputElement;
  if (input.files) {
    attachedFiles.value.push(...Array.from(input.files));
  }
  input.value = ''; // Reset so same file can be selected again
};

const removeFile = (index: number) => {
  attachedFiles.value.splice(index, 1);
};

const formatFileSize = (bytes: number) => {
  if (bytes < 1024) return bytes + ' B';
  if (bytes < 1048576) return (bytes / 1024).toFixed(1) + ' KB';
  return (bytes / 1048576).toFixed(1) + ' MB';
};

const getFileIconForInput = (name: string) => {
  const ext = name.split('.').pop()?.toLowerCase();
  if (['jpg','jpeg','png','bmp','webp','gif'].includes(ext || '')) return 'pi pi-image text-green-600';
  if (ext === 'pdf') return 'pi pi-file-pdf text-red-600';
  if (['doc','docx'].includes(ext || '')) return 'pi pi-file-word text-blue-600';
  if (['xls','xlsx'].includes(ext || '')) return 'pi pi-file-excel text-emerald-600';
  return 'pi pi-file text-gray-600';
};

// Remove file context block from user message display
const getCleanUserContent = (content: string) => {
  return content.replace(/\n\n--- Nội dung file đính kèm ---[\s\S]*/g, '').trim();
};

const sendMessage = async () => {
  const content = inputMessage.value.trim();
  const hasFiles = attachedFiles.value.length > 0;
  if ((!content && !hasFiles) || isGenerating.value) return;

  const fileNames = attachedFiles.value.map(f => f.name);
  messages.value.push({ role: 'user', content: content || `📎 ${fileNames.join(', ')}`, attachedFiles: fileNames.length > 0 ? fileNames : undefined });
  inputMessage.value = '';
  await scrollToBottom();

  isGenerating.value = true;
  messages.value.push({ role: 'assistant', content: '', isTyping: true });
  const assistantMsg = messages.value[messages.value.length - 1];
  await scrollToBottom();

  if (!currentSessionId.value) {
      const sessionRes = await http.post(ApiFactory.CHAT.CREATE_SESSION);
      if (sessionRes.ok) {
          const newSession = await sessionRes.json();
          currentSessionId.value = newSession.id;
          window.dispatchEvent(new CustomEvent('chat-session-created'));
      }
  }

  try {
    let response: Response;

    if (hasFiles) {
      // Multipart: send with files
      const formData = new FormData();
      formData.append('sessionId', currentSessionId.value || '00000000-0000-0000-0000-000000000000');
      formData.append('message', content || 'Hãy phân tích nội dung file đính kèm.');
      for (const file of attachedFiles.value) {
        formData.append('files', file);
      }
      attachedFiles.value = []; // Clear after sending
      response = await http.post(ApiFactory.CHAT.STREAM_WITH_FILES, formData);
    } else {
      // JSON: text only
      const payload = {
        SessionId: currentSessionId.value || '00000000-0000-0000-0000-000000000000',
        Message: content,
        ModelId: null
      };
      response = await http.post(ApiFactory.CHAT.STREAM, payload);
    }

    if (!response.body) throw new Error('ReadableStream not supported');

    const reader = response.body.getReader();
    const decoder = new TextDecoder('utf-8');

    assistantMsg.isTyping = false;

    let buffer = '';
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      let lineEnd;
      while ((lineEnd = buffer.indexOf('\n')) !== -1) {
        let line = buffer.substring(0, lineEnd);
        if (line.endsWith('\r')) {
            line = line.substring(0, line.length - 1);
        }
        buffer = buffer.substring(lineEnd + 1);

        if (line.startsWith('data:')) {
          let data = line.substring(5);
          if (data.startsWith(' ')) data = data.substring(1);

          if (data === '[DONE]') break;
          
          if (data.startsWith('[WEB_SEARCH]:')) {
              const urls = data.replace('[WEB_SEARCH]:', '').split('|').filter(u => u);
              assistantMsg.webUrls = urls;
              continue;
          }
          if (data.startsWith('[THINKING]:')) {
              const thinkingMsg = data.replace('[THINKING]:', '').trim();
              if (!assistantMsg.thinkingSteps) {
                  assistantMsg.thinkingSteps = [];
              }
              assistantMsg.thinkingSteps.push(thinkingMsg);
              await scrollToBottom();
              continue;
          }
          if (data.startsWith('[HITL_APPROVAL_REQUIRED:')) {
              const taskId = data.replace('[HITL_APPROVAL_REQUIRED:', '').replace(']', '').trim();
              assistantMsg.hitlTaskId = taskId;
              await scrollToBottom();
              break;
          }
          if (data.startsWith('[Agent invoked:')) continue; // Ẩn log thô của agent

          assistantMsg.content += data;
          await scrollToBottom();
        }
      }
    }
  } catch (error) {
    assistantMsg.content = `Error: ${error instanceof Error ? error.message : 'Unknown error occurred'}`;
    assistantMsg.isTyping = false;
  } finally {
    isGenerating.value = false;
    window.dispatchEvent(new CustomEvent('chat-session-created'));
  }
};

const approveTask = async (msg: any) => {
  try {
    msg.hitlResolved = 'Approved';
    const res = await http.post(`/api/TaskApproval/${msg.hitlTaskId}/approve`);
    if (res.ok) {
      const result = await res.json();
      messages.value.push({
        role: 'system',
        content: `Đã phê duyệt. Kết quả thực thi tool: ${result.Result}`
      });
      // Optionally trigger agent to continue by sending a hidden prompt
      inputMessage.value = `Tôi đã phê duyệt hành động trên. Kết quả thực thi là: ${result.Result}. Vui lòng tiếp tục.`;
      await sendMessage();
    }
  } catch (error) {
    console.error('Failed to approve task', error);
  }
};

const rejectTask = async (msg: any) => {
  try {
    msg.hitlResolved = 'Rejected';
    await http.post(`/api/TaskApproval/${msg.hitlTaskId}/reject`, { Comment: "User rejected" });
    messages.value.push({
      role: 'system',
      content: `Đã từ chối hành động.`
    });
  } catch (error) {
    console.error('Failed to reject task', error);
  }
};
</script>
