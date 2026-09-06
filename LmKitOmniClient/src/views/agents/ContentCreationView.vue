<template>
  <div class="flex-1 overflow-y-auto bg-gray-50 p-4 md:p-6">
    <div class="max-w-3xl mx-auto">
      <header class="mb-6 flex items-center gap-4">
        <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-fuchsia-500 to-purple-600 flex items-center justify-center shadow-md shadow-fuchsia-500/20 flex-shrink-0">
          <i class="pi pi-pen-to-square text-white text-sm" aria-hidden="true"></i>
        </div>
        <div>
          <h1 class="text-xl font-bold text-gray-900 tracking-tight">Tạo nội dung</h1>
          <p class="text-sm text-gray-500">Pipeline đa giai đoạn (nghiên cứu → dàn ý → soạn thảo → kiểm chứng) chạy trên agent backend.</p>
        </div>
      </header>

      <form class="space-y-4" @submit.prevent="run">
        <section class="rounded-xl border border-gray-200 bg-white p-5">
          <label for="topic" class="text-sm font-semibold text-gray-900">Chủ đề</label>
          <textarea
            id="topic"
            v-model="topic"
            rows="3"
            maxlength="2000"
            required
            class="mt-2 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-fuchsia-400 focus:ring-1 focus:ring-fuchsia-300"
            placeholder="Ví dụ: Viết bài giới thiệu về lợi ích của việc làm việc từ xa cho đội ngũ kỹ thuật…"
          ></textarea>
          <p class="mt-1 text-xs text-gray-500">{{ topic.length }}/2000 ký tự. Thời gian chạy phụ thuộc model và độ dài nội dung.</p>
          <div class="mt-3">
            <button type="submit" :disabled="running || topic.trim().length === 0" class="rounded-lg bg-fuchsia-600 px-5 py-2.5 text-sm font-semibold text-white hover:bg-fuchsia-700 disabled:opacity-50">
              <i :class="running ? 'pi pi-spin pi-spinner' : 'pi pi-play'" class="mr-2 text-xs" aria-hidden="true"></i>
              {{ running ? 'Đang chạy…' : 'Chạy pipeline' }}
            </button>
          </div>
        </section>
      </form>

      <div v-if="error" class="mt-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700" role="alert">{{ error }}</div>

      <!-- Result -->
      <section v-if="result" class="mt-6 space-y-4" aria-live="polite">
        <!-- Stage timeline -->
        <div class="rounded-xl border border-gray-200 bg-white p-5">
          <div class="flex items-center justify-between mb-3">
            <h2 class="text-sm font-semibold text-gray-900">Các giai đoạn</h2>
            <span class="text-xs text-gray-500">{{ result.totalDurationSeconds.toFixed(1) }}s</span>
          </div>
          <ol class="space-y-2">
            <li v-for="(stage, index) in result.stages" :key="index" class="flex items-start gap-2.5 text-sm">
              <i
                :class="stage.isSuccess ? 'pi pi-check-circle text-green-600' : 'pi pi-times-circle text-red-500'"
                class="mt-0.5 flex-shrink-0"
                aria-hidden="true"
              ></i>
              <div class="min-w-0">
                <span class="font-medium text-gray-900">{{ stage.stageName }}</span>
                <span v-if="!stage.isSuccess" class="ml-2 text-xs text-red-600">{{ stage.errorMessage }}</span>
              </div>
            </li>
          </ol>
        </div>

        <!-- Final content -->
        <div class="rounded-xl border border-gray-200 bg-white p-5">
          <h2 class="text-sm font-semibold text-gray-900 mb-3">Nội dung hoàn chỉnh</h2>
          <div class="rounded-lg border border-gray-100 bg-gray-50 p-4 text-sm leading-relaxed text-gray-800 whitespace-pre-wrap break-words">{{ result.finalContent || '(Không có nội dung)' }}</div>
        </div>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';

interface AgentStageResult {
  stageName: string;
  content: string;
  isSuccess: boolean;
  errorMessage: string;
}

interface PipelineResult {
  finalContent: string;
  stages: AgentStageResult[];
  totalDurationSeconds: number;
}

const topic = ref('');
const running = ref(false);
const error = ref('');
const result = ref<PipelineResult | null>(null);

const run = async () => {
  const content = topic.value.trim();
  if (!content || running.value) return;

  running.value = true;
  error.value = '';
  result.value = null;

  try {
    const response = await http.post(ApiFactory.AGENTS.CONTENT_PIPELINE, { topic: content });
    if (!response.ok) throw new Error(await readApiError(response, 'Pipeline tạo nội dung thất bại.'));
    const data = await response.json();
    result.value = {
      finalContent: data?.finalContent ?? '',
      stages: Array.isArray(data?.stages) ? data.stages : [],
      totalDurationSeconds: typeof data?.totalDurationSeconds === 'number' ? data.totalDurationSeconds : 0
    };
  } catch (err) {
    error.value = errorMessage(err, 'Không thể chạy pipeline tạo nội dung.');
  } finally {
    running.value = false;
  }
};
</script>
