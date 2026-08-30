<template>
  <div class="flex-1 flex flex-col h-full bg-chatgpt-dark overflow-y-auto">
    <!-- Page Header -->
    <div class="sticky top-0 z-10 bg-chatgpt-dark/80 backdrop-blur-xl border-b border-gray-200/60">
      <div class="max-w-4xl mx-auto px-6 py-4">
        <div class="flex items-center gap-4">
          <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-teal-500 to-cyan-600 flex items-center justify-center shadow-md shadow-teal-500/20">
            <i class="pi pi-compass text-white text-sm"></i>
          </div>
          <div>
            <h1 class="text-xl font-bold text-gray-900 tracking-tight">Nghiên cứu chuyên sâu</h1>
            <p class="text-xs text-gray-500">Trợ lý tự tìm kiếm nhiều nguồn, đối chiếu và tổng hợp báo cáo</p>
          </div>
        </div>
      </div>
    </div>

    <!-- Main Content -->
    <div class="flex-1 max-w-4xl mx-auto w-full px-6 py-6">
      <!-- Query form -->
      <form @submit.prevent="startResearch" class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5 mb-6">
        <div class="grid gap-1 mb-4">
          <label for="research-query" class="text-sm font-medium text-gray-700">Chủ đề nghiên cứu</label>
          <Textarea
            id="research-query"
            v-model="query"
            rows="3"
            :disabled="isRunning"
            placeholder="Ví dụ: So sánh các khung pháp lý về AI tại Việt Nam và EU năm 2026..."
            class="w-full !text-base"
          />
        </div>

        <div class="flex flex-col sm:flex-row sm:items-end gap-3">
          <div class="grid gap-1">
            <label for="research-max-sources" class="text-sm font-medium text-gray-700">Số nguồn tối đa</label>
            <Select
              v-model="maxSources"
              :options="sourceOptions"
              optionLabel="label"
              optionValue="value"
              inputId="research-max-sources"
              :disabled="isRunning"
              class="w-40"
            />
          </div>
          <div class="flex items-center gap-2 sm:ml-auto">
            <Button
              v-if="isRunning"
              type="button"
              label="Dừng"
              icon="pi pi-stop-circle"
              severity="danger"
              outlined
              @click="stopResearch"
              class="!min-h-11 !px-4 !rounded-xl !text-sm"
            />
            <Button
              type="submit"
              label="Bắt đầu nghiên cứu"
              icon="pi pi-compass"
              :loading="isRunning"
              :disabled="isRunning || !query.trim()"
              class="!min-h-11 !px-5 !rounded-xl !text-sm !font-medium !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800"
            />
          </div>
        </div>
      </form>

      <div v-if="researchError" role="alert" class="mb-5 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        {{ researchError }}
      </div>

      <div v-if="savedRootId" role="status" class="mb-5 rounded-xl border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-900">
        <i class="pi pi-check-circle mr-1.5" aria-hidden="true"></i>Đã lưu vào Canvas.
      </div>

      <!-- Live progress steps -->
      <section v-if="thinkingSteps.length > 0" class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5 mb-6" aria-label="Tiến trình nghiên cứu">
        <h2 class="text-sm font-semibold text-gray-900 mb-3 flex items-center gap-2">
          <i v-if="isRunning" class="pi pi-spin pi-spinner text-sky-600" aria-hidden="true"></i>
          <i v-else class="pi pi-check-circle text-emerald-600" aria-hidden="true"></i>
          Tiến trình nghiên cứu
        </h2>
        <ol class="grid gap-2" aria-live="polite">
          <li v-for="(step, index) in thinkingSteps" :key="index" class="flex items-start gap-2.5 text-xs text-gray-600">
            <span class="mt-0.5 w-5 h-5 rounded-full bg-sky-50 border border-sky-200 text-sky-700 text-[10px] font-semibold flex items-center justify-center flex-shrink-0" aria-hidden="true">{{ index + 1 }}</span>
            <span class="leading-relaxed">{{ step }}</span>
          </li>
        </ol>
      </section>

      <!-- Report -->
      <section v-if="reportContent" class="bg-white rounded-2xl border border-gray-100 shadow-sm p-6" aria-label="Báo cáo nghiên cứu">
        <h2 class="text-sm font-semibold text-gray-900 mb-3 flex items-center gap-2">
          <i class="pi pi-file-edit text-teal-600" aria-hidden="true"></i>
          Báo cáo
        </h2>
        <!-- Content passes through formatSafeMessage (escape-then-format), the
             same sanitizer the chat surfaces use for their only v-html sink. -->
        <div class="text-sm leading-relaxed whitespace-pre-wrap break-words text-gray-800 markdown-body" v-html="formattedReport"></div>
      </section>

      <!-- Idle empty state -->
      <div v-if="!isRunning && !reportContent && thinkingSteps.length === 0 && !researchError" class="flex flex-col items-center justify-center py-16 text-center">
        <div class="w-20 h-20 rounded-2xl bg-gradient-to-br from-gray-100 to-gray-200 flex items-center justify-center mb-5 shadow-inner">
          <i class="pi pi-compass text-3xl text-gray-300" aria-hidden="true"></i>
        </div>
        <h3 class="text-lg font-semibold text-gray-600 mb-1">Bắt đầu một nghiên cứu mới</h3>
        <p class="text-sm text-gray-400 max-w-sm">Nhập chủ đề phía trên, trợ lý sẽ hiển thị từng bước tìm kiếm và tổng hợp báo cáo hoàn chỉnh tại đây. Kết quả được lưu vào Canvas.</p>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onUnmounted, ref } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import { ChatSseParser } from '@/utils/chatSse';
import { formatSafeMessage } from '@/utils/safeFormatting';

const sourceOptions = [2, 3, 4, 5].map((value) => ({ label: `${value} nguồn`, value }));

const query = ref('');
const maxSources = ref(3);
const isRunning = ref(false);
const thinkingSteps = ref<string[]>([]);
const reportContent = ref('');
const savedRootId = ref('');
const researchError = ref('');

const formattedReport = computed(() => formatSafeMessage(reportContent.value));

/**
 * Abort seam for the in-flight research stream. `http.post` exposes no
 * `signal`, so — exactly like useChatStream — aborting cancels the active
 * response-body reader, which tears the fetch down and lets the pending
 * `reader.read()` resolve with `done: true` (partial output is kept).
 */
let controller: AbortController | null = null;

const startResearch = async () => {
  const trimmedQuery = query.value.trim();
  if (!trimmedQuery || isRunning.value) return;

  researchError.value = '';
  thinkingSteps.value = [];
  reportContent.value = '';
  savedRootId.value = '';
  isRunning.value = true;

  controller?.abort();
  const localController = new AbortController();
  controller = localController;

  try {
    const response = await http.post(ApiFactory.RESEARCH.RUN, {
      query: trimmedQuery,
      maxSources: maxSources.value
    });
    if (!response.ok) throw new Error(await readApiError(response, 'Không thể bắt đầu nghiên cứu'));
    if (!response.body) throw new Error('Trình duyệt không hỗ trợ streaming response.');

    const reader = response.body.getReader();
    const decoder = new TextDecoder('utf-8');
    const onAbort = () => { void reader.cancel().catch(() => {}); };
    localController.signal.addEventListener('abort', onAbort);
    // "Dừng" can be pressed while the POST itself is still in flight; an
    // already-aborted signal never re-fires its event, so cancel directly.
    if (localController.signal.aborted) onAbort();

    const parser = new ChatSseParser();
    try {
      let finished = false;
      while (!finished) {
        const { done, value } = await reader.read();
        const events = done
          ? [...parser.push(decoder.decode()), ...parser.finish()]
          : parser.push(decoder.decode(value, { stream: true }));
        for (const event of events) {
          if (event.type === 'done') {
            finished = true;
            break;
          }
          if (event.type === 'error') throw new Error(event.value);
          if (event.type === 'thinking') {
            // Parser already strips the "[THINKING]: " prefix.
            thinkingSteps.value.push(event.value);
            continue;
          }
          if (event.type !== 'content') continue;

          const savedMatch = event.value.match(/^\[RESEARCH_SAVED:(.+)\]$/);
          if (savedMatch) {
            savedRootId.value = savedMatch[1] ?? '';
            continue;
          }
          reportContent.value += event.value;
        }
        if (done) break;
      }
      if (finished) await reader.cancel();
    } finally {
      localController.signal.removeEventListener('abort', onAbort);
    }
  } catch (cause) {
    // A user-initiated stop is not an error; keep whatever was produced.
    if (!localController.signal.aborted) {
      researchError.value = errorMessage(cause, 'Không thể hoàn tất nghiên cứu.');
    }
  } finally {
    if (controller === localController) controller = null;
    isRunning.value = false;
  }
};

const stopResearch = () => {
  controller?.abort();
  controller = null;
};

onUnmounted(stopResearch);
</script>
