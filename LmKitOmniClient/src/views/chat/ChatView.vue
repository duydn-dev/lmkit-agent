<template>
  <div class="flex-1 flex flex-col relative w-full min-h-0">
    <!-- Session Header: agent badge on the left, quiet icon actions on the right.
         Deliberately low-chrome — the transcript is the page, so canvas/share read as
         icon buttons with tooltips rather than labelled pills stacked across the top. -->
    <div v-if="currentSessionId" class="flex items-center justify-between gap-2 px-4 py-1.5 border-b border-gray-200 bg-white">
      <div class="flex items-center min-w-0">
        <span
          v-if="activeAgentName"
          role="note"
          class="flex items-center gap-1.5 px-2 py-0.5 rounded-full bg-violet-50 border border-violet-200 text-xs text-violet-700 max-w-[220px]"
          :title="`Đang chat với agent ${activeAgentName}`"
          :aria-label="`Đang chat với agent ${activeAgentName}`">
          <span aria-hidden="true">{{ activeAgentIcon || '🤖' }}</span>
          <span class="truncate font-medium">{{ activeAgentName }}</span>
        </span>
      </div>
      <div class="flex items-center gap-0.5">
        <button
          @click="toggleCanvasPanel"
          aria-label="Mở Canvas"
          :title="canvasCount > 0 ? `Canvas (${canvasCount} phiên bản)` : 'Canvas'"
          :aria-expanded="canvasPanelOpen"
          class="relative w-9 h-9 flex items-center justify-center rounded-lg text-gray-500 hover:text-gray-900 hover:bg-gray-100 transition-colors"
          :class="canvasPanelOpen ? 'bg-blue-50 text-blue-700' : ''">
          <i class="pi pi-palette text-base" aria-hidden="true"></i>
          <span v-if="canvasCount > 0" class="absolute -top-0.5 -right-0.5 min-w-4 h-4 px-1 rounded-full bg-blue-900 text-white text-[10px] font-semibold leading-4 flex items-center justify-center">{{ canvasCount > 9 ? '9+' : canvasCount }}</span>
        </button>
        <button
          @click="shareSession"
          :disabled="shareBusy"
          class="w-9 h-9 flex items-center justify-center rounded-lg text-gray-500 hover:text-gray-900 hover:bg-gray-100 disabled:opacity-50 transition-colors"
          aria-label="Tạo liên kết chia sẻ"
          title="Chia sẻ đoạn chat (tạo liên kết mới và sao chép)">
          <i class="pi pi-share-alt text-base" aria-hidden="true"></i>
        </button>
        <button
          @click="revokeShare"
          :disabled="shareBusy"
          class="w-9 h-9 flex items-center justify-center rounded-lg text-gray-400 hover:text-red-600 hover:bg-red-50 disabled:opacity-50 transition-colors"
          aria-label="Thu hồi liên kết chia sẻ"
          title="Thu hồi liên kết chia sẻ (liên kết cũ sẽ ngừng hoạt động)">
          <i class="pi pi-link text-base rotate-45" aria-hidden="true"></i>
        </button>
      </div>
    </div>

    <!-- Chat History -->
    <div ref="chatContainer" class="flex-1 min-h-0 overflow-y-auto scroll-smooth" role="log" aria-live="polite" aria-relevant="additions text" aria-label="Lịch sử trò chuyện">
      <div v-if="messages.length === 0" class="h-full flex flex-col items-center justify-center text-center px-4">
        <div class="w-16 h-16 rounded-full bg-chatgpt-brand flex items-center justify-center mb-6 shadow-lg shadow-chatgpt-brand/20">
          <i class="pi pi-sparkles text-2xl text-white"></i>
        </div>
        <h1 class="text-3xl font-bold mb-2">Hôm nay tôi có thể giúp gì cho bạn?</h1>
        <p class="text-gray-600 max-w-md">CILA - AI Agent hỗ trợ đa năng: phân tích tài liệu, tìm kiếm thông minh, tạo nội dung và nhiều hơn thế.</p>
      </div>

      <div v-else class="max-w-3xl mx-auto w-full py-6" :style="{ paddingBottom: composerHeight + 32 + 'px' }">
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
              <div class="font-semibold mb-1 text-sm text-gray-700">CILA - AI Agent</div>

              <!-- One reasoning panel: pipeline milestones AND the model's own chain-of-thought
                   live in the same card. They are two halves of one story (what the agent did /
                   what the model thought) and splitting them into a stack of separate boxes
                   made the transcript noisy — see the reasoning-in-panel decision. The
                   chain-of-thought stays expanded while it streams: hiding it behind a closed
                   disclosure is what made a working turn look like a hang. -->
              <div v-if="hasReasoning(msg)" class="mb-4 flex flex-col gap-1.5 p-3 rounded-lg bg-gray-50 border border-gray-200 w-fit min-w-[280px] max-w-[90%]">
                <button type="button" class="flex items-center gap-1.5 text-[11px] font-semibold text-gray-400 uppercase tracking-wider mb-1 cursor-pointer select-none w-fit hover:text-gray-500" :aria-expanded="!reasoningPanelCollapsed(msg)" @click="toggleReasoningPanel(msg)">
                  <i class="pi text-[10px]" :class="reasoningPanelCollapsed(msg) ? 'pi-chevron-right' : 'pi-chevron-down'" aria-hidden="true"></i>
                  <span>Quá trình suy luận</span>
                  <i v-if="reasoningStreaming(msg)" class="pi pi-spin pi-spinner text-[10px]" aria-hidden="true"></i>
                </button>
                <template v-if="!reasoningPanelCollapsed(msg)">
                <!-- Pipeline milestones are progress scaffolding: they matter only while the
                     turn runs, then give way to the chain-of-thought — the part worth rereading. -->
                <template v-if="isLiveTurn(msg)">
                <div v-for="(step, idx) in msg.thinkingSteps ?? []" :key="idx" 
                  class="text-[13px] flex items-start gap-2 py-0.5"
                  :class="idx === (msg.thinkingSteps?.length ?? 0) - 1 && msg.isTyping && !msg.content ? 'text-gray-700' : 'text-gray-500'">
                  <span class="mt-0.5 flex-shrink-0">
                    <i class="pi pi-spin pi-spinner text-gray-400" v-if="idx === (msg.thinkingSteps?.length ?? 0) - 1 && msg.isTyping && !msg.content"></i>
                    <i class="pi pi-check-circle text-gray-400" v-else></i>
                  </span>
                  <span class="leading-snug">{{ step }}</span>
                </div>
                </template>
                <!-- Model reasoning (DeepSeek-R1 style): the model's own chain-of-thought.
                     While streaming it sits under a live label below the milestones; once the
                     answer lands the milestones and the label drop away and the panel IS the
                     chain-of-thought. -->
                <div v-if="msg.reasoning" class="flex flex-col"
                  :class="reasoningStreaming(msg) ? 'mt-1 pt-2 border-t border-gray-200' : ''"
                  :aria-label="reasoningStreaming(msg) ? 'Mô hình đang suy luận' : 'Suy luận của mô hình'"
                  :role="reasoningStreaming(msg) ? 'status' : undefined">
                  <div v-if="reasoningStreaming(msg)" class="flex items-center gap-1.5 text-[11px] font-semibold uppercase tracking-wider text-gray-400 mb-1">
                    <i class="pi pi-spin pi-spinner text-[10px]" aria-hidden="true"></i>
                    <span>Đang suy luận</span>
                  </div>
                  <div class="reasoning-live max-h-44 overflow-y-auto whitespace-pre-wrap text-[13px] leading-snug text-gray-600">{{ msg.reasoning }}</div>
                </div>
                </template>
              </div>

              <!-- Web Search Chip -->
              <button v-if="msg.webUrls && msg.webUrls.length > 0" type="button" class="mb-3 min-h-11 flex items-center gap-2 cursor-pointer group/chip w-max" @click="openDrawer(msg.webUrls)">
                <div class="bg-blue-50 hover:bg-blue-100 text-gray-700 border border-gray-200 px-3 py-1.5 rounded-full flex items-center gap-2 transition-colors shadow-sm inline-flex">
                  <i class="pi pi-search text-xs"></i>
                  <span class="text-sm font-medium">Đã đọc {{ msg.webUrls.length }} trang web</span>
                  <div class="flex -space-x-1.5 ml-1">
                    <span v-for="(url, i) in msg.webUrls.slice(0, 3)" :key="i" class="relative w-5 h-5 rounded-full border border-gray-200 bg-white flex items-center justify-center overflow-hidden">
                      <i class="pi pi-globe text-[10px] text-blue-600"></i>
                      <img v-if="faviconUrl(url)" :src="faviconUrl(url)" alt="" loading="lazy" class="absolute inset-0 w-full h-full object-contain" @error="onFaviconError" />
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

              <!-- Thinking Indicator: ChatGPT-style shimmer shown while the pipeline
                   works (isTyping stays true until the first answer token). -->
              <div v-if="msg.isTyping" class="mt-2 mb-1 flex items-center gap-2 select-none" aria-live="polite">
                <span class="thinking-shimmer text-[15px] font-medium">Thinking...</span>
              </div>

              <!-- Legacy bouncing dots (kept for reduced-motion users). -->
              <div v-if="msg.isTyping" class="flex gap-1 mt-1 motion-reduce:block" style="display:none">
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
                <div class="flex gap-2" v-if="!msg.hitlResolved && !msg.hitlClosed">
                  <button @click="approveTask(msg)" :disabled="msg.hitlBusy" class="flex-1 min-h-11 px-4 py-2 bg-orange-600 hover:bg-orange-700 disabled:opacity-50 text-white text-sm font-medium rounded-lg transition-colors">
                    Phê duyệt
                  </button>
                  <button @click="rejectTask(msg)" :disabled="msg.hitlBusy" class="flex-1 min-h-11 px-4 py-2 bg-white hover:bg-gray-50 disabled:opacity-50 text-gray-700 border border-gray-300 text-sm font-medium rounded-lg transition-colors">
                    Từ chối
                  </button>
                </div>
                <div v-else-if="msg.hitlResolved" class="text-sm font-medium" :class="msg.hitlResolved === 'Approved' ? 'text-green-600' : 'text-red-600'">
                  Đã {{ msg.hitlResolved === 'Approved' ? 'Phê duyệt' : 'Từ chối' }} thao tác này.
                </div>
                <div v-else class="text-sm font-medium text-gray-600">
                  Yêu cầu này đã đóng — xem lý do ở trên.
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

    <div ref="composerOverlayRef" class="absolute bottom-0 left-0 right-0 bg-gradient-to-t from-chatgpt-dark via-chatgpt-dark to-transparent pt-10 pb-6 px-4">
      <div class="max-w-3xl mx-auto relative group">
        <div v-if="chatError" role="alert" class="mb-2 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {{ chatError }}
        </div>
        <!-- Edit-last-message mode banner -->
        <div v-if="isEditing" class="mb-2 flex items-center justify-between gap-3 rounded-lg border border-blue-200 bg-blue-50 px-4 py-2 text-sm text-blue-800">
          <span class="flex items-center gap-2 min-w-0">
            <i class="pi pi-pencil text-xs" aria-hidden="true"></i>
            <span class="truncate">Đang sửa tin nhắn cuối — gửi để thay thế cặp hỏi đáp trước.</span>
          </span>
          <button @click="cancelEditing" class="min-h-11 px-2 flex items-center gap-1 font-medium text-blue-800 hover:text-blue-950 transition-colors flex-shrink-0" aria-label="Hủy sửa tin nhắn">
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
              placeholder="Nhắn tin cho CILA - AI Agent..." />
          </div>
          
          <!-- Bottom Toolbar -->
          <div class="flex items-center justify-between mt-2 px-1 pb-1">
            <!-- Left: tool buttons -->
            <div class="flex items-center gap-1">
              <button
                @click="toggleWebSearch"
                :aria-pressed="webSearchEnabled"
                aria-label="Tìm kiếm web"
                class="min-w-10 min-h-10 px-2.5 flex items-center justify-center rounded-full border transition-colors"
                :class="webSearchEnabled ? 'border-blue-200 bg-blue-50 text-blue-700 hover:bg-blue-100' : 'border-transparent text-gray-500 hover:text-gray-900 hover:bg-gray-100'">
                <i class="pi pi-globe text-base" aria-hidden="true"></i>
              </button>
              <button
                @click="toggleEphemeral"
                :aria-pressed="isEphemeral"
                aria-label="Chat tạm thời"
                title="Chat tạm thời — không lưu vào lịch sử"
                class="min-w-10 min-h-10 px-2.5 flex items-center justify-center rounded-full border transition-colors"
                :class="isEphemeral ? 'border-amber-200 bg-amber-50 text-amber-700 hover:bg-amber-100' : 'border-transparent text-gray-500 hover:text-gray-900 hover:bg-gray-100'">
                <i class="pi pi-eye-slash text-base" aria-hidden="true"></i>
              </button>
              <button @click="triggerFileInput" class="min-w-10 min-h-10 flex items-center justify-center text-gray-500 hover:text-gray-900 transition-colors rounded-full hover:bg-gray-100" aria-label="Đính kèm file">
                <i class="pi pi-paperclip text-base"></i>
              </button>
              <button
                v-if="voiceSupported"
                @click="toggleVoiceInput"
                :aria-pressed="isRecording"
                aria-label="Nhập bằng giọng nói"
                :disabled="isTranscribing"
                class="min-w-10 min-h-10 px-2 flex items-center justify-center gap-1.5 rounded-full border transition-colors disabled:opacity-50"
                :class="isRecording ? 'border-red-200 bg-red-50 text-red-600 hover:bg-red-100' : 'border-transparent text-gray-500 hover:text-gray-900 hover:bg-gray-100'">
                <i :class="[isTranscribing ? 'pi pi-spin pi-spinner' : 'pi pi-microphone', isRecording ? 'animate-pulse' : '']" class="text-base" aria-hidden="true"></i>
                <span v-if="isRecording" class="text-xs font-medium tabular-nums">{{ voiceElapsedLabel }}</span>
              </button>
            </div>
            <!-- Right: send / stop -->
            <div class="flex items-center">
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
                rounded
                class="!w-11 !h-11 !bg-blue-900 !border-blue-900 hover:!bg-blue-950 hover:!border-blue-950"
              />
            </div>
          </div>
        </div>
        <div class="text-center text-xs text-gray-500 mt-3">
          CILA - AI Agent có thể mắc sai lầm. Vui lòng kiểm tra lại các thông tin quan trọng.
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
            <div class="relative w-8 h-8 rounded-lg bg-white shadow-sm flex-shrink-0 flex items-center justify-center overflow-hidden">
                <i class="pi pi-globe text-blue-600"></i>
                <img v-if="faviconUrl(url)" :src="faviconUrl(url)" alt="" loading="lazy" class="absolute inset-0 w-full h-full object-contain" @error="onFaviconError" />
            </div>
            <div class="flex-1 min-w-0">
              <div class="text-sm font-medium text-gray-800 truncate group-hover:text-blue-600 transition-colors">{{ getCleanHostname(url) }}</div>
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
  </div>
</template>

<script setup lang="ts">
import { computed, ref, nextTick, watch, onMounted, onBeforeUnmount, type ComponentPublicInstance } from 'vue';
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

const inputMessage = ref('');
const messages = ref<ChatMessage[]>([]);
const { consumeStream, stop, isStreaming } = useChatStream();
const toast = useToast();
const chatError = ref('');
const isGenerating = ref(false);
const chatContainer = ref<HTMLElement | null>(null);
// The composer is an absolutely-positioned overlay (gradient + textarea + toolbar)
// pinned to the bottom of the chat pane. A fixed list padding could not clear it:
// measured height grows with the attachment tray, error banner and edit banner, so
// the last bubble (e.g. the live "Thinking..." line) slid underneath the input and
// was unreadable. The padding is therefore driven by the composer's REAL height.
const composerOverlayRef = ref<HTMLElement | null>(null);
const composerHeight = ref(232);
let composerObserver: ResizeObserver | null = null;
const currentSessionId = ref<string | null>(null);
const attachedFiles = ref<File[]>([]);

// Keep the last ordinary (persisted) conversation so a browser reload returns to
// the transcript instead of showing the empty welcome screen. Ephemeral chats are
// deliberately never written here.
const LAST_SESSION_STORAGE_KEY = 'omni.lastChatSessionId';
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

/**
 * The reasoning panel shows when the turn has anything to say about HOW it got there.
 * While the turn runs that means milestones and/or arriving chain-of-thought; once the
 * answer lands the milestones are scaffolding nobody rereads, so the panel — and its
 * very existence — is decided by the chain-of-thought alone.
 */
const hasReasoning = (msg: ChatMessage): boolean =>
  isLiveTurn(msg)
    ? Boolean(msg.reasoning) || (msg.thinkingSteps?.length ?? 0) > 0
    : Boolean(msg.reasoning);

/** True while this exact message is the turn still being generated. */
const isLiveTurn = (msg: ChatMessage): boolean =>
  isGenerating.value && messages.value[messages.value.length - 1] === msg;

/** The chain-of-thought is still arriving, so the panel label reads as in-progress. */
const reasoningStreaming = (msg: ChatMessage): boolean => Boolean(msg.reasoning) && isLiveTurn(msg);

/**
 * Per-message collapse for the reasoning panel. The live turn is never force-collapsed
 * (a collapsing panel would look like the turn stalled), and once expanded a panel stays
 * open — only the user, via the chevron, closes it.
 */
const collapsedReasoningPanels = ref(new WeakSet<ChatMessage>());
const reasoningPanelCollapsed = (msg: ChatMessage): boolean =>
  collapsedReasoningPanels.value.has(msg) && !isLiveTurn(msg);
const toggleReasoningPanel = (msg: ChatMessage): void => {
  if (reasoningPanelCollapsed(msg)) collapsedReasoningPanels.value.delete(msg);
  else collapsedReasoningPanels.value.add(msg);
};

/**
 * Favicon for a citation: Google's public s2 service, keyed by the site's origin so the
 * CDN cache stays warm. Kept in the template as a plain function (not a computed map) —
 * the URL depends only on the host, so re-running it per render is free.
 */
const faviconUrl = (urlStr: string): string => {
  try { return `https://www.google.com/s2/favicons?domain=${new URL(urlStr).origin}&sz=32`; }
  catch { return ''; }
};
/** On load failure (offline, blocked host, CSP) the globe icon shows through instead of a broken image. */
const onFaviconError = (event: Event): void => {
  (event.target as HTMLElement).style.display = 'none';
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
  const sessionId = currentSessionId.value;
  if (!sessionId) return;
  chatError.value = '';
  try {
    const response = await http.get(ApiFactory.CHAT.GET_MESSAGES(sessionId));
    if (response.ok) {
      const data = await response.json();
      if (currentSessionId.value !== sessionId) return;
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
    } else {
      if (response.status === 404) {
        // A deleted/expired remembered session should not trap the user in a
        // broken route on every reload.
        try {
          if (localStorage.getItem(LAST_SESSION_STORAGE_KEY) === sessionId)
            localStorage.removeItem(LAST_SESSION_STORAGE_KEY);
        } catch { /* storage may be unavailable */ }
        currentSessionId.value = null;
      }
      chatError.value = await readApiError(response, 'Không thể tải nội dung đoạn chat');
    }
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
  // Track the composer's real height so the transcript always clears the overlay.
  if (typeof ResizeObserver !== 'undefined' && composerOverlayRef.value) {
    composerObserver = new ResizeObserver((entries) => {
      // borderBoxSize includes the overlay's own padding (pt-10 gradient + pb-6),
      // which contentRect omits — the transcript must clear the whole overlay.
      const entry = entries[0];
      const height = entry?.borderBoxSize?.[0]?.blockSize ?? entry?.contentRect.height;
      if (height) composerHeight.value = Math.ceil(height);
    });
    composerObserver.observe(composerOverlayRef.value);
    composerHeight.value =
      Math.ceil(composerOverlayRef.value.getBoundingClientRect().height) || composerHeight.value;
  }

  if (route.query.id && typeof route.query.id === 'string') {
    // Opening a saved conversation is never temporary.
    isEphemeral.value = false;
    currentSessionId.value = route.query.id;
    try { localStorage.setItem(LAST_SESSION_STORAGE_KEY, route.query.id); } catch { /* best effort */ }
    void loadMessages();
    return;
  }

  // `/chat?new=...` is an explicit request for a blank composer. With no
  // navigation hint, restore the last persisted conversation after a reload.
  if (!route.query.new) {
    try {
      const rememberedId = localStorage.getItem(LAST_SESSION_STORAGE_KEY);
      if (rememberedId) {
        isEphemeral.value = false;
        currentSessionId.value = rememberedId;
        void loadMessages();
      }
    } catch {
      // Storage may be unavailable; the normal welcome screen remains usable.
    }
  }
});

// Watch the full query object, not just `id`: "New chat" navigates /chat →
// /chat?new=... leaving `id` undefined on BOTH routes, so an id-only watcher
// never fired and the previous transcript stayed on screen — the next message
// silently appended to the old session instead of starting a fresh one.
watch(() => route.query, (query) => {
  const newId = typeof query.id === 'string' ? query.id : undefined;
  if (newId) {
    cancelEditing();
    // A saved session opened from history leaves temporary mode.
    isEphemeral.value = false;
    currentSessionId.value = newId;
    try { localStorage.setItem(LAST_SESSION_STORAGE_KEY, newId); } catch { /* best effort */ }
    void loadMessages();
  } else if (query.new) {
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
  if (!isEphemeral.value) {
    try { localStorage.setItem(LAST_SESSION_STORAGE_KEY, newSession.id); } catch { /* best effort */ }
  }
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

/**
 * " Liên kết sẽ hết hạn vào <ngày giờ>." for a usable ISO timestamp, otherwise the
 * empty string so the toast reads exactly as it did before this field existed.
 */
const formatShareExpiry = (expiresAtUtc: unknown): string => {
  if (typeof expiresAtUtc !== 'string' || !expiresAtUtc) return '';
  const date = new Date(expiresAtUtc);
  if (Number.isNaN(date.getTime())) return '';
  return ` Liên kết sẽ hết hạn vào ${date.toLocaleString('vi-VN')}.`;
};

const shareSession = async () => {
  if (!currentSessionId.value || shareBusy.value) return;
  shareBusy.value = true;
  chatError.value = '';
  try {
    const response = await http.post(ApiFactory.SHARE.CREATE_LINK(currentSessionId.value));
    if (!response.ok) throw new Error(await readApiError(response, 'Không thể tạo liên kết chia sẻ'));
    const data = await response.json() as { token?: unknown; expiresAtUtc?: unknown };
    if (typeof data.token !== 'string' || !data.token) throw new Error('Máy chủ không trả về token chia sẻ.');
    const shareUrl = `${window.location.origin}/share/${data.token}`;
    // Share links now expire. The owner is the only one who can act on that — they are
    // the one who has to re-share before the deadline — so it is stated at the moment
    // the link is handed out, not left for the recipient to discover as a dead URL.
    // Silently omitted if the server did not send a usable date.
    const expiryNote = formatShareExpiry(data.expiresAtUtc);
    const copied = await copyToClipboard(shareUrl);
    if (copied) {
      toast.add({
        severity: 'success',
        summary: 'Đã sao chép liên kết chia sẻ',
        detail: `Liên kết chia sẻ cũ của đoạn chat này (nếu có) đã ngừng hoạt động.${expiryNote}`,
        life: 7000
      });
    } else {
      toast.add({
        severity: 'warn',
        summary: 'Không thể tự động sao chép',
        detail: `Liên kết chia sẻ: ${shareUrl} (liên kết cũ, nếu có, đã ngừng hoạt động).${expiryNote}`,
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

onBeforeUnmount(() => {
  composerObserver?.disconnect();
  composerObserver = null;
});
</script>

<style scoped>
/* ChatGPT-style animated shimmer for the "Thinking..." indicator: a gray base
   text with a lighter sweep sliding across it, looping while isTyping is true. */
.thinking-shimmer {
  background: linear-gradient(90deg, #6b7280 25%, #d1d5db 50%, #6b7280 75%);
  background-size: 200% 100%;
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
  animation: thinking-sweep 1.8s linear infinite;
}

@keyframes thinking-sweep {
  from { background-position: 200% 0; }
  to { background-position: -200% 0; }
}

@media (prefers-reduced-motion: reduce) {
  .thinking-shimmer {
    animation: none;
    color: #6b7280;
    background: none;
  }
}
</style>
