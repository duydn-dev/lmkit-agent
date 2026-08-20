<template>
  <div class="flex-1 flex flex-col relative w-full h-full">
    <!-- Chat History -->
    <div ref="chatContainer" class="flex-1 overflow-y-auto scroll-smooth" role="log" aria-live="polite" aria-relevant="additions text" aria-label="Lịch sử trò chuyện">
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
              <!-- User Action -->
              <div class="flex items-center gap-2 mt-2 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity text-gray-500">
                <button @click="copyMessage(getCleanUserContent(msg.content))" class="w-11 h-11 hover:text-gray-900 transition-colors" aria-label="Sao chép tin nhắn của bạn"><i class="pi pi-copy text-sm"></i></button>
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
              <button v-if="msg.webUrls && msg.webUrls.length > 0" type="button" class="mb-3 min-h-11 flex items-center gap-2 cursor-pointer group/chip w-max" @click="openDrawer(msg.webUrls)">
                <div class="bg-blue-50 hover:bg-blue-100 text-gray-700 border border-gray-200 px-3 py-1.5 rounded-full flex items-center gap-2 transition-colors shadow-sm inline-flex">
                  <i class="pi pi-search text-xs"></i>
                  <span class="text-sm font-medium">Read {{ msg.webUrls.length }} web pages</span>
                  <div class="flex -space-x-1.5 ml-1">
                    <span v-for="(_, i) in msg.webUrls.slice(0, 3)" :key="i" class="w-5 h-5 rounded-full border border-gray-200 bg-white flex items-center justify-center">
                      <i class="pi pi-globe text-[10px] text-sky-600"></i>
                    </span>
                  </div>
                </div>
              </button>

              <!-- Render Message with Charts -->
              <GenerativeUiRenderer :content="msg.content" />
              
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
                <div v-if="msg.hitlError" class="text-sm text-red-700 mb-3" role="alert">{{ msg.hitlError }}</div>
                <div class="flex gap-2" v-if="!msg.hitlResolved">
                  <button @click="approveTask(msg)" :disabled="msg.hitlBusy" class="flex-1 min-h-11 px-4 py-2 bg-orange-600 hover:bg-orange-700 disabled:opacity-50 text-white text-sm font-medium rounded-lg transition-colors">
                    Phê duyệt
                  </button>
                  <button @click="rejectTask(msg)" :disabled="msg.hitlBusy" class="flex-1 min-h-11 px-4 py-2 bg-white hover:bg-gray-50 disabled:opacity-50 text-gray-700 border border-gray-300 text-sm font-medium rounded-lg transition-colors">
                    Từ chối
                  </button>
                </div>
                <div v-else class="text-sm font-medium" :class="msg.hitlResolved === 'Approved' ? 'text-green-600' : 'text-red-600'">
                  Đã {{ msg.hitlResolved === 'Approved' ? 'Phê duyệt' : 'Từ chối' }} thao tác này.
                </div>
              </div>

              <!-- Assistant Action -->
              <div v-if="!msg.isTyping" class="flex items-center gap-2 mt-3 text-gray-500">
                <button @click="copyMessage(msg.content)" class="w-11 h-11 hover:text-gray-900 hover:bg-gray-200/50 rounded-md transition-colors" aria-label="Sao chép câu trả lời"><i class="pi pi-copy text-sm"></i></button>
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
        <div v-if="chatError" role="alert" class="mb-2 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {{ chatError }}
        </div>
        <div class="relative flex flex-col bg-white border border-gray-200 rounded-[28px] p-2 shadow-sm">
          <!-- Attached Files Preview -->
          <div v-if="attachedFiles.length > 0" class="flex flex-wrap gap-2 px-3 pt-2">
            <div v-for="(file, index) in attachedFiles" :key="index"
              class="flex items-center gap-2 px-3 py-1.5 rounded-xl bg-blue-50 border border-blue-100 text-sm group/file">
              <i :class="getFileIconForInput(file.name)" class="text-xs"></i>
              <span class="max-w-[120px] truncate text-blue-700">{{ file.name }}</span>
              <span class="text-[10px] text-blue-400">({{ formatFileSize(file.size) }})</span>
              <button @click="removeFile(index)" class="ml-1 w-11 h-11 text-gray-500 hover:text-red-600 transition-colors" :aria-label="`Bỏ file ${file.name}`">
                <i class="pi pi-times text-xs"></i>
              </button>
            </div>
            <label class="flex items-center gap-2 px-2 text-xs text-gray-600 cursor-pointer">
              <input v-model="saveAttachmentsToKnowledge" type="checkbox" class="accent-blue-600" />
              Lưu nội dung file vào kho tri thức
            </label>
          </div>
          
          <!-- Text Area -->
          <div class="px-3 pt-2">
            <Textarea 
              v-model="inputMessage" 
              @keydown.enter.prevent="sendMessage"
              class="w-full max-h-48 !bg-transparent !border-0 resize-none !shadow-none text-gray-800 text-base"
              rows="1"
              autoResize
              aria-label="Tin nhắn"
              placeholder="Nhắn tin cho Trợ lý AI..." />
          </div>
          
          <!-- Bottom Toolbar -->
          <div class="flex items-center justify-between mt-2 px-1 pb-1">
            <!-- Right Actions -->
            <div class="flex items-center gap-1.5">
              <button @click="triggerFileInput" class="w-11 h-11 flex items-center justify-center text-gray-600 hover:text-gray-900 transition-colors rounded-full hover:bg-gray-100" aria-label="Đính kèm file">
                <i class="pi pi-paperclip text-lg"></i>
              </button>
              <Button 
                icon="pi pi-arrow-up" 
                aria-label="Gửi tin nhắn"
                @click="sendMessage"
                :disabled="(!inputMessage.trim() && attachedFiles.length === 0) || isGenerating"
                severity="info"
                rounded
                class="!w-11 !h-11"
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
        <a v-for="(url, index) in drawerUrls" :key="index" :href="url" target="_blank" rel="noopener noreferrer" class="block p-3 rounded-xl border border-gray-100 bg-gray-200/50 hover:bg-gray-200 hover:border-gray-300 transition-all group">
          <div class="flex items-start gap-3">
            <div class="w-8 h-8 rounded-lg bg-white shadow-sm flex-shrink-0 flex items-center justify-center overflow-hidden">
                <i class="pi pi-globe text-sky-600"></i>
            </div>
            <div class="flex-1 min-w-0">
              <div class="text-sm font-medium text-gray-800 truncate group-hover:text-cyan-400 transition-colors">{{ getCleanHostname(url) }}</div>
              <div class="text-xs text-gray-500 truncate mt-1">{{ url }}</div>
            </div>
          </div>
        </a>
      </div>
    </Drawer>
    
    <!-- Voice to Voice Module -->
    <VoiceWebRtcModule />
  </div>
</template>

<script setup lang="ts">
import { defineAsyncComponent, ref, nextTick, watch, onMounted } from 'vue';
import { useRoute } from 'vue-router';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import GenerativeUiRenderer from '@/components/chat/GenerativeUiRenderer.vue';
import { ChatSseParser, type ChatStreamEvent } from '@/utils/chatSse';
const VoiceWebRtcModule = defineAsyncComponent(
  () => import('@/components/voice/VoiceWebRtcModule.vue')
);

interface Message {
  role: 'user' | 'assistant' | 'system';
  content: string;
  isTyping?: boolean;
  webUrls?: string[];
  thinkingSteps?: string[];
  attachedFiles?: string[];
  hitlTaskId?: string;
  hitlResolved?: string;
  hitlBusy?: boolean;
  hitlError?: string;
}

const inputMessage = ref('');
const messages = ref<Message[]>([]);
const chatError = ref('');
const isGenerating = ref(false);
const chatContainer = ref<HTMLElement | null>(null);
const currentSessionId = ref<string | null>(null);
const attachedFiles = ref<File[]>([]);
const saveAttachmentsToKnowledge = ref(false);
const fileInputRef = ref<HTMLInputElement | null>(null);

const loadMessages = async () => {
  if (!currentSessionId.value) return;
  chatError.value = '';
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
    } else chatError.value = await readApiError(response, 'Không thể tải nội dung đoạn chat');
  } catch (error) {
    chatError.value = errorMessage(error, 'Không thể tải nội dung đoạn chat.');
  }
};
const route = useRoute();

const isDrawerOpen = ref(false);
const drawerUrls = ref<string[]>([]);

const openDrawer = (urls: string[]) => {
  drawerUrls.value = urls.filter(isSafeWebUrl);
  isDrawerOpen.value = true;
};

const isSafeWebUrl = (urlStr: string) => {
  try {
    const url = new URL(urlStr);
    return url.protocol === 'https:' || url.protocol === 'http:';
  } catch {
    return false;
  }
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

const copyMessage = async (content: string) => {
  chatError.value = '';
  try {
    await navigator.clipboard.writeText(content);
  } catch (error) {
    chatError.value = errorMessage(error, 'Không thể sao chép nội dung.');
  }
};

const sendMessage = async () => {
  const content = inputMessage.value.trim();
  const hasFiles = attachedFiles.value.length > 0;
  if ((!content && !hasFiles) || isGenerating.value) return;
  chatError.value = '';

  const fileNames = attachedFiles.value.map(f => f.name);
  messages.value.push({ role: 'user', content: content || `📎 ${fileNames.join(', ')}`, attachedFiles: fileNames.length > 0 ? fileNames : undefined });
  inputMessage.value = '';
  await scrollToBottom();

  isGenerating.value = true;
  messages.value.push({ role: 'assistant', content: '', isTyping: true });
  const assistantMsg = messages.value[messages.value.length - 1];
  await scrollToBottom();

  try {
    if (!currentSessionId.value) {
      const sessionRes = await http.post(ApiFactory.CHAT.CREATE_SESSION);
      if (sessionRes.ok) {
          const newSession = await sessionRes.json();
          currentSessionId.value = newSession.id;
          window.dispatchEvent(new CustomEvent('chat-session-created'));
      } else {
          throw new Error('Không thể tạo phiên trò chuyện.');
      }
    }
    let response: Response;

    if (hasFiles) {
      // Multipart: send with files
      const formData = new FormData();
      formData.append('sessionId', currentSessionId.value || '00000000-0000-0000-0000-000000000000');
      formData.append('message', content || 'Hãy phân tích nội dung file đính kèm.');
      formData.append('saveToKnowledge', String(saveAttachmentsToKnowledge.value));
      for (const file of attachedFiles.value) {
        formData.append('files', file);
      }
      attachedFiles.value = []; // Clear after sending
      saveAttachmentsToKnowledge.value = false;
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

    if (!response.ok) throw new Error(await readApiError(response, 'Yêu cầu chat thất bại'));
    if (!response.body) throw new Error('Trình duyệt không hỗ trợ streaming response.');

    const reader = response.body.getReader();
    const decoder = new TextDecoder('utf-8');

    assistantMsg.isTyping = false;

    const parser = new ChatSseParser();
    let streamFinished = false;
    while (!streamFinished) {
      const { done, value } = await reader.read();
      const events = done
        ? [...parser.push(decoder.decode()), ...parser.finish()]
        : parser.push(decoder.decode(value, { stream: true }));
      for (const event of events) {
          if (event.type === 'done') {
              streamFinished = true;
              break;
          }
          if (event.type === 'error') {
              throw new Error(event.value);
          }
          
          if (event.type === 'web-search') {
              const urls = event.value.split('|').filter(isSafeWebUrl);
              assistantMsg.webUrls = urls;
              continue;
          }
          if (event.type === 'thinking') {
              if (!assistantMsg.thinkingSteps) {
                  assistantMsg.thinkingSteps = [];
              }
              assistantMsg.thinkingSteps.push(event.value);
              await scrollToBottom();
              continue;
          }
          if (event.type === 'approval') {
              assistantMsg.hitlTaskId = event.value;
              await scrollToBottom();
              streamFinished = true;
              break;
          }
          if (event.type === 'agent-log') continue;

          assistantMsg.content += (event as Extract<ChatStreamEvent, { type: 'content' }>).value;
          await scrollToBottom();
      }
      if (done) break;
    }
    if (streamFinished) await reader.cancel();
  } catch (error) {
    assistantMsg.content = `Lỗi: ${errorMessage(error, 'Không thể tạo câu trả lời.')}`;
    assistantMsg.isTyping = false;
  } finally {
    isGenerating.value = false;
    window.dispatchEvent(new CustomEvent('chat-session-created'));
  }
};

const approveTask = async (msg: any) => {
  msg.hitlBusy = true;
  msg.hitlError = undefined;
  try {
    const res = await http.post(`/api/TaskApproval/${msg.hitlTaskId}/approve`);
    if (!res.ok) throw new Error(await readApiError(res, 'Phê duyệt thất bại'));
    const response = await res.json();
    const result = typeof response.result === 'string' ? response.result : '';
    msg.hitlResolved = 'Approved';
    messages.value.push({
      role: 'system',
      content: `Đã phê duyệt. Kết quả thực thi tool: ${result}`
    });
    inputMessage.value = `Tôi đã phê duyệt hành động trên. Kết quả thực thi là: ${result}. Vui lòng tiếp tục.`;
    await sendMessage();
  } catch (error) {
    msg.hitlError = errorMessage(error, 'Không thể phê duyệt thao tác.');
  } finally {
    msg.hitlBusy = false;
  }
};

const rejectTask = async (msg: any) => {
  msg.hitlBusy = true;
  msg.hitlError = undefined;
  try {
    const res = await http.post(`/api/TaskApproval/${msg.hitlTaskId}/reject`, { Comment: "User rejected" });
    if (!res.ok) throw new Error(await readApiError(res, 'Từ chối thất bại'));
    msg.hitlResolved = 'Rejected';
    messages.value.push({
      role: 'system',
      content: `Đã từ chối hành động.`
    });
  } catch (error) {
    msg.hitlError = errorMessage(error, 'Không thể từ chối thao tác.');
  } finally {
    msg.hitlBusy = false;
  }
};
</script>
