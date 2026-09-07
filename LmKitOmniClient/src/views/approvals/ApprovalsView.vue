<template>
  <div class="flex-1 flex flex-col h-full bg-chatgpt-dark overflow-y-auto">
    <!-- Page Header -->
    <div class="sticky top-0 z-10 bg-chatgpt-dark/80 backdrop-blur-xl border-b border-gray-200/60">
      <div class="max-w-4xl mx-auto px-6 py-4">
        <div class="flex items-center justify-between gap-4">
          <div class="flex items-center gap-4">
            <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-emerald-500 to-green-600 flex items-center justify-center shadow-md shadow-emerald-500/20">
              <i class="pi pi-check-square text-white text-sm" aria-hidden="true"></i>
            </div>
            <div>
              <h1 class="text-xl font-bold text-gray-900 tracking-tight">Phê duyệt tác vụ</h1>
              <p class="text-xs text-gray-500">Các hành động của agent đang chờ bạn phê duyệt trước khi thực thi.</p>
            </div>
          </div>
          <Button
            label="Làm mới"
            icon="pi pi-refresh"
            severity="secondary"
            outlined
            :loading="loading"
            :disabled="loading || anyBusy"
            @click="loadPending(true)"
            class="!min-h-11 !px-4 !rounded-xl !text-sm flex-shrink-0"
          />
        </div>
      </div>
    </div>

    <!-- Main Content -->
    <div class="flex-1 max-w-4xl mx-auto w-full px-6 py-6">
      <div v-if="topError" role="alert" class="mb-5 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        {{ topError }}
      </div>

      <div v-if="loading && rows.length === 0" class="flex flex-col items-center justify-center py-16 text-gray-500" role="status">
        <i class="pi pi-spin pi-spinner text-2xl mb-3" aria-hidden="true"></i>
        <p class="text-sm">Đang tải danh sách tác vụ...</p>
      </div>

      <template v-else>
        <!-- Empty state (inbox zero) -->
        <div v-if="rows.length === 0 && !topError" class="flex flex-col items-center justify-center py-16 text-center">
          <div class="w-20 h-20 rounded-2xl bg-gradient-to-br from-gray-100 to-gray-200 flex items-center justify-center mb-5 shadow-inner">
            <i class="pi pi-inbox text-3xl text-gray-300" aria-hidden="true"></i>
          </div>
          <h2 class="text-lg font-semibold text-gray-600 mb-1">Không có tác vụ nào đang chờ phê duyệt.</h2>
          <p class="text-sm text-gray-400 max-w-xs">Khi agent cần bạn xác nhận trước khi thực thi một hành động, tác vụ sẽ xuất hiện ở đây.</p>
        </div>

        <!-- Pending list -->
        <section v-else-if="rows.length > 0" aria-label="Danh sách tác vụ chờ phê duyệt">
          <h2 class="sr-only">Danh sách tác vụ chờ phê duyệt</h2>
          <div class="grid gap-3">
            <article
              v-for="row in rows"
              :key="row.id"
              class="rounded-2xl border p-5 shadow-sm"
              :class="row.done ? 'border-emerald-200 bg-emerald-50/50' : row.closed ? 'border-gray-200 bg-gray-50' : 'border-gray-100 bg-white'"
            >
              <div class="flex flex-wrap items-start justify-between gap-3">
                <div class="min-w-0 flex-1 basis-64">
                  <h3 class="text-base font-semibold text-gray-900 break-words">{{ row.actionName }}</h3>
                  <p class="mt-1 text-xs text-gray-500 flex items-center gap-1.5 flex-wrap">
                    <i class="pi pi-clock" aria-hidden="true"></i>
                    <time :datetime="row.createdAtUtc" :title="absoluteTime(row.createdAtUtc)">{{ relativeTime(row.createdAtUtc) }}</time>
                    <span aria-hidden="true">·</span>
                    <span>{{ absoluteTime(row.createdAtUtc) }}</span>
                  </p>
                  <p
                    v-if="!row.done && !row.closed && expiryLabel(row.expiresAtUtc)"
                    class="mt-1 text-xs flex items-center gap-1.5"
                    :class="expiringSoon(row.expiresAtUtc) ? 'text-amber-700 font-medium' : 'text-gray-500'"
                  >
                    <i class="pi pi-hourglass" aria-hidden="true"></i>
                    <span>
                      {{ expiryLabel(row.expiresAtUtc) }}
                      <span class="text-gray-400">({{ absoluteTime(row.expiresAtUtc) }})</span>
                    </span>
                  </p>
                  <pre v-if="row.details" class="mt-2 text-xs text-gray-800 bg-gray-50 border border-gray-200 rounded-lg p-3 max-h-40 overflow-auto whitespace-pre-wrap break-words">{{ row.details }}</pre>
                </div>

                <!-- Pending actions -->
                <div v-if="!row.done && !row.closed" class="flex items-center gap-2 flex-shrink-0">
                  <Button
                    label="Phê duyệt"
                    icon="pi pi-check"
                    severity="success"
                    :loading="row.busyAction === 'approve'"
                    :disabled="row.busyAction !== null"
                    @click="approve(row)"
                    :aria-label="`Phê duyệt tác vụ ${row.actionName}`"
                    class="!min-h-11 !px-4 !rounded-xl !text-sm !font-medium"
                  />
                  <Button
                    label="Từ chối"
                    icon="pi pi-times"
                    severity="danger"
                    outlined
                    :loading="row.busyAction === 'reject'"
                    :disabled="row.busyAction !== null"
                    @click="reject(row)"
                    :aria-label="`Từ chối tác vụ ${row.actionName}`"
                    class="!min-h-11 !px-4 !rounded-xl !text-sm"
                  />
                </div>

                <!-- Resolved badge -->
                <span
                  v-else-if="row.done"
                  class="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-full text-xs font-semibold bg-emerald-100 text-emerald-800 flex-shrink-0"
                >
                  <i class="pi pi-check-circle" aria-hidden="true"></i>Đã phê duyệt
                </span>

                <!-- Closed: the approval can never be answered again -->
                <span
                  v-else
                  class="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-full text-xs font-semibold bg-gray-200 text-gray-700 flex-shrink-0"
                >
                  <i class="pi pi-ban" aria-hidden="true"></i>Không còn xử lý được
                </span>
              </div>

              <!-- Approval result (expandable) -->
              <div v-if="row.done" class="mt-4">
                <template v-if="row.result">
                  <div class="flex flex-wrap items-center gap-2">
                    <button
                      type="button"
                      :aria-expanded="row.resultExpanded"
                      :aria-controls="`approval-result-${row.id}`"
                      @click="row.resultExpanded = !row.resultExpanded"
                      class="inline-flex items-center gap-2 min-h-11 px-3 rounded-xl text-sm font-medium text-emerald-800 hover:bg-emerald-100/60 transition-colors"
                    >
                      <i :class="row.resultExpanded ? 'pi pi-chevron-down' : 'pi pi-chevron-right'" aria-hidden="true"></i>
                      {{ row.resultExpanded ? 'Ẩn kết quả' : 'Xem kết quả' }}
                    </button>
                    <button
                      type="button"
                      @click="copyResult(row)"
                      :aria-label="`Sao chép kết quả của tác vụ ${row.actionName}`"
                      class="inline-flex items-center gap-2 min-h-11 px-3 rounded-xl text-sm font-medium text-gray-700 hover:bg-gray-100 transition-colors"
                    >
                      <i :class="row.copied ? 'pi pi-check' : 'pi pi-copy'" aria-hidden="true"></i>
                      {{ row.copied ? 'Đã sao chép' : 'Sao chép kết quả' }}
                    </button>
                  </div>
                  <div
                    v-if="row.resultExpanded"
                    :id="`approval-result-${row.id}`"
                    class="mt-2 rounded-xl border border-emerald-200 bg-white px-4 py-3 text-sm text-gray-800 whitespace-pre-wrap break-words"
                  >
                    <span class="font-semibold text-gray-900">Kết quả: </span>{{ row.result }}
                  </div>
                </template>
                <p v-else class="text-sm text-emerald-800">
                  Tác vụ đã được phê duyệt và thực thi (không có kết quả trả về).
                </p>
              </div>

              <!--
                Where the result went. Approving here runs the tool but the API writes
                nothing back to the chat that asked for it, so the page continues the
                conversation itself — and says plainly when it could not.
              -->
              <div v-if="row.done" class="mt-3">
                <p v-if="row.continuation === 'running'" role="status" class="flex items-center gap-2 text-sm text-gray-600">
                  <i class="pi pi-spin pi-spinner" aria-hidden="true"></i>
                  Đang ghi kết quả vào cuộc trò chuyện gốc...
                </p>

                <div
                  v-else-if="row.continuation === 'written'"
                  class="rounded-xl border border-emerald-200 bg-white px-4 py-3 text-sm text-gray-800"
                >
                  <p class="flex items-start gap-2 text-emerald-900">
                    <i class="pi pi-comments mt-0.5" aria-hidden="true"></i>
                    <span>
                      Đã ghi kết quả vào cuộc trò chuyện<template v-if="row.sessionTitle"> “{{ row.sessionTitle }}”</template>
                      và agent đã trả lời tiếp.
                    </span>
                  </p>
                  <p v-if="row.followUpApprovalId" class="mt-2 text-amber-800">
                    Agent lại cần thêm một phê duyệt nữa — tác vụ mới sẽ xuất hiện trong danh sách phía trên.
                  </p>
                  <p v-if="row.continuationAnswer" class="mt-2 whitespace-pre-wrap break-words text-gray-700">
                    {{ row.continuationAnswer }}
                  </p>
                  <router-link
                    :to="{ path: '/chat', query: { id: row.sessionId } }"
                    class="mt-2 inline-flex items-center gap-1.5 min-h-11 font-semibold text-emerald-800 underline underline-offset-2 hover:text-emerald-900 focus-visible:ring-2 focus-visible:ring-emerald-500 rounded"
                  >
                    <i class="pi pi-arrow-right" aria-hidden="true"></i>Mở cuộc trò chuyện
                  </router-link>
                </div>

                <div
                  v-else-if="row.continuation === 'skipped'"
                  role="status"
                  class="rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900"
                >
                  <p class="flex items-start gap-2">
                    <i class="pi pi-exclamation-triangle mt-0.5" aria-hidden="true"></i>
                    <span>
                      Hành động đã chạy xong, nhưng kết quả <strong>chưa được ghi vào cuộc trò chuyện nào</strong>.
                      Không tìm thấy đoạn chat gốc — tác vụ có thể đến từ một phiên
                      <router-link to="/agent-mode" class="font-semibold underline underline-offset-2 hover:text-amber-950 focus-visible:ring-2 focus-visible:ring-amber-500 rounded">agent tự hành</router-link>
                      (lần chạy đó tự cập nhật trạng thái) hoặc từ một đoạn chat tạm thời không được lưu.
                      Hãy sao chép kết quả ở trên nếu bạn cần dùng lại.
                    </span>
                  </p>
                </div>

                <div
                  v-else-if="row.continuation === 'failed'"
                  role="alert"
                  class="rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900"
                >
                  <p class="flex items-start gap-2">
                    <i class="pi pi-exclamation-triangle mt-0.5" aria-hidden="true"></i>
                    <span>
                      Hành động đã chạy xong, nhưng <strong>chưa chắc kết quả đã được ghi đầy đủ</strong> vào cuộc trò chuyện:
                      {{ row.continuationError }}
                    </span>
                  </p>
                  <router-link
                    v-if="row.sessionId"
                    :to="{ path: '/chat', query: { id: row.sessionId } }"
                    class="mt-2 inline-flex items-center gap-1.5 min-h-11 font-semibold text-amber-900 underline underline-offset-2 hover:text-amber-950 focus-visible:ring-2 focus-visible:ring-amber-500 rounded"
                  >
                    <i class="pi pi-arrow-right" aria-hidden="true"></i>Mở cuộc trò chuyện để kiểm tra
                  </router-link>
                </div>
              </div>

              <!-- Per-item error -->
              <div v-if="row.error" role="alert" class="mt-3 rounded-xl border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
                {{ row.error }}
              </div>
            </article>
          </div>
        </section>
      </template>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import {
  resolveApprovalChatSession,
  streamApprovedContinuation,
  submitApprovalDecision
} from '@/composables/useTaskApproval';

/**
 * A pending approval as returned by GET /api/taskapproval/pending.
 *
 * The three `chatSession*`/`isChatSession` fields say where an approved result
 * belongs. They are optional so the page keeps working against an API that does not
 * project them yet — `resolveApprovalChatSession` then falls back to the content
 * search, which is what this page used to do unconditionally.
 */
interface PendingApproval {
  id: string;
  actionName: string;
  details?: string;
  createdAtUtc: string;
  expiresAtUtc?: string;
  chatSessionId?: string;
  isChatSession?: boolean;
  chatSessionTitle?: string;
}

/**
 * What happened to the approved result afterwards:
 *  - idle     nothing attempted yet (or the row is still pending),
 *  - running  the continuation turn is streaming,
 *  - written  the conversation now holds the result and the agent's reply,
 *  - skipped  no chat session owns this approval, so there was nothing to write to,
 *  - failed   the attempt broke; the user is told, never left assuming it landed.
 */
type ContinuationState = 'idle' | 'running' | 'written' | 'skipped' | 'failed';

/** A pending approval plus the per-card UI state it needs. */
interface ApprovalRow extends PendingApproval {
  busyAction: 'approve' | 'reject' | null;
  error: string;
  done: boolean;
  /** The approval can never be answered again (404 / 409 / 410 / failed execution). */
  closed: boolean;
  result: string;
  resultExpanded: boolean;
  copied: boolean;
  continuation: ContinuationState;
  continuationError: string;
  continuationAnswer: string;
  followUpApprovalId: string;
  sessionId: string;
  sessionTitle: string;
}

const rows = ref<ApprovalRow[]>([]);
const loading = ref(false);
const topError = ref('');

/** True while any card has a request in flight — "Làm mới" would discard its state. */
const anyBusy = computed(() =>
  rows.value.some(row => row.busyAction !== null || row.continuation === 'running')
);

const toRow = (item: PendingApproval): ApprovalRow => ({
  id: item.id,
  actionName: item.actionName,
  details: item.details,
  createdAtUtc: item.createdAtUtc,
  expiresAtUtc: item.expiresAtUtc,
  chatSessionId: item.chatSessionId,
  isChatSession: item.isChatSession,
  chatSessionTitle: item.chatSessionTitle,
  busyAction: null,
  error: '',
  done: false,
  closed: false,
  result: '',
  resultExpanded: false,
  copied: false,
  continuation: 'idle',
  continuationError: '',
  continuationAnswer: '',
  followUpApprovalId: '',
  sessionId: '',
  sessionTitle: ''
});

/**
 * Loads the pending queue.
 *  - reset=true  (mount / "Làm mới"): full reload, replaces the list and shows
 *    the top-level loading + error states.
 *  - reset=false (silent merge after a decision): folds in freshly pending items
 *    while KEEPING cards the user has already resolved — approved ones so their
 *    result and its continuation stay readable, closed ones so the explanation of
 *    why they cannot be answered does not vanish. A failed silent refresh is
 *    ignored — the decision itself succeeded.
 */
const loadPending = async (reset: boolean) => {
  if (reset) {
    loading.value = true;
    topError.value = '';
  }
  try {
    const response = await http.get(ApiFactory.TASK_APPROVAL.PENDING);
    if (!response.ok) {
      if (reset) topError.value = await readApiError(response, 'Không thể tải danh sách tác vụ chờ phê duyệt');
      return;
    }
    const data = await response.json().catch(() => []) as PendingApproval[];
    const incoming = Array.isArray(data) ? data : [];
    if (reset) {
      rows.value = incoming.map(toRow);
      return;
    }
    const settledRows = rows.value.filter(row => row.done || row.closed);
    const settledIds = new Set(settledRows.map(row => row.id));
    const freshRows = incoming.filter(item => !settledIds.has(item.id)).map(toRow);
    rows.value = [...settledRows, ...freshRows];
  } catch (cause) {
    if (reset) topError.value = errorMessage(cause, 'Không thể tải danh sách tác vụ chờ phê duyệt.');
  } finally {
    if (reset) loading.value = false;
  }
};

/**
 * Carries the approved tool output back into the conversation that asked for it.
 *
 * The approve endpoint executes the tool and hands the output to whoever called
 * it; it writes nothing to the chat session. The chat card closes that loop by
 * sending the output back as the next user turn, and this does exactly the same
 * from here — same prompt, same endpoint — so the transcript is identical no
 * matter which surface the user approved from. Every outcome other than "written"
 * is stated on the card rather than hidden behind the success badge.
 *
 * The session comes from the pending row itself; `resolveApprovalChatSession`
 * only searches when the API did not say.
 */
const writeResultToConversation = async (row: ApprovalRow): Promise<void> => {
  row.continuation = 'running';
  row.continuationError = '';
  row.continuationAnswer = '';
  row.followUpApprovalId = '';

  const session = await resolveApprovalChatSession(row);
  if (!session) {
    row.continuation = 'skipped';
    return;
  }

  row.sessionId = session.id;
  row.sessionTitle = session.title;

  const outcome = await streamApprovedContinuation(session.id, row.result);
  row.continuationAnswer = outcome.answer;
  row.followUpApprovalId = outcome.pendingApprovalId;
  if (outcome.ok) {
    row.continuation = 'written';
  } else {
    row.continuation = 'failed';
    row.continuationError = outcome.error;
  }
};

const approve = async (row: ApprovalRow) => {
  if (row.busyAction) return;
  row.busyAction = 'approve';
  row.error = '';
  try {
    const decision = await submitApprovalDecision(row.id, 'approve');
    if (decision.ok) {
      row.result = decision.result;
      row.done = true;
      row.resultExpanded = true;
    } else {
      row.error = decision.error;
      row.closed = decision.settled;
    }
  } finally {
    row.busyAction = null;
  }

  // Outside the decision's busy window: the tool has already run, so a failure
  // here must never read as a failed approval.
  if (row.done) await writeResultToConversation(row);
  await loadPending(false);
};

const reject = async (row: ApprovalRow) => {
  if (row.busyAction) return;
  row.busyAction = 'reject';
  row.error = '';
  let settled = false;
  try {
    const decision = await submitApprovalDecision(row.id, 'reject');
    if (decision.ok) {
      rows.value = rows.value.filter(item => item.id !== row.id);
    } else {
      row.error = decision.error;
      row.closed = decision.settled;
      settled = decision.settled;
    }
  } finally {
    row.busyAction = null;
  }
  // Somebody else decided (or it lapsed): the rest of the queue may be stale too.
  if (settled) await loadPending(false);
};

const copyResult = async (row: ApprovalRow) => {
  try {
    await navigator.clipboard.writeText(row.result);
    row.copied = true;
    window.setTimeout(() => { row.copied = false; }, 2000);
  } catch {
    // Clipboard access can be refused (insecure origin, denied permission); the
    // result is already on screen and selectable, so this is not worth an alert.
    row.copied = false;
  }
};

const absoluteTime = (value: string | undefined): string => {
  const date = new Date(value ?? NaN);
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleString('vi-VN');
};

const relativeTime = (value: string): string => {
  const time = new Date(value).getTime();
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

/** Minutes left before an approval stops being answerable; null when unknown. */
const minutesLeft = (value: string | undefined): number | null => {
  const time = new Date(value ?? NaN).getTime();
  if (Number.isNaN(time)) return null;
  return Math.round((time - Date.now()) / 60000);
};

/**
 * How long the approval can still be answered. Shown because the window is real:
 * past it the approve endpoint answers 410 and nothing executes, and an approval
 * a day old is one the approver can no longer place in context.
 */
const expiryLabel = (value: string | undefined): string => {
  const minutes = minutesLeft(value);
  if (minutes === null) return '';
  if (minutes <= 0) return 'Đã hết hạn phê duyệt';
  if (minutes < 60) return `Còn ${minutes} phút để phê duyệt`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `Còn ${hours} giờ để phê duyệt`;
  return `Còn ${Math.round(hours / 24)} ngày để phê duyệt`;
};

const expiringSoon = (value: string | undefined): boolean => {
  const minutes = minutesLeft(value);
  return minutes !== null && minutes < 60;
};

onMounted(() => {
  void loadPending(true);
});
</script>
