<template>
  <div class="flex-1 flex flex-col relative w-full h-full">
    <!-- Session Header: agent badge + canvas / share actions for the active session -->
    <div v-if="currentSessionId" class="flex flex-wrap items-center justify-between gap-2 px-4 py-2 border-b border-gray-200 bg-chatgpt-dark">
      <div class="flex items-center min-w-0">
        <span
          v-if="activeAgentName"
          role="note"
          class="flex items-center gap-1.5 px-2.5 py-1 rounded-full bg-violet-50 border border-violet-200 text-sm text-violet-700 max-w-[240px]"
          :title="`Đang chat với agent ${activeAgentName}`"
          :aria-label="`Đang chat với agent ${activeAgentName}`">
          <span aria-hidden="true">{{ activeAgentIcon || '🤖' }}</span>
          <span class="truncate font-medium">{{ activeAgentName }}</span>
        </span>
      </div>
      <div class="flex flex-wrap items-center gap-2">
        <button
          @click="toggleCanvasPanel"
          aria-label="Mở Canvas"
          :aria-expanded="canvasPanelOpen"
          class="min-h-11 px-3 flex items-center gap-2 rounded-lg border border-gray-200 bg-white text-sm font-medium text-gray-700 hover:bg-gray-50 hover:text-gray-900 transition-colors">
          <i class="pi pi-palette text-sm" aria-hidden="true"></i>
          <span>Canvas</span>
          <span v-if="canvasCount > 0" class="min-w-5 h-5 px-1 rounded-full bg-sky-100 text-sky-700 text-xs font-semibold flex items-center justify-center">{{ canvasCount }}</span>
        </button>
        <button
          @click="shareSession"
          :disabled="shareBusy"
          class="min-h-11 px-3 flex items-center gap-2 rounded-lg border border-gray-200 bg-white text-sm font-medium text-gray-700 hover:bg-gray-50 hover:text-gray-900 disabled:opacity-50 transition-colors"
          aria-label="Chia sẻ đoạn chat">
          <i class="pi pi-share-alt text-sm" aria-hidden="true"></i>
          <span>Chia sẻ</span>
        </button>
        <button
          @click="revokeShare"
          :disabled="shareBusy"
          class="min-h-11 px-3 flex items-center gap-2 rounded-lg text-sm font-medium text-gray-500 hover:text-red-600 hover:bg-red-50 disabled:opacity-50 transition-colors"
          aria-label="Thu hồi liên kết chia sẻ">
          <i class="pi pi-link text-sm" aria-hidden="true"></i>
          <span>Thu hồi liên kết</span>
        </button>
      </div>
    </div>

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
                <button v-if="index === lastUserIndex && !isGenerating" @click="startEditing" class="w-11 h-11 hover:text-gray-900 transition-colors" aria-label="Sửa tin nhắn"><i class="pi pi-pencil text-sm"></i></button>
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

              <!-- Model reasoning (DeepSeek-R1 style): the model's own chain-of-thought,
                   collapsible and kept distinct from the pipeline-status steps above. -->
              <details v-if="msg.reasoning" class="mb-4 w-fit max-w-[90%] rounded-xl border border-indigo-200 bg-indigo-50/60 p-3">
                <summary class="cursor-pointer select-none text-[11px] font-semibold uppercase tracking-wider text-indigo-700">
                  Suy luận của mô hình
                </summary>
                <div class="mt-2 whitespace-pre-wrap text-[13px] leading-snug text-indigo-900">{{ msg.reasoning }}</div>
              </details>

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

              <!-- Produced Files (charts / CSVs the code interpreter returned) -->
              <div v-if="msg.producedFiles && msg.producedFiles.length > 0" class="mt-2">
                <div class="text-xs text-gray-500 mb-1.5">Tệp kết quả</div>
                <div class="flex flex-wrap gap-2">
                  <template v-for="file in msg.producedFiles" :key="file.id">
                    <!-- Image result: inline preview, click to open / save -->
                    <a
                      v-if="file.contentType.startsWith('image/')"
                      :href="fileUrl(file.id)"
                      :download="file.name"
                      target="_blank"
                      rel="noopener"
                      :aria-label="`Tải ảnh ${file.name}`"
                      class="block rounded-lg overflow-hidden">
                      <img
                        :src="fileUrl(file.id)"
                        :alt="file.name"
                        class="max-w-xs max-h-64 rounded-lg border border-gray-200 object-contain" />
                    </a>
                    <!-- Non-image result: download chip -->
                    <a
                      v-else
                      :href="fileUrl(file.id)"
                      :download="file.name"
                      :aria-label="`Tải tệp ${file.name}`"
                      class="min-h-11 flex items-center gap-2 px-3 py-1.5 rounded-xl border border-gray-200 bg-white text-sm text-gray-700 hover:bg-gray-50 hover:border-gray-300 transition-colors">
                      <i class="pi pi-file text-base text-gray-500" aria-hidden="true"></i>
                      <span class="max-w-[160px] truncate font-medium">{{ file.name }}</span>
                      <span class="text-xs text-gray-400">{{ formatFileSize(file.size) }}</span>
                    </a>
                  </template>
                </div>
              </div>

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
                <div class="text-sm text-orange-700 mb-3">
                  Agent đang cố gắng thực thi một công cụ nhạy cảm. Hệ thống đã tạm dừng để chờ bạn phê duyệt.
                </div>
                <div v-if="msg.hitlActionName || msg.hitlDetails" class="mb-4">
                  <div v-if="msg.hitlActionName" class="text-xs font-semibold text-orange-800 mb-1">Hành động: {{ msg.hitlActionName }}</div>
                  <pre v-if="msg.hitlDetails" class="text-xs text-gray-800 bg-white border border-orange-200 rounded-lg p-3 max-h-48 overflow-auto whitespace-pre-wrap break-words">{{ msg.hitlDetails }}</pre>
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
                <button v-if="msg.role === 'assistant' && index === messages.length - 1 && !isGenerating" @click="regenerate" class="w-11 h-11 hover:text-gray-900 hover:bg-gray-200/50 rounded-md transition-colors" aria-label="Tạo lại câu trả lời"><i class="pi pi-refresh text-sm"></i></button>
                <button v-if="hasCanvasBlock(msg)" @click="openMessageInCanvas(msg.content)" class="min-h-11 px-2 flex items-center gap-1.5 hover:text-gray-900 hover:bg-gray-200/50 rounded-md transition-colors text-sm" aria-label="Mở trong Canvas">
                  <i class="pi pi-palette text-sm" aria-hidden="true"></i>
                  <span>Mở trong Canvas</span>
                </button>
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
        <!-- Edit-last-message mode banner -->
        <div v-if="isEditing" class="mb-2 flex items-center justify-between gap-3 rounded-lg border border-sky-200 bg-sky-50 px-4 py-2 text-sm text-sky-800">
          <span class="flex items-center gap-2 min-w-0">
            <i class="pi pi-pencil text-xs" aria-hidden="true"></i>
            <span class="truncate">Đang sửa tin nhắn cuối — gửi để thay thế cặp hỏi đáp trước.</span>
          </span>
          <button @click="cancelEditing" class="min-h-11 px-2 flex items-center gap-1 font-medium text-sky-800 hover:text-sky-950 transition-colors flex-shrink-0" aria-label="Hủy sửa tin nhắn">
            <i class="pi pi-times text-xs" aria-hidden="true"></i>
            <span>Hủy</span>
          </button>
        </div>
        <!-- Temporary chat indicator: subtle reminder that nothing is saved -->
        <div v-if="isEphemeral" class="mb-2 flex items-center gap-2 rounded-lg border border-amber-200 bg-amber-50 px-4 py-2 text-sm text-amber-800">
          <i class="pi pi-eye-slash text-xs" aria-hidden="true"></i>
          <span>Chat tạm thời — đoạn chat này sẽ không được lưu vào lịch sử.</span>
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
              ref="composerRef"
              v-model="inputMessage"
              @keydown.enter.exact.prevent="sendMessage"
              @keydown.esc="cancelEditing"
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
              <button
                @click="toggleWebSearch"
                :aria-pressed="webSearchEnabled"
                aria-label="Tìm kiếm web"
                class="min-w-11 min-h-11 px-3 flex items-center justify-center rounded-full border transition-colors"
                :class="webSearchEnabled ? 'border-sky-200 bg-sky-50 text-sky-700 hover:bg-sky-100' : 'border-transparent text-gray-500 hover:text-gray-900 hover:bg-gray-100'">
                <i class="pi pi-globe text-lg" aria-hidden="true"></i>
              </button>
              <button
                @click="toggleEphemeral"
                :aria-pressed="isEphemeral"
                aria-label="Chat tạm thời"
                title="Chat tạm thời — không lưu vào lịch sử"
                class="min-w-11 min-h-11 px-3 flex items-center justify-center rounded-full border transition-colors"
                :class="isEphemeral ? 'border-amber-200 bg-amber-50 text-amber-700 hover:bg-amber-100' : 'border-transparent text-gray-500 hover:text-gray-900 hover:bg-gray-100'">
                <i class="pi pi-eye-slash text-lg" aria-hidden="true"></i>
              </button>
              <button @click="triggerFileInput" class="w-11 h-11 flex items-center justify-center text-gray-600 hover:text-gray-900 transition-colors rounded-full hover:bg-gray-100" aria-label="Đính kèm file">
                <i class="pi pi-paperclip text-lg"></i>
              </button>
              <button
                v-if="voiceSupported"
                @click="toggleVoiceInput"
                :aria-pressed="isRecording"
                aria-label="Nhập bằng giọng nói"
                :disabled="isTranscribing"
                class="min-w-11 min-h-11 px-2 flex items-center justify-center gap-1.5 rounded-full border transition-colors disabled:opacity-50"
                :class="isRecording ? 'border-red-200 bg-red-50 text-red-600 hover:bg-red-100' : 'border-transparent text-gray-500 hover:text-gray-900 hover:bg-gray-100'">
                <i :class="[isTranscribing ? 'pi pi-spin pi-spinner' : 'pi pi-microphone', isRecording ? 'animate-pulse' : '']" class="text-lg" aria-hidden="true"></i>
                <span v-if="isRecording" class="text-xs font-medium tabular-nums">{{ voiceElapsedLabel }}</span>
              </button>
              <Button
                v-if="isStreaming"
                icon="pi pi-stop"
                aria-label="Dừng tạo trả lời"
                @click="stop"
                severity="danger"
                rounded
                class="!w-11 !h-11"
              />
              <Button
                v-else
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
    
    <!-- Canvas slide-over panel (session artifacts editor) -->
    <CanvasPanel
      ref="canvasPanelRef"
      :visible="canvasPanelOpen"
      :session-id="currentSessionId"
      @close="canvasPanelOpen = false"
      @insert="insertIntoComposer"
      @count-changed="canvasCount = $event" />

    <!-- Toast notifications (share confirmations, regenerate warnings) -->
    <Toast position="bottom-right" />

    <!-- Voice to Voice Module -->
    <VoiceWebRtcModule />
  </div>
</template>

<script setup lang="ts">
import { computed, defineAsyncComponent, ref, nextTick, watch, onMounted, type ComponentPublicInstance } from 'vue';
import { useRoute } from 'vue-router';
import { useToast } from 'primevue/usetoast';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import GenerativeUiRenderer from '@/components/chat/GenerativeUiRenderer.vue';
import CanvasPanel from '@/components/canvas/CanvasPanel.vue';
import { largestCodeFence } from '@/components/canvas/codeFence';
import { useVoiceInput } from '@/composables/useVoiceInput';
import {
  useChatStream,
  useHitlActions,
  isSafeWebUrl,
  getCleanUserContent,
  parseStoredAssistantContent,
  type ChatMessage,
} from '@/composables/useChatStream';
const VoiceWebRtcModule = defineAsyncComponent(
  () => import('@/components/voice/VoiceWebRtcModule.vue')
);

const inputMessage = ref('');
const messages = ref<ChatMessage[]>([]);
const { consumeStream, stop, isStreaming } = useChatStream();
const toast = useToast();
const chatError = ref('');
const isGenerating = ref(false);
const chatContainer = ref<HTMLElement | null>(null);
const currentSessionId = ref<string | null>(null);
const attachedFiles = ref<File[]>([]);
const saveAttachmentsToKnowledge = ref(false);
const fileInputRef = ref<HTMLInputElement | null>(null);

// --- Composer helpers (canvas insert + voice input target) --------------------

const composerRef = ref<ComponentPublicInstance | null>(null);

const focusComposer = () => {
  const el = composerRef.value?.$el as unknown;
  if (el instanceof HTMLTextAreaElement) el.focus();
};

/** "Chèn vào chat" from the Canvas panel: appends a fenced block to the composer. */
const insertIntoComposer = (text: string) => {
  inputMessage.value = inputMessage.value ? `${inputMessage.value}\n${text}` : text;
  focusComposer();
};

// --- Canvas panel -------------------------------------------------------------

const canvasPanelOpen = ref(false);
const canvasCount = ref(0);
const canvasPanelRef = ref<InstanceType<typeof CanvasPanel> | null>(null);

const toggleCanvasPanel = () => {
  canvasPanelOpen.value = !canvasPanelOpen.value;
};

// Canvas-fence detection is memoised per message so the regex runs once per
// distinct message content instead of for every message on every render — a long
// stream would otherwise re-scan the whole transcript on each token. Finished
// messages have stable content and hit the cache; only the message currently
// streaming (its content still growing) is re-scanned, and only for itself.
const canvasFenceCache = new WeakMap<ChatMessage, { content: string; present: boolean }>();
const hasCanvasBlock = (msg: ChatMessage): boolean => {
  const cached = canvasFenceCache.get(msg);
  if (cached && cached.content === msg.content) return cached.present;
  const present = largestCodeFence(msg.content) !== null;
  canvasFenceCache.set(msg, { content: msg.content, present });
  return present;
};

/** "Mở trong Canvas": creates a code artifact from the message's largest fence. */
const openMessageInCanvas = async (content: string) => {
  const block = largestCodeFence(content);
  if (!block) return;
  canvasPanelOpen.value = true;
  await nextTick();
  await canvasPanelRef.value?.createFromChat({
    title: 'Đoạn mã từ chat',
    kind: 'code',
    language: block.language,
    content: block.content,
  });
};

/** Silent header-badge refresh; failures just leave the badge hidden. */
const refreshCanvasCount = async () => {
  const id = currentSessionId.value;
  if (!id) return;
  try {
    const response = await http.get(ApiFactory.CANVAS.LIST(id));
    if (!response.ok || currentSessionId.value !== id) return;
    const data = await response.json() as unknown;
    if (currentSessionId.value !== id) return;
    canvasCount.value = Array.isArray(data) ? data.length : 0;
  } catch {
    // Display-only badge: never surface list errors outside the panel.
  }
};

// --- Agent badge (display only) -----------------------------------------------

const activeAgentName = ref<string | null>(null);
const activeAgentIcon = ref<string | null>(null);

/** Resolves agentName/agentIcon of the active session from the sessions list. */
const loadSessionMeta = async () => {
  const id = currentSessionId.value;
  activeAgentName.value = null;
  activeAgentIcon.value = null;
  if (!id) return;
  try {
    const response = await http.get(ApiFactory.CHAT.SESSIONS);
    if (!response.ok || currentSessionId.value !== id) return;
    const sessions = await response.json() as Array<{ id?: unknown; agentName?: unknown; agentIcon?: unknown }>;
    if (!Array.isArray(sessions) || currentSessionId.value !== id) return;
    const active = sessions.find((session) => session.id === id);
    if (!active) return;
    activeAgentName.value = typeof active.agentName === 'string' && active.agentName.trim() ? active.agentName : null;
    activeAgentIcon.value = typeof active.agentIcon === 'string' && active.agentIcon.trim() ? active.agentIcon : null;
  } catch {
    // Display-only badge: sessions without resolvable metadata look as today.
  }
};

watch(currentSessionId, (id) => {
  canvasCount.value = 0;
  if (id) {
    void refreshCanvasCount();
    void loadSessionMeta();
  } else {
    canvasPanelOpen.value = false;
    activeAgentName.value = null;
    activeAgentIcon.value = null;
  }
});

// --- Push-to-talk voice input -------------------------------------------------

const {
  isSupported: voiceSupported,
  isRecording,
  isTranscribing,
  elapsedLabel: voiceElapsedLabel,
  toggle: toggleVoiceInput,
} = useVoiceInput({
  onTranscript: (text) => {
    inputMessage.value = inputMessage.value ? `${inputMessage.value} ${text}` : text;
    focusComposer();
  },
  onError: (message) => {
    toast.add({ severity: 'error', summary: 'Nhập bằng giọng nói', detail: message, life: 6000 });
  },
});

const loadMessages = async () => {
  if (!currentSessionId.value) return;
  chatError.value = '';
  try {
    const response = await http.get(ApiFactory.CHAT.GET_MESSAGES(currentSessionId.value));
    if (response.ok) {
      const data = await response.json();
      messages.value = data.map((m: { content: string; role: string }) => {
        // Xóa các marker giao thức ([Agent invoked], [THINKING], [WEB_SEARCH])
        // bằng đúng tiện ích dùng chung với trang chia sẻ công khai.
        const parsed = parseStoredAssistantContent(m.content);
        return {
          role: m.role.toLowerCase(),
          content: parsed.content,
          webUrls: parsed.webUrls,
          thinkingSteps: parsed.thinkingSteps,
          reasoning: parsed.reasoning,
          producedFiles: parsed.producedFiles
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

const getCleanHostname = (urlStr: string) => {
    try { return new URL(urlStr).hostname.replace('www.', ''); } catch { return 'Website'; }
};

onMounted(() => {
  if (route.query.id) {
    // Opening a saved conversation is never temporary.
    isEphemeral.value = false;
    currentSessionId.value = route.query.id as string;
    loadMessages();
  }
});

watch(() => route.query.id, (newId) => {
  if (newId && typeof newId === 'string') {
    cancelEditing();
    // A saved session opened from history leaves temporary mode.
    isEphemeral.value = false;
    currentSessionId.value = newId;
    loadMessages();
  } else if (route.query.new) {
    cancelEditing();
    // "New chat" from the sidebar starts an ordinary (saved) conversation.
    isEphemeral.value = false;
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

/**
 * Same-origin, cookie-authenticated URL for a code-interpreter output file.
 * SECURITY: the id is always encoded as a single path segment so it can never
 * break out of `/api/files/` or inject query/path characters.
 */
const fileUrl = (id: string) => `/api/files/${encodeURIComponent(id)}`;

const copyMessage = async (content: string) => {
  chatError.value = '';
  try {
    await navigator.clipboard.writeText(content);
  } catch (error) {
    chatError.value = errorMessage(error, 'Không thể sao chép nội dung.');
  }
};

// --- Web-search toggle (persisted, default ON) -------------------------------

const WEB_SEARCH_STORAGE_KEY = 'omni.webSearch';

const loadWebSearchPreference = (): boolean => {
  try {
    return localStorage.getItem(WEB_SEARCH_STORAGE_KEY) !== 'off';
  } catch {
    return true;
  }
};

const webSearchEnabled = ref(loadWebSearchPreference());

const toggleWebSearch = () => {
  webSearchEnabled.value = !webSearchEnabled.value;
  try {
    localStorage.setItem(WEB_SEARCH_STORAGE_KEY, webSearchEnabled.value ? 'on' : 'off');
  } catch {
    // Storage may be unavailable (private mode); the toggle still applies to this session.
  }
};

// --- Temporary chat ("Chat tạm thời", ChatGPT/Gemini style) -------------------
// A deliberate per-conversation choice, never persisted across reloads. Toggling it
// starts a fresh chat so the mode only ever governs a brand-new conversation: the
// session it creates is flagged ephemeral server-side (no messages persisted, hidden
// from history). Opening a saved session clears the mode (see the route watcher).

const isEphemeral = ref(false);

const toggleEphemeral = () => {
  isEphemeral.value = !isEphemeral.value;
  // Start a clean conversation so the new mode applies to a fresh session and never
  // retroactively changes the currently open one.
  cancelEditing();
  currentSessionId.value = null;
  messages.value = [];
  chatError.value = '';
};

// --- Edit last user message ---------------------------------------------------

const isEditing = ref(false);

const lastUserIndex = computed(() => {
  for (let i = messages.value.length - 1; i >= 0; i--) {
    if (messages.value[i].role === 'user') return i;
  }
  return -1;
});

const startEditing = () => {
  const index = lastUserIndex.value;
  if (index === -1 || isGenerating.value) return;
  isEditing.value = true;
  inputMessage.value = getCleanUserContent(messages.value[index].content);
};

const cancelEditing = () => {
  if (!isEditing.value) return;
  isEditing.value = false;
  inputMessage.value = '';
};

// --- Session bootstrap (shared by the text and file send paths) ---------------

/**
 * Creates a chat session on demand for the first message of a brand-new chat and
 * announces it so the sidebar refreshes. Throws when the session cannot be
 * created; callers run this inside their guarded exchange so the failure surfaces
 * on the pending assistant bubble exactly like any other send error.
 */
const ensureSession = async () => {
  if (currentSessionId.value) return;
  // A temporary chat creates its session flagged ephemeral (messages never persisted,
  // hidden from history); a normal chat keeps the exact legacy body-less request.
  const sessionRes = await http.post(
    ApiFactory.CHAT.CREATE_SESSION,
    isEphemeral.value ? { ephemeral: true } : undefined
  );
  if (!sessionRes.ok) throw new Error('Không thể tạo phiên trò chuyện.');
  const newSession = await sessionRes.json();
  currentSessionId.value = newSession.id;
  window.dispatchEvent(new CustomEvent('chat-session-created'));
};

// --- Shared JSON streaming exchange (normal send + regenerate) ----------------

interface StreamExchangeHooks {
  /** readApiError fallback used when the POST itself is rejected. */
  requestErrorFallback: string;
  /** errorMessage fallback for a thrown transport / stream / body-factory error. */
  errorFallback: string;
  /** Lets a caller claim a mapped error (regenerate's "nothing to regenerate"); return true when handled. */
  onError?: (message: string) => boolean;
  /** Runs after a fully successful stream, before `finally` (regenerate's post-stream signal check). */
  onComplete?: () => void;
  /** Runs in `finally` once `isGenerating` is cleared (the normal send refreshes the sidebar). */
  onSettled?: () => void;
}

type StreamRequestBody = Record<string, unknown>;

/**
 * Engine shared by the two JSON streaming exchanges — a normal text send and a
 * regenerate. Both POST a body to the SSE endpoint, consume it into the pending
 * assistant bubble with the same web-search closure, map transport / stream errors
 * onto that bubble, and clear `isGenerating` in `finally`. The multipart/file send
 * targets a different endpoint and keeps its own copy of this flow rather than
 * routing through here.
 *
 * `body` is either a ready payload (regenerate, whose session already exists) or a
 * factory run just before the request (the normal send creates the session on
 * demand, then builds the payload with the resulting id); a factory that throws is
 * reported exactly like a failed request.
 */
async function streamExchange(
  body: StreamRequestBody | (() => StreamRequestBody | Promise<StreamRequestBody>),
  assistantMsg: ChatMessage,
  hooks: StreamExchangeHooks,
): Promise<void> {
  try {
    const payload = typeof body === 'function' ? await body() : body;
    const response = await http.post(ApiFactory.CHAT.STREAM, payload);
    if (!response.ok) throw new Error(await readApiError(response, hooks.requestErrorFallback));
    await consumeStream({
      response,
      assistantMsg,
      scrollToBottom,
      onWebSearch: (urls) => { assistantMsg.webUrls = urls; },
    });
    hooks.onComplete?.();
  } catch (error) {
    const message = errorMessage(error, hooks.errorFallback);
    if (hooks.onError?.(message)) return;
    assistantMsg.content = `Lỗi: ${message}`;
    assistantMsg.isTyping = false;
  } finally {
    isGenerating.value = false;
    hooks.onSettled?.();
  }
}

const sendMessage = async () => {
  const content = inputMessage.value.trim();
  const editing = isEditing.value;
  // Edit mode always replaces via the JSON stream endpoint; any attachments in
  // the tray are kept for the next normal send instead of being mixed in.
  const hasFiles = !editing && attachedFiles.value.length > 0;
  if ((!content && !hasFiles) || isGenerating.value) return;
  chatError.value = '';

  if (editing) {
    // Locally drop the last user message plus its assistant reply; the server
    // does the same because of `replaceLastExchange: true`.
    const index = lastUserIndex.value;
    if (index !== -1) messages.value.splice(index);
    isEditing.value = false;
  }

  const fileNames = hasFiles ? attachedFiles.value.map(f => f.name) : [];
  messages.value.push({ role: 'user', content: content || `📎 ${fileNames.join(', ')}`, attachedFiles: fileNames.length > 0 ? fileNames : undefined });
  inputMessage.value = '';
  await scrollToBottom();

  isGenerating.value = true;
  messages.value.push({ role: 'assistant', content: '', isTyping: true });
  const assistantMsg = messages.value[messages.value.length - 1];
  await scrollToBottom();

  // Both paths refresh the sidebar (new session / updated preview) once settled.
  const onSettled = () => window.dispatchEvent(new CustomEvent('chat-session-created'));

  if (hasFiles) {
    // Multipart/file send keeps its own path: it builds a FormData request for a
    // different endpoint, so it owns its stream + error handling instead of going
    // through streamExchange.
    try {
      await ensureSession();
      // Multipart: send with files
      const formData = new FormData();
      formData.append('sessionId', currentSessionId.value || '00000000-0000-0000-0000-000000000000');
      formData.append('message', content || 'Hãy phân tích nội dung file đính kèm.');
      formData.append('saveToKnowledge', String(saveAttachmentsToKnowledge.value));
      // Mirror the JSON branch so an OFF web-search toggle reaches the endpoint
      // instead of defaulting back to on when a file is attached.
      formData.append('enableWebSearch', String(webSearchEnabled.value));
      for (const file of attachedFiles.value) {
        formData.append('files', file);
      }
      attachedFiles.value = []; // Clear after sending
      saveAttachmentsToKnowledge.value = false;
      const response = await http.post(ApiFactory.CHAT.STREAM_WITH_FILES, formData);
      if (!response.ok) throw new Error(await readApiError(response, 'Yêu cầu chat thất bại'));
      await consumeStream({
        response,
        assistantMsg,
        scrollToBottom,
        onWebSearch: (urls) => { assistantMsg.webUrls = urls; },
      });
    } catch (error) {
      assistantMsg.content = `Lỗi: ${errorMessage(error, 'Không thể tạo câu trả lời.')}`;
      assistantMsg.isTyping = false;
    } finally {
      isGenerating.value = false;
      onSettled();
    }
    return;
  }

  // Text-only send: create the session on demand, then stream the JSON exchange.
  await streamExchange(
    async () => {
      await ensureSession();
      return {
        SessionId: currentSessionId.value || '00000000-0000-0000-0000-000000000000',
        Message: content,
        ModelId: null,
        enableWebSearch: webSearchEnabled.value,
        ephemeral: isEphemeral.value,
        ...(editing ? { replaceLastExchange: true } : {})
      };
    },
    assistantMsg,
    {
      requestErrorFallback: 'Yêu cầu chat thất bại',
      errorFallback: 'Không thể tạo câu trả lời.',
      onSettled,
    }
  );
};

// --- Regenerate the last answer ----------------------------------------------

const NO_REGENERATE_TARGET = 'Không có tin nhắn nào để tạo lại';

const regenerate = async () => {
  if (isGenerating.value || !currentSessionId.value) return;
  chatError.value = '';

  // Remove the trailing assistant reply locally; the server deletes its copy.
  const last = messages.value[messages.value.length - 1];
  if (last && last.role === 'assistant') messages.value.pop();

  isGenerating.value = true;
  messages.value.push({ role: 'assistant', content: '', isTyping: true });
  const assistantMsg = messages.value[messages.value.length - 1];
  await scrollToBottom();

  const discardPlaceholder = () => {
    const index = messages.value.indexOf(assistantMsg);
    if (index !== -1) messages.value.splice(index, 1);
  };
  const warnNothingToRegenerate = () => {
    discardPlaceholder();
    toast.add({ severity: 'warn', summary: 'Không thể tạo lại', detail: `${NO_REGENERATE_TARGET}.`, life: 5000 });
  };

  await streamExchange(
    {
      SessionId: currentSessionId.value,
      Message: '',
      ModelId: null,
      regenerate: true,
      enableWebSearch: webSearchEnabled.value,
      ephemeral: isEphemeral.value,
    },
    assistantMsg,
    {
      requestErrorFallback: 'Yêu cầu tạo lại thất bại',
      errorFallback: 'Không thể tạo lại câu trả lời.',
      // The "nothing to regenerate" signal may arrive as a thrown error...
      onError: (message) => {
        if (message.includes(NO_REGENERATE_TARGET)) {
          warnNothingToRegenerate();
          return true;
        }
        return false;
      },
      // ...or as plain stream content.
      onComplete: () => {
        if (assistantMsg.content.trim() === `[${NO_REGENERATE_TARGET}]`) warnNothingToRegenerate();
      },
    },
  );
};

// --- Share / revoke public link -----------------------------------------------

const shareBusy = ref(false);

const copyToClipboard = async (text: string): Promise<boolean> => {
  try {
    if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // Clipboard API can be unavailable/blocked; fall through to the legacy path.
  }
  try {
    const textarea = document.createElement('textarea');
    textarea.value = text;
    textarea.setAttribute('readonly', 'true');
    textarea.style.position = 'fixed';
    textarea.style.opacity = '0';
    document.body.appendChild(textarea);
    textarea.select();
    const copied = document.execCommand('copy');
    document.body.removeChild(textarea);
    return copied;
  } catch {
    return false;
  }
};

const shareSession = async () => {
  if (!currentSessionId.value || shareBusy.value) return;
  shareBusy.value = true;
  chatError.value = '';
  try {
    const response = await http.post(ApiFactory.SHARE.CREATE_LINK(currentSessionId.value));
    if (!response.ok) throw new Error(await readApiError(response, 'Không thể tạo liên kết chia sẻ'));
    const data = await response.json() as { token?: unknown };
    if (typeof data.token !== 'string' || !data.token) throw new Error('Máy chủ không trả về token chia sẻ.');
    const shareUrl = `${window.location.origin}/share/${data.token}`;
    const copied = await copyToClipboard(shareUrl);
    if (copied) {
      toast.add({
        severity: 'success',
        summary: 'Đã sao chép liên kết chia sẻ',
        detail: 'Liên kết chia sẻ cũ của đoạn chat này (nếu có) đã ngừng hoạt động.',
        life: 7000
      });
    } else {
      toast.add({
        severity: 'warn',
        summary: 'Không thể tự động sao chép',
        detail: `Liên kết chia sẻ: ${shareUrl} (liên kết cũ, nếu có, đã ngừng hoạt động).`,
        life: 12000
      });
    }
  } catch (error) {
    chatError.value = errorMessage(error, 'Không thể tạo liên kết chia sẻ.');
  } finally {
    shareBusy.value = false;
  }
};

const revokeShare = async () => {
  if (!currentSessionId.value || shareBusy.value) return;
  shareBusy.value = true;
  chatError.value = '';
  try {
    const response = await http.delete(ApiFactory.SHARE.REVOKE_LINK(currentSessionId.value));
    if (response.ok) {
      toast.add({
        severity: 'info',
        summary: 'Đã thu hồi liên kết chia sẻ',
        detail: 'Liên kết chia sẻ của đoạn chat này không còn truy cập được.',
        life: 6000
      });
    } else {
      chatError.value = await readApiError(response, 'Không thể thu hồi liên kết chia sẻ');
    }
  } catch (error) {
    chatError.value = errorMessage(error, 'Không thể thu hồi liên kết chia sẻ.');
  } finally {
    shareBusy.value = false;
  }
};

const { approveTask, rejectTask } = useHitlActions({
  messages,
  inputMessage,
  sendMessage,
  approvedSystemMessage: (result) => `Đã phê duyệt. Kết quả thực thi tool: ${result}`,
});
</script>
