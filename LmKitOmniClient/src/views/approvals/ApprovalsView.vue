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
              :class="row.done ? 'border-emerald-200 bg-emerald-50/50' : 'border-gray-100 bg-white'"
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
                  <pre v-if="row.details" class="mt-2 text-xs text-gray-800 bg-gray-50 border border-gray-200 rounded-lg p-3 max-h-40 overflow-auto whitespace-pre-wrap break-words">{{ row.details }}</pre>
                </div>

                <!-- Pending actions -->
                <div v-if="!row.done" class="flex items-center gap-2 flex-shrink-0">
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
                  v-else
                  class="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-full text-xs font-semibold bg-emerald-100 text-emerald-800 flex-shrink-0"
                >
                  <i class="pi pi-check-circle" aria-hidden="true"></i>Đã phê duyệt
                </span>
              </div>

              <!-- Approval result (expandable) -->
              <div v-if="row.done" class="mt-4">
                <template v-if="row.result">
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
import { onMounted, ref } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';

/** A pending approval as returned by GET /api/taskapproval/pending. */
interface PendingApproval {
  id: string;
  actionName: string;
  details?: string;
  createdAtUtc: string;
}

/** A pending approval plus the per-card UI state it needs. */
interface ApprovalRow extends PendingApproval {
  busyAction: 'approve' | 'reject' | null;
  error: string;
  done: boolean;
  result: string;
  resultExpanded: boolean;
}

const rows = ref<ApprovalRow[]>([]);
const loading = ref(false);
const topError = ref('');

const toRow = (item: PendingApproval): ApprovalRow => ({
  id: item.id,
  actionName: item.actionName,
  createdAtUtc: item.createdAtUtc,
  busyAction: null,
  error: '',
  done: false,
  result: '',
  resultExpanded: false
});

/**
 * Loads the pending queue.
 *  - reset=true  (mount / "Làm mới"): full reload, replaces the list and shows
 *    the top-level loading + error states.
 *  - reset=false (silent merge after an approval): folds in freshly pending
 *    items while KEEPING already-resolved cards so the user still sees their
 *    result. A failed silent refresh is ignored — the approval itself succeeded.
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
    const doneRows = rows.value.filter(row => row.done);
    const doneIds = new Set(doneRows.map(row => row.id));
    const freshRows = incoming.filter(item => !doneIds.has(item.id)).map(toRow);
    rows.value = [...doneRows, ...freshRows];
  } catch (cause) {
    if (reset) topError.value = errorMessage(cause, 'Không thể tải danh sách tác vụ chờ phê duyệt.');
  } finally {
    if (reset) loading.value = false;
  }
};

/** Status-specific fallback message; the server's own message wins when present. */
const fallbackForStatus = (status: number): string => {
  if (status === 404) return 'Không tìm thấy tác vụ.';
  if (status === 409) return 'Tác vụ không còn ở trạng thái chờ.';
  if (status === 500) return 'Thực thi tác vụ thất bại.';
  return 'Yêu cầu không thành công.';
};

const approve = async (row: ApprovalRow) => {
  if (row.busyAction) return;
  row.busyAction = 'approve';
  row.error = '';
  try {
    const response = await http.post(ApiFactory.TASK_APPROVAL.APPROVE(row.id));
    if (!response.ok) {
      row.error = await readApiError(response, fallbackForStatus(response.status));
      return;
    }
    const data = await response.json().catch(() => ({})) as { success?: boolean; result?: string };
    if (data.success === false) {
      row.error = (typeof data.result === 'string' && data.result.trim())
        ? data.result
        : 'Phê duyệt không thành công.';
      return;
    }
    row.result = typeof data.result === 'string' ? data.result : '';
    row.done = true;
    row.resultExpanded = true;
    await loadPending(false);
  } catch (cause) {
    row.error = errorMessage(cause, 'Không thể phê duyệt tác vụ.');
  } finally {
    row.busyAction = null;
  }
};

const reject = async (row: ApprovalRow) => {
  if (row.busyAction) return;
  row.busyAction = 'reject';
  row.error = '';
  try {
    // Backend expects the capitalized "Comment" field.
    const response = await http.post(ApiFactory.TASK_APPROVAL.REJECT(row.id), { Comment: 'User rejected' });
    if (!response.ok) {
      row.error = await readApiError(response, fallbackForStatus(response.status));
      return;
    }
    rows.value = rows.value.filter(item => item.id !== row.id);
  } catch (cause) {
    row.error = errorMessage(cause, 'Không thể từ chối tác vụ.');
  } finally {
    row.busyAction = null;
  }
};

const absoluteTime = (value: string): string => {
  const date = new Date(value);
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

onMounted(() => {
  void loadPending(true);
});
</script>
