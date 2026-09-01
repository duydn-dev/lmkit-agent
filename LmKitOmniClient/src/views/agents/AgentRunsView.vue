<template>
  <div class="flex-1 flex flex-col h-full bg-chatgpt-dark overflow-y-auto">
    <!-- Page Header -->
    <div class="sticky top-0 z-10 bg-chatgpt-dark/80 backdrop-blur-xl border-b border-gray-200/60">
      <div class="max-w-7xl mx-auto px-6 py-4">
        <div class="flex items-center gap-4">
          <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-indigo-500 to-violet-600 flex items-center justify-center shadow-md shadow-indigo-500/20">
            <i class="pi pi-bolt text-white text-sm" aria-hidden="true"></i>
          </div>
          <div>
            <h1 class="text-xl font-bold text-gray-900 tracking-tight">Agent tự hành</h1>
            <p class="text-xs text-gray-500">Giao một mục tiêu; agent tự lập kế hoạch và thực thi công cụ theo nhiều bước.</p>
          </div>
        </div>
      </div>
    </div>

    <!-- Main Content -->
    <div class="flex-1 max-w-7xl mx-auto w-full px-6 py-6">
      <p class="mb-6 flex items-start gap-2 text-xs text-gray-500">
        <i class="pi pi-info-circle mt-0.5 text-gray-400" aria-hidden="true"></i>
        <span>Agent sử dụng cùng bộ công cụ và quyền (RBAC/HITL) như khi trò chuyện — các hành động nhạy cảm vẫn cần được phê duyệt trước khi chạy.</span>
      </p>

      <div class="grid grid-cols-1 gap-6 lg:grid-cols-[1fr_320px]">
        <!-- ============ MAIN COLUMN: launcher + live/selected run ============ -->
        <div class="min-w-0 flex flex-col gap-6">
          <!-- Launcher -->
          <section aria-labelledby="launcher-heading" class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5">
            <h2 id="launcher-heading" class="text-sm font-semibold text-gray-900 mb-3">Giao mục tiêu</h2>
            <form @submit.prevent="runGoal">
              <div class="grid gap-1 mb-3">
                <div class="flex items-center justify-between gap-2">
                  <label for="agent-goal" class="text-sm font-medium text-gray-700">Mục tiêu</label>
                  <span class="text-xs tabular-nums" :class="goalTooLong ? 'text-red-600' : 'text-gray-400'">
                    {{ goal.length }}/{{ MAX_GOAL }}
                  </span>
                </div>
                <Textarea
                  id="agent-goal"
                  v-model="goal"
                  rows="3"
                  :maxlength="MAX_GOAL"
                  :disabled="isStreaming"
                  placeholder="Ví dụ: Tổng hợp doanh thu quý gần nhất từ tài liệu nội bộ và lập biểu đồ so sánh theo tháng..."
                  class="w-full !text-base"
                />
              </div>

              <div class="flex items-center justify-end gap-2">
                <Button
                  v-if="isStreaming"
                  type="button"
                  label="Dừng"
                  icon="pi pi-stop-circle"
                  severity="danger"
                  outlined
                  @click="stopRun"
                  class="!min-h-11 !px-4 !rounded-xl !text-sm"
                />
                <Button
                  type="submit"
                  label="Chạy"
                  icon="pi pi-bolt"
                  :loading="isStreaming"
                  :disabled="isStreaming || !goal.trim() || goalTooLong"
                  class="!min-h-11 !px-5 !rounded-xl !text-sm !font-medium !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800"
                />
              </div>
            </form>
          </section>

          <!-- ---------- Selected past run detail ---------- -->
          <section
            v-if="selectedRunId"
            aria-labelledby="detail-heading"
            class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5"
          >
            <div class="flex items-start justify-between gap-3 mb-3">
              <h2 id="detail-heading" class="text-sm font-semibold text-gray-900">Chi tiết lần chạy</h2>
              <Button
                type="button"
                label="Đóng"
                icon="pi pi-times"
                text
                severity="secondary"
                @click="closeDetail"
                aria-label="Đóng chi tiết lần chạy"
                class="!min-h-11 !px-3 !rounded-xl !text-sm flex-shrink-0"
              />
            </div>

            <div v-if="detailLoading" role="status" class="flex flex-col items-center justify-center py-12 text-gray-500">
              <i class="pi pi-spin pi-spinner text-2xl mb-3" aria-hidden="true"></i>
              <p class="text-sm">Đang tải chi tiết lần chạy...</p>
            </div>

            <div v-else-if="detailError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
              {{ detailError }}
            </div>

            <div v-else-if="detail" class="grid gap-4">
              <!-- Goal + status -->
              <div class="grid gap-2">
                <div class="flex flex-wrap items-center gap-2">
                  <span
                    class="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-semibold border whitespace-nowrap"
                    :class="statusMeta(detail.status).classes"
                  >{{ statusMeta(detail.status).label }}</span>
                  <span class="text-xs text-gray-500">{{ detail.steps.length }} bước</span>
                  <span class="text-xs text-gray-400" aria-hidden="true">·</span>
                  <span class="text-xs text-gray-500">
                    Tạo lúc <time :datetime="detail.createdAtUtc" :title="absoluteTime(detail.createdAtUtc)">{{ relativeTime(detail.createdAtUtc) }}</time>
                  </span>
                </div>
                <p class="text-sm font-medium text-gray-900 whitespace-pre-wrap break-words">{{ detail.goal }}</p>
              </div>

              <!-- Step timeline -->
              <div v-if="detail.steps.length > 0">
                <h3 class="text-xs font-semibold uppercase tracking-wide text-gray-400 mb-2">Các bước thực thi</h3>
                <ol class="grid gap-2.5">
                  <li v-for="step in detail.steps" :key="step.ordinal" class="rounded-xl border border-gray-200 bg-white p-4">
                    <div class="flex items-center gap-2.5 flex-wrap">
                      <span class="w-6 h-6 rounded-full bg-sky-50 border border-sky-200 text-sky-700 text-xs font-semibold flex items-center justify-center flex-shrink-0" aria-hidden="true">{{ step.ordinal }}</span>
                      <span class="inline-flex items-center gap-1.5 px-2 py-1 rounded-md bg-gray-100 border border-gray-200 text-xs font-mono text-gray-700">
                        <i class="pi pi-wrench text-[10px] text-gray-400" aria-hidden="true"></i>{{ step.action || 'công cụ' }}
                      </span>
                    </div>
                    <details v-if="step.input" class="mt-2.5">
                      <summary class="inline-flex items-center gap-1.5 min-h-11 cursor-pointer select-none text-xs font-medium text-gray-600 hover:text-gray-800">
                        <i class="pi pi-chevron-right text-[10px] agent-disclosure-icon transition-transform" aria-hidden="true"></i>
                        Đầu vào
                      </summary>
                      <pre class="mt-1.5 whitespace-pre-wrap break-words rounded-lg bg-gray-50 border border-gray-200 p-2.5 text-xs font-mono text-gray-700 overflow-x-auto">{{ step.input }}</pre>
                    </details>
                    <div v-if="step.observation" class="mt-2.5">
                      <div class="text-[11px] font-medium uppercase tracking-wide text-gray-400 mb-1">Kết quả</div>
                      <pre class="whitespace-pre-wrap break-words rounded-lg bg-gray-50 border border-gray-200 p-2.5 text-xs text-gray-700 overflow-x-auto">{{ step.observation }}</pre>
                    </div>
                  </li>
                </ol>
              </div>

              <!-- Error / result -->
              <div v-if="detail.error" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
                <div class="text-[11px] font-medium uppercase tracking-wide text-red-800 mb-1">Lỗi</div>
                <div class="whitespace-pre-wrap break-words">{{ detail.error }}</div>
              </div>
              <div v-if="detail.result">
                <h3 class="text-xs font-semibold uppercase tracking-wide text-gray-400 mb-1.5">Kết quả</h3>
                <div class="whitespace-pre-wrap break-words text-sm leading-relaxed text-gray-800">{{ detail.result }}</div>
              </div>
            </div>
          </section>

          <!-- ---------- Live run output (when not viewing a past run) ---------- -->
          <template v-else>
            <div v-if="runError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
              {{ runError }}
            </div>

            <div v-if="approvalTaskId" role="status" class="rounded-xl border border-sky-200 bg-sky-50 px-4 py-3 text-sm text-sky-900">
              <i class="pi pi-clock mr-1.5" aria-hidden="true"></i>
              Đang chờ phê duyệt trước khi tiếp tục.
              <router-link to="/approvals" class="font-semibold underline underline-offset-2 hover:text-sky-950 focus-visible:ring-2 focus-visible:ring-sky-500 rounded">
                Mở trang phê duyệt
              </router-link>
            </div>

            <!-- Live run panel -->
            <section
              v-if="hasLiveActivity"
              aria-labelledby="live-heading"
              class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5"
            >
              <div class="flex items-center justify-between gap-3 mb-3">
                <h2 id="live-heading" class="text-sm font-semibold text-gray-900 flex items-center gap-2">
                  <i v-if="isStreaming" class="pi pi-spin pi-spinner text-sky-600" aria-hidden="true"></i>
                  Phiên hiện tại
                </h2>
                <span
                  v-if="runStatus"
                  class="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-semibold border whitespace-nowrap"
                  :class="statusMeta(runStatus).classes"
                >{{ statusMeta(runStatus).label }}</span>
              </div>

              <!-- Thinking / status log -->
              <div v-if="statusLines.length > 0" class="mb-4">
                <h3 class="text-xs font-semibold uppercase tracking-wide text-gray-400 mb-1.5">Nhật ký</h3>
                <ol aria-live="polite" class="grid gap-1.5 max-h-40 overflow-y-auto rounded-lg bg-gray-50 border border-gray-200 p-3">
                  <li v-for="(line, index) in statusLines" :key="index" class="flex items-start gap-2 text-xs text-gray-500 leading-relaxed">
                    <i class="pi pi-angle-right mt-0.5 text-gray-300 text-[10px]" aria-hidden="true"></i>
                    <span class="min-w-0 break-words">{{ line }}</span>
                  </li>
                </ol>
              </div>

              <!-- Step timeline -->
              <div v-if="steps.length > 0" class="mb-4">
                <h3 class="text-xs font-semibold uppercase tracking-wide text-gray-400 mb-2">Các bước thực thi</h3>
                <ol aria-live="polite" class="grid gap-2.5">
                  <li v-for="step in steps" :key="step.ordinal" class="rounded-xl border border-gray-200 bg-white p-4">
                    <div class="flex items-center gap-2.5 flex-wrap">
                      <span class="w-6 h-6 rounded-full bg-sky-50 border border-sky-200 text-sky-700 text-xs font-semibold flex items-center justify-center flex-shrink-0" aria-hidden="true">{{ step.ordinal }}</span>
                      <span class="inline-flex items-center gap-1.5 px-2 py-1 rounded-md bg-gray-100 border border-gray-200 text-xs font-mono text-gray-700">
                        <i class="pi pi-wrench text-[10px] text-gray-400" aria-hidden="true"></i>{{ step.action || 'công cụ' }}
                      </span>
                    </div>
                    <details v-if="step.input" class="mt-2.5">
                      <summary class="inline-flex items-center gap-1.5 min-h-11 cursor-pointer select-none text-xs font-medium text-gray-600 hover:text-gray-800">
                        <i class="pi pi-chevron-right text-[10px] agent-disclosure-icon transition-transform" aria-hidden="true"></i>
                        Đầu vào
                      </summary>
                      <pre class="mt-1.5 whitespace-pre-wrap break-words rounded-lg bg-gray-50 border border-gray-200 p-2.5 text-xs font-mono text-gray-700 overflow-x-auto">{{ step.input }}</pre>
                    </details>
                    <div v-if="step.observation" class="mt-2.5">
                      <div class="text-[11px] font-medium uppercase tracking-wide text-gray-400 mb-1">Kết quả</div>
                      <pre class="whitespace-pre-wrap break-words rounded-lg bg-gray-50 border border-gray-200 p-2.5 text-xs text-gray-700 overflow-x-auto">{{ step.observation }}</pre>
                    </div>
                  </li>
                </ol>
              </div>

              <!-- Final synthesized result -->
              <div v-if="resultText" class="mb-4">
                <h3 class="text-xs font-semibold uppercase tracking-wide text-gray-400 mb-1.5">Kết quả</h3>
                <div class="whitespace-pre-wrap break-words text-sm leading-relaxed text-gray-800">{{ resultText }}</div>
              </div>

              <!-- Produced files (charts / CSVs a tool returned) -->
              <div v-if="producedFiles.length > 0">
                <h3 class="text-xs font-semibold uppercase tracking-wide text-gray-400 mb-1.5">Tệp kết quả</h3>
                <div class="flex flex-wrap gap-2">
                  <template v-for="file in producedFiles" :key="file.id">
                    <a
                      v-if="file.contentType.startsWith('image/')"
                      :href="fileUrl(file.id)"
                      :download="file.name"
                      target="_blank"
                      rel="noopener"
                      :aria-label="`Tải ảnh ${file.name}`"
                      class="block rounded-lg overflow-hidden focus-visible:ring-2 focus-visible:ring-sky-500"
                    >
                      <img :src="fileUrl(file.id)" :alt="file.name" class="max-w-xs max-h-64 rounded-lg border border-gray-200 object-contain" />
                    </a>
                    <a
                      v-else
                      :href="fileUrl(file.id)"
                      :download="file.name"
                      :aria-label="`Tải tệp ${file.name}`"
                      class="min-h-11 flex items-center gap-2 px-3 py-1.5 rounded-xl border border-gray-200 bg-white text-sm text-gray-700 hover:bg-gray-50 hover:border-gray-300 transition-colors focus-visible:ring-2 focus-visible:ring-sky-500"
                    >
                      <i class="pi pi-file text-base text-gray-500" aria-hidden="true"></i>
                      <span class="max-w-[160px] truncate font-medium">{{ file.name }}</span>
                      <span class="text-xs text-gray-400">{{ formatFileSize(file.size) }}</span>
                    </a>
                  </template>
                </div>
              </div>
            </section>

            <!-- Idle empty state -->
            <div v-else class="flex flex-col items-center justify-center py-16 text-center">
              <div class="w-20 h-20 rounded-2xl bg-gradient-to-br from-gray-100 to-gray-200 flex items-center justify-center mb-5 shadow-inner">
                <i class="pi pi-bolt text-3xl text-gray-300" aria-hidden="true"></i>
              </div>
              <h2 class="text-lg font-semibold text-gray-600 mb-1">Bắt đầu một phiên agent</h2>
              <p class="text-sm text-gray-400 max-w-sm">Nhập mục tiêu phía trên và nhấn “Chạy”. Agent sẽ hiển thị từng bước lập kế hoạch, gọi công cụ và tổng hợp kết quả tại đây.</p>
            </div>
          </template>
        </div>

        <!-- ============ RIGHT COLUMN: past runs ============ -->
        <aside aria-labelledby="runs-heading" class="self-start lg:sticky lg:top-24">
          <div class="flex items-center justify-between gap-2 mb-3">
            <h2 id="runs-heading" class="text-sm font-semibold text-gray-900">Lần chạy gần đây</h2>
            <Button
              type="button"
              label="Làm mới"
              icon="pi pi-refresh"
              text
              severity="secondary"
              :loading="runsLoading"
              @click="loadRuns"
              class="!min-h-11 !px-3 !rounded-xl !text-sm"
            />
          </div>

          <div v-if="runsError" role="alert" class="mb-3 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
            {{ runsError }}
          </div>

          <div v-if="runsLoading && runs.length === 0" role="status" class="flex flex-col items-center justify-center py-12 text-gray-500">
            <i class="pi pi-spin pi-spinner text-xl mb-2" aria-hidden="true"></i>
            <p class="text-sm">Đang tải...</p>
          </div>

          <div v-else-if="runs.length === 0" class="rounded-2xl border border-dashed border-gray-200 bg-white/50 px-4 py-10 text-center">
            <i class="pi pi-inbox text-2xl text-gray-300 mb-2 block" aria-hidden="true"></i>
            <p class="text-sm text-gray-400">Chưa có lần chạy nào.</p>
          </div>

          <ul v-else class="grid gap-2">
            <li v-for="run in runs" :key="run.id">
              <button
                type="button"
                @click="openRun(run.id)"
                :aria-current="selectedRunId === run.id ? 'true' : undefined"
                :aria-label="`Xem lần chạy: ${run.goal}`"
                class="w-full text-left rounded-xl border bg-white p-3 transition-colors hover:border-sky-300 hover:bg-sky-50/40 focus-visible:ring-2 focus-visible:ring-sky-500"
                :class="selectedRunId === run.id ? 'border-sky-300 bg-sky-50/60' : 'border-gray-200'"
              >
                <div class="flex items-start justify-between gap-2">
                  <span class="flex-1 min-w-0 text-sm font-medium text-gray-900 line-clamp-2">{{ run.goal }}</span>
                  <span
                    class="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-semibold border whitespace-nowrap flex-shrink-0"
                    :class="statusMeta(run.status).classes"
                  >{{ statusMeta(run.status).label }}</span>
                </div>
                <div class="mt-1.5 flex items-center gap-2 text-xs text-gray-500">
                  <span>{{ run.stepCount }} bước</span>
                  <span aria-hidden="true">·</span>
                  <time :datetime="run.createdAtUtc" :title="absoluteTime(run.createdAtUtc)">{{ relativeTime(run.createdAtUtc) }}</time>
                </div>
              </button>
            </li>
          </ul>
        </aside>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import { ChatSseParser } from '@/utils/chatSse';
import { parseProducedFile, type ProducedFile } from '@/composables/useChatStream';

/** A single ReAct tool step. `createdAtUtc` is present only on persisted (detail) steps. */
interface RunStep {
  ordinal: number;
  action: string;
  input: string;
  observation: string;
  createdAtUtc?: string;
}

/** Row shape from GET /api/agent-runs (newest first). */
interface AgentRunSummary {
  id: string;
  goal: string;
  status: string;
  stepCount: number;
  createdAtUtc: string;
  completedAtUtc: string | null;
}

/** Full run from GET /api/agent-runs/{id}. */
interface AgentRunDetail {
  id: string;
  goal: string;
  status: string;
  result: string | null;
  error: string | null;
  createdAtUtc: string;
  completedAtUtc: string | null;
  steps: RunStep[];
}

const MAX_GOAL = 4000;

// --- Launcher / live-run state -------------------------------------------
const goal = ref('');
const isStreaming = ref(false);
const currentRunId = ref('');
/** '' | 'Running' | 'Completed' | 'AwaitingApproval' | 'Failed' — drives the live status pill. */
const runStatus = ref('');
const statusLines = ref<string[]>([]);      // [THINKING] log
const steps = ref<RunStep[]>([]);           // live [STEP] timeline
const resultText = ref('');                 // final synthesized answer ([content] chunks)
const producedFiles = ref<ProducedFile[]>([]);
const approvalTaskId = ref('');
const runError = ref('');

// --- Past runs (right column) --------------------------------------------
const runs = ref<AgentRunSummary[]>([]);
const runsLoading = ref(false);
const runsError = ref('');

// --- Selected run detail (main column) -----------------------------------
const selectedRunId = ref('');
const detail = ref<AgentRunDetail | null>(null);
const detailLoading = ref(false);
const detailError = ref('');

const goalTooLong = computed(() => goal.value.length > MAX_GOAL);

const hasLiveActivity = computed(() =>
  isStreaming.value ||
  !!runStatus.value ||
  steps.value.length > 0 ||
  statusLines.value.length > 0 ||
  !!resultText.value ||
  producedFiles.value.length > 0 ||
  !!runError.value ||
  !!approvalTaskId.value
);

/**
 * Same-origin, cookie-authenticated URL for a produced file. SECURITY: the id
 * is always encoded as a single path segment so it can never break out of
 * `/api/files/` or inject query/path characters (matches ChatView).
 */
const fileUrl = (id: string): string => `/api/files/${encodeURIComponent(id)}`;

const formatFileSize = (bytes: number): string => {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
};

const absoluteTime = (value: string | null): string => {
  const date = new Date(value ?? NaN);
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleString('vi-VN');
};

const relativeTime = (value: string | null): string => {
  const time = new Date(value ?? NaN).getTime();
  if (Number.isNaN(time)) return '';
  const diffSec = Math.round((Date.now() - time) / 1000);
  if (diffSec < 45) return 'Vừa xong';
  const diffMin = Math.round(diffSec / 60);
  if (diffMin < 60) return `${diffMin} phút trước`;
  const diffHour = Math.round(diffMin / 60);
  if (diffHour < 24) return `${diffHour} giờ trước`;
  const diffDay = Math.round(diffHour / 24);
  if (diffDay < 30) return `${diffDay} ngày trước`;
  return absoluteTime(value);
};

const statusMeta = (status: string): { label: string; classes: string } => {
  switch (status) {
    case 'Running':
      return { label: 'Đang chạy', classes: 'bg-amber-50 text-amber-900 border-amber-200' };
    case 'Completed':
      return { label: 'Hoàn tất', classes: 'bg-emerald-50 text-emerald-900 border-emerald-200' };
    case 'AwaitingApproval':
      return { label: 'Chờ phê duyệt', classes: 'bg-sky-50 text-sky-900 border-sky-200' };
    case 'Failed':
      return { label: 'Thất bại', classes: 'bg-red-50 text-red-800 border-red-200' };
    default:
      return { label: status || 'Không rõ', classes: 'bg-gray-50 text-gray-600 border-gray-200' };
  }
};

const loadRuns = async (): Promise<void> => {
  runsLoading.value = runs.value.length === 0;
  runsError.value = '';
  try {
    const response = await http.get(ApiFactory.AGENT_RUNS.BASE);
    if (response.ok) runs.value = await response.json();
    else runsError.value = await readApiError(response, 'Không thể tải danh sách lần chạy');
  } catch (cause) {
    runsError.value = errorMessage(cause, 'Không thể tải danh sách lần chạy.');
  } finally {
    runsLoading.value = false;
  }
};

const openRun = async (id: string): Promise<void> => {
  selectedRunId.value = id;
  detail.value = null;
  detailError.value = '';
  detailLoading.value = true;
  try {
    const response = await http.get(ApiFactory.AGENT_RUNS.BY_ID(id));
    if (response.ok) detail.value = await response.json();
    else detailError.value = await readApiError(response, 'Không thể tải chi tiết lần chạy');
  } catch (cause) {
    detailError.value = errorMessage(cause, 'Không thể tải chi tiết lần chạy.');
  } finally {
    detailLoading.value = false;
  }
};

const closeDetail = (): void => {
  selectedRunId.value = '';
  detail.value = null;
  detailError.value = '';
  detailLoading.value = false;
};

/**
 * Abort seam for the in-flight run stream. Identical mechanism to ResearchView
 * / useChatStream: `http.post` exposes no `signal`, so aborting cancels the
 * active response-body reader, tearing the fetch down and letting the pending
 * `reader.read()` resolve with `done: true` (partial output is kept).
 */
let controller: AbortController | null = null;

const runGoal = async (): Promise<void> => {
  const trimmed = goal.value.trim();
  if (!trimmed || isStreaming.value || goalTooLong.value) return;

  // A fresh run drops any open detail and resets the live pane.
  closeDetail();
  runError.value = '';
  approvalTaskId.value = '';
  statusLines.value = [];
  steps.value = [];
  resultText.value = '';
  producedFiles.value = [];
  currentRunId.value = '';
  runStatus.value = 'Running';
  isStreaming.value = true;

  controller?.abort();
  const localController = new AbortController();
  controller = localController;

  try {
    const response = await http.post(ApiFactory.AGENT_RUNS.BASE, { goal: trimmed });
    if (!response.ok) throw new Error(await readApiError(response, 'Không thể bắt đầu phiên agent'));
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
          if (event.type === 'run-id') {
            // Parser decodes the "[AGENT_RUN:<id>]" marker for us.
            currentRunId.value = event.value;
            continue;
          }
          if (event.type === 'thinking') {
            // Parser already strips the "[THINKING]: " prefix.
            statusLines.value.push(event.value);
            continue;
          }
          if (event.type === 'step') {
            // "[STEP:{json}]" — guard against a malformed payload; skip on failure.
            try {
              const parsed = JSON.parse(event.value) as Partial<RunStep>;
              if (parsed && typeof parsed.ordinal === 'number') {
                steps.value.push({
                  ordinal: parsed.ordinal,
                  action: typeof parsed.action === 'string' ? parsed.action : '',
                  input: typeof parsed.input === 'string' ? parsed.input : '',
                  observation: typeof parsed.observation === 'string' ? parsed.observation : ''
                });
              }
            } catch {
              // Malformed step JSON — drop this one step, keep the stream alive.
            }
            continue;
          }
          if (event.type === 'file') {
            const file = parseProducedFile(event.value);
            if (file) producedFiles.value.push(file);
            continue;
          }
          if (event.type === 'approval') {
            // Backend paused for human approval; the run is no longer "running".
            approvalTaskId.value = event.value;
            runStatus.value = 'AwaitingApproval';
            finished = true;
            break;
          }
          if (event.type === 'content') {
            resultText.value += event.value;
            continue;
          }
          // web-search / saved / agent-log markers are irrelevant to agent mode — ignore.
        }
        if (done) break;
      }
      if (finished) await reader.cancel();
    } finally {
      localController.signal.removeEventListener('abort', onAbort);
    }
    // A natural end with no terminal marker means the run finished successfully.
    // After a user "Dừng" the run keeps going server-side, so leave the pill on
    // "Đang chạy" — the refreshed past-runs list carries the authoritative status.
    if (!localController.signal.aborted && runStatus.value === 'Running') {
      runStatus.value = 'Completed';
    }
  } catch (cause) {
    // A user-initiated stop is not an error; keep whatever streamed so far.
    if (!localController.signal.aborted) {
      runError.value = errorMessage(cause, 'Phiên agent thất bại.');
      runStatus.value = 'Failed';
    }
  } finally {
    if (controller === localController) controller = null;
    isStreaming.value = false;
    // Refresh the past-runs list so the new run appears with its final status.
    void loadRuns();
  }
};

const stopRun = (): void => {
  controller?.abort();
  controller = null;
};

onMounted(() => {
  void loadRuns();
});

onUnmounted(stopRun);
</script>

<style scoped>
/* Native disclosure marker replaced by the rotating chevron in the summary. */
summary {
  list-style: none;
}
summary::-webkit-details-marker {
  display: none;
}
details[open] > summary .agent-disclosure-icon {
  transform: rotate(90deg);
}
</style>
