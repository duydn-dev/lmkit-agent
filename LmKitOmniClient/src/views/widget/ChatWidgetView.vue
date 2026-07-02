<template>
  <div class="flex flex-col w-full h-full bg-white font-sans text-sm m-0 p-0 overflow-hidden">
    <!-- Header -->
    <div class="flex items-center justify-between bg-blue-600 text-white px-4 py-3 shadow-md z-10 shrink-0">
      <div class="flex items-center gap-3">
        <div class="w-8 h-8 rounded-full bg-white/20 flex items-center justify-center">
          <i class="pi pi-sparkles text-white"></i>
        </div>
        <div>
          <div class="font-semibold text-base leading-tight">Trợ lý AI</div>
          <div class="text-[11px] text-blue-100 flex items-center gap-1">
            <span class="w-1.5 h-1.5 rounded-full bg-green-400 inline-block"></span>
            Luôn sẵn sàng
          </div>
        </div>
      </div>
      <button @click="closeWidget" class="text-white/80 hover:text-white p-1 hover:bg-white/10 rounded-full transition-colors" title="Thu nhỏ">
        <i class="pi pi-minus"></i>
      </button>
    </div>

    <!-- Chat History -->
    <div ref="chatContainer" class="flex-1 overflow-y-auto p-4 scroll-smooth bg-gray-50/50">
      <div v-if="messages.length === 0" class="h-full flex flex-col items-center justify-center text-center opacity-70">
        <i class="pi pi-comments text-4xl text-gray-300 mb-2"></i>
        <p class="text-xs text-gray-500">Bắt đầu trò chuyện với AI...</p>
      </div>

      <div v-else class="flex flex-col space-y-4">
        <div v-for="(msg, index) in messages" :key="index" class="flex flex-col">
          
          <!-- User Message -->
          <div v-if="msg.role === 'user'" class="flex justify-end w-full">
            <div class="bg-blue-600 text-white px-3.5 py-2 rounded-2xl rounded-tr-sm shadow-sm max-w-[85%] text-[13px] break-words whitespace-pre-wrap">
              {{ getCleanUserContent(msg.content) }}
            </div>
          </div>

          <!-- Assistant Message -->
          <div v-else class="flex w-full gap-2 items-end">
            <!-- Avatar -->
            <div class="flex-shrink-0 w-6 h-6 rounded-full bg-blue-100 border border-blue-200 flex items-center justify-center mb-1">
              <i class="pi pi-sparkles text-[10px] text-blue-600"></i>
            </div>
            
            <div class="flex flex-col flex-1 min-w-0 bg-white border border-gray-100 px-3.5 py-2.5 rounded-2xl rounded-tl-sm shadow-sm">
              <!-- Thinking -->
              <div v-if="msg.thinkingSteps && msg.thinkingSteps.length > 0" class="mb-2 flex flex-col gap-1 p-2 rounded-lg bg-gray-50 border border-gray-100 text-[11px]">
                <div v-for="(step, idx) in msg.thinkingSteps.slice(-1)" :key="idx" class="flex items-center gap-1.5 text-gray-500">
                  <i class="pi pi-spin pi-spinner text-blue-400" v-if="idx === 0 && msg.isTyping && !msg.content"></i>
                  <i class="pi pi-check text-green-500" v-else></i>
                  <span class="truncate">{{ step }}</span>
                </div>
              </div>

              <!-- Content -->
              <div class="text-[13px] leading-relaxed whitespace-pre-wrap break-words text-gray-800 markdown-body" v-html="formatMessage(msg.content)"></div>
              
              <!-- Typing Indicator -->
              <div v-if="msg.isTyping" class="flex gap-1 mt-1">
                <div class="w-1.5 h-1.5 rounded-full bg-gray-400 animate-bounce"></div>
                <div class="w-1.5 h-1.5 rounded-full bg-gray-400 animate-bounce" style="animation-delay: 0.1s"></div>
                <div class="w-1.5 h-1.5 rounded-full bg-gray-400 animate-bounce" style="animation-delay: 0.2s"></div>
              </div>

              <!-- HITL Approval Card -->
              <div v-if="msg.hitlTaskId" class="mt-3 p-3 bg-orange-50 border border-orange-200 rounded-lg shadow-sm w-full">
                <div class="flex items-center gap-1.5 text-orange-800 font-semibold mb-1 text-sm">
                  <i class="pi pi-exclamation-triangle text-xs"></i> Yêu cầu xác nhận
                </div>
                <div class="text-xs text-orange-700 mb-3 leading-snug">
                  Agent cần sự cho phép của bạn để tiếp tục.
                </div>
                <div class="flex gap-2" v-if="!msg.hitlResolved">
                  <button @click="approveTask(msg)" class="flex-1 px-2 py-1.5 bg-orange-600 hover:bg-orange-700 text-white text-xs font-medium rounded transition-colors">
                    Phê duyệt
                  </button>
                  <button @click="rejectTask(msg)" class="flex-1 px-2 py-1.5 bg-white hover:bg-gray-50 text-gray-700 border border-gray-300 text-xs font-medium rounded transition-colors">
                    Từ chối
                  </button>
                </div>
                <div v-else class="text-xs font-medium" :class="msg.hitlResolved === 'Approved' ? 'text-green-600' : 'text-red-600'">
                  Đã {{ msg.hitlResolved === 'Approved' ? 'Phê duyệt' : 'Từ chối' }}.
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Input Area -->
    <div class="bg-white border-t border-gray-100 p-3 shrink-0">
      <div class="relative flex items-center bg-gray-50 border border-gray-200 rounded-full pr-1 shadow-inner focus-within:border-blue-300 focus-within:ring-1 focus-within:ring-blue-300 transition-all">
        <textarea 
          v-model="inputMessage" 
          @keydown.enter.prevent="sendMessage"
          class="flex-1 bg-transparent border-0 focus:ring-0 resize-none outline-none py-2.5 pl-4 pr-2 text-[13px] text-gray-700 max-h-24 leading-snug" 
          rows="1"
          placeholder="Nhập tin nhắn..."
          style="scrollbar-width: none;"
        ></textarea>
        
        <button 
          @click="sendMessage"
          :disabled="!inputMessage.trim() || isGenerating"
          class="w-8 h-8 flex items-center justify-center rounded-full bg-blue-600 text-white disabled:opacity-50 disabled:bg-gray-300 hover:bg-blue-700 transition-colors shrink-0"
        >
          <i class="pi pi-send text-xs"></i>
        </button>
      </div>
      <div class="text-center text-[10px] text-gray-400 mt-2">
        Powered by LM-Kit.NET
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, nextTick, onMounted } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';

interface Message {
  role: 'user' | 'assistant';
  content: string;
  isTyping?: boolean;
  thinkingSteps?: string[];
}

const inputMessage = ref('');
const messages = ref<Message[]>([
    {
        role: 'assistant',
        content: 'Xin chào! Tôi có thể giúp gì cho bạn?'
    }
]);
const isGenerating = ref(false);
const chatContainer = ref<HTMLElement | null>(null);
const currentSessionId = ref<string | null>(null);

const closeWidget = () => {
    // Gửi thông báo ra trang web chứa iframe để đóng iframe lại
    if (window.parent) {
        window.parent.postMessage({ type: 'lmkit-close-widget' }, '*');
    }
};

const scrollToBottom = async () => {
  await nextTick();
  if (chatContainer.value) {
    chatContainer.value.scrollTop = chatContainer.value.scrollHeight;
  }
};

const formatMessage = (text: string) => {
  if (!text) return '';
  return text.replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>').replace(/\n/g, '<br/>');
};

const getCleanUserContent = (content: string) => {
  return content.replace(/\n\n--- Nội dung file đính kèm ---[\s\S]*/g, '').trim();
};

const sendMessage = async () => {
  const content = inputMessage.value.trim();
  if (!content || isGenerating.value) return;

  messages.value.push({ role: 'user', content: content });
  inputMessage.value = '';
  await scrollToBottom();

  isGenerating.value = true;
  messages.value.push({ role: 'assistant', content: '', isTyping: true });
  const assistantMsg = messages.value[messages.value.length - 1];
  await scrollToBottom();

  if (!currentSessionId.value) {
      try {
          const sessionRes = await http.post(ApiFactory.CHAT.CREATE_SESSION);
          if (sessionRes.ok) {
              const newSession = await sessionRes.json();
              currentSessionId.value = newSession.id;
          }
      } catch (e) {
          console.error('Lỗi tạo session ẩn danh:', e);
      }
  }

  try {
    const payload = {
      SessionId: currentSessionId.value || '00000000-0000-0000-0000-000000000000',
      Message: content,
      ModelId: null
    };
    
    const response = await http.post(ApiFactory.CHAT.STREAM, payload);

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
              continue; // Bỏ qua web search display cho widget nhỏ để đỡ rối
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
    assistantMsg.content = `Lỗi phản hồi: ${error instanceof Error ? error.message : 'Unknown error'}`;
    assistantMsg.isTyping = false;
  } finally {
    isGenerating.value = false;
  }
};

onMounted(() => {
    // Tự động điều chỉnh height textarea
    const tx = document.getElementsByTagName("textarea");
    for (let i = 0; i < tx.length; i++) {
        tx[i].setAttribute("style", "height:" + (tx[i].scrollHeight) + "px;overflow-y:hidden;scrollbar-width:none;");
        tx[i].addEventListener("input", OnInput, false);
    }

    function OnInput(this: any) {
        this.style.height = 0;
        this.style.height = (this.scrollHeight) + "px";
    }
});

const approveTask = async (msg: any) => {
  try {
    msg.hitlResolved = 'Approved';
    const res = await http.post(`/api/TaskApproval/${msg.hitlTaskId}/approve`);
    if (res.ok) {
      const result = await res.json();
      messages.value.push({
        role: 'system',
        content: `Đã phê duyệt. Kết quả: ${result.Result}`
      });
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

<style scoped>
/* Scoped styles để đảm bảo CSS không bị leak ra ngoài nếu component được dùng ở chỗ khác, 
   mặc dù dùng iframe đã cách ly hoàn toàn rồi. */
.markdown-body {
    font-size: 13px;
}
.markdown-body p {
    margin-bottom: 0.5em;
}
.markdown-body p:last-child {
    margin-bottom: 0;
}
</style>
