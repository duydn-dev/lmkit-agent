<!--
  Panel "Tác vụ nền" — bản sao cách ChatGPT/Claude cho người dùng thấy việc đang chạy phía sau,
  thay vì bắt họ mở trang Agent Runs để đoán xem có gì đang chạy.

  Trước panel này, agent run chỉ tồn tại ở /agent-runs: người đang chat không biết có tác vụ nào
  đang chạy, không huỷ được, và không biết nó xong lúc nào. Với model local rất chậm, một tác vụ
  đang chạy mà không có hiện diện thị giác thì trải nghiệm giống như "đứng máy".

  Hai nhóm, đúng như Claude: ĐANG CHẠY (con quay, huỷ được) và ĐÃ XONG (gấp lại kèm số lượng, nút
  dọn). "Dọn" chỉ ẩn khỏi panel (localStorage) — không xoá dữ liệu trên server.
-->
<template>
  <aside
    class="fixed inset-y-0 right-0 z-40 w-[360px] max-w-[92vw] flex flex-col border-l border-gray-200 bg-white shadow-xl"
    aria-label="Tác vụ nền">
    <!-- Header -->
    <div class="h-12 shrink-0 flex items-center justify-between gap-2 px-3 border-b border-gray-100">
      <span class="text-sm font-semibold text-gray-900">Tác vụ nền</span>
      <span class="flex items-center gap-1">
        <button
          type="button"
          class="w-8 h-8 flex items-center justify-center rounded-md text-gray-500 hover:bg-gray-100 disabled:opacity-50"
          :disabled="isLoading"
          aria-label="Làm mới"
          title="Làm mới"
          @click="refresh()">
          <i class="pi pi-refresh text-sm" :class="{ 'animate-spin': isLoading }" aria-hidden="true"></i>
        </button>
        <button
          type="button"
          class="w-8 h-8 flex items-center justify-center rounded-md text-gray-500 hover:bg-gray-100"
          aria-label="Đóng panel tác vụ nền"
          title="Đóng"
          @click="emit('close')">
          <i class="pi pi-times text-sm" aria-hidden="true"></i>
        </button>
      </span>
    </div>

    <div class="min-h-0 grow overflow-y-auto px-3 py-3">
      <!-- Đang chạy -->
      <div v-if="activeTasks.length" class="flex flex-col gap-2">
        <div
          v-for="task in activeTasks"
          :key="task.id"
          class="rounded-xl border border-gray-200 bg-white p-3 shadow-sm">
          <div class="flex items-start gap-2">
            <i class="pi pi-spin pi-spinner mt-0.5 shrink-0 text-sm text-blue-500" aria-hidden="true"></i>
            <div class="min-w-0 grow">
              <button
                type="button"
                class="block w-full text-left text-sm font-medium text-gray-900 hover:text-blue-600 line-clamp-3"
                :title="task.goal"
                @click="openTask(task)">
                {{ task.goal }}
              </button>
              <div class="mt-1 flex items-center gap-2 text-xs text-gray-500">
                <span class="px-1.5 py-0.5 rounded border" :class="statusMeta(task.status).classes">
                  {{ statusMeta(task.status).label }}
                </span>
                <span aria-hidden="true">·</span>
                <span>{{ task.stepCount }} bước</span>
                <span aria-hidden="true">·</span>
                <span>{{ timeOf(task.createdAtUtc) }}</span>
              </div>
            </div>
            <button
              v-if="task.status === 'Running'"
              type="button"
              class="w-7 h-7 shrink-0 flex items-center justify-center rounded-md text-gray-400 hover:bg-red-50 hover:text-red-600 disabled:opacity-40"
              :disabled="busyIds.has(task.id)"
              aria-label="Dừng tác vụ"
              title="Dừng tác vụ này"
              @click="cancelTask(task)">
              <i class="pi pi-minus-circle text-sm" aria-hidden="true"></i>
            </button>
          </div>
        </div>
      </div>

      <!-- Đã xong: gấp lại kèm số lượng + nút dọn -->
      <div v-if="finishedTasks.length" class="mt-3">
        <div class="flex items-center justify-between gap-2">
          <button
            type="button"
            class="flex items-center gap-1.5 px-1 py-1 rounded-md text-sm font-medium text-gray-600 hover:bg-gray-50"
            :aria-expanded="finishedOpen"
            @click="finishedOpen = !finishedOpen">
            <i class="pi text-xs" :class="finishedOpen ? 'pi-chevron-down' : 'pi-chevron-right'" aria-hidden="true"></i>
            <span>Đã xong {{ finishedTasks.length }}</span>
          </button>
          <button
            type="button"
            class="w-7 h-7 flex items-center justify-center rounded-md text-gray-400 hover:bg-gray-100 hover:text-gray-600"
            aria-label="Xoá các tác vụ đã xong khỏi danh sách"
            title="Xoá khỏi danh sách"
            @click="clearFinished()">
            <i class="pi pi-trash text-sm" aria-hidden="true"></i>
          </button>
        </div>

        <div v-if="finishedOpen" class="mt-2 flex flex-col gap-1.5">
          <div
            v-for="task in finishedTasks"
            :key="task.id"
            class="flex items-start gap-2 px-2 py-2 rounded-lg hover:bg-gray-50">
            <i
              class="mt-0.5 shrink-0 text-sm"
              :class="task.status === 'Completed' || task.status === 'CompletedAfterApproval'
                ? 'pi pi-check-circle text-emerald-600'
                : 'pi pi-info-circle text-amber-500'"
              aria-hidden="true"></i>
            <div class="min-w-0 grow">
              <button
                type="button"
                class="block w-full text-left text-sm text-gray-700 hover:text-blue-600 line-clamp-2"
                :title="task.goal"
                @click="openTask(task)">
                {{ task.goal }}
              </button>
              <span class="text-xs text-gray-400">{{ timeOf(task.completedAtUtc) }}</span>
            </div>
          </div>
        </div>
      </div>

      <!-- Trống -->
      <div v-if="!activeTasks.length && !finishedTasks.length && !isLoading" class="h-full flex flex-col items-center justify-center gap-2 px-6 text-center">
        <i class="pi pi-inbox text-2xl text-gray-300" aria-hidden="true"></i>
        <p class="text-sm text-gray-500">
          Chưa có tác vụ nền nào. Các tác vụ chạy nền trong lúc bạn tiếp tục trò chuyện sẽ hiện ở đây.
        </p>
      </div>

      <p v-if="loadError" class="mt-2 text-xs text-red-600">{{ loadError }}</p>
    </div>
  </aside>
</template>

<script setup lang="ts">
/**
 * Danh sách tác vụ nền lấy từ GET /api/agent-runs (mới nhất trước) và tự làm mới theo nhịp, vì
 * trạng thái đổi ở SERVER: run do lịch chạy, run xong ở tab khác, run bị sweeper đóng vì hết hạn
 * duyệt — không có sự kiện nào để nghe ở client.
 */
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { useRouter } from 'vue-router';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { pagedUrl } from '@/api/paging';
import { errorMessage, readApiError } from '@/api/errors';
import { agentRunStatusMeta } from '@/views/agents/agentRunStatus';

interface AgentRunSummary {
  id: string;
  goal: string;
  status: string;
  stepCount: number;
  createdAtUtc: string;
  completedAtUtc: string | null;
}

/**
 * GET /api/agent-runs trả ENVELOPE có phân trang, không phải mảng trần:
 * <c>{ items, page, pageSize, totalCount, totalPages }</c>. Đọc thẳng response thành mảng thì
 * <c>tasks</c> thành object và mọi <c>.filter</c> sau đó ném lỗi ngay lúc render.
 */
interface PagedAgentRuns {
  items: AgentRunSummary[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

const emit = defineEmits<{
  (e: 'close'): void;
  /** Số tác vụ đang chạy — cha dùng để hiện badge trên nút mở panel. */
  (e: 'active-count', count: number): void;
}>();

const router = useRouter();

const tasks = ref<AgentRunSummary[]>([]);
const isLoading = ref(false);
const finishedOpen = ref(true);
const loadError = ref('');
const busyIds = ref(new Set<string>());

const POLL_MS = 5000;
const DISMISSED_KEY = 'chat.backgroundTasks.dismissed';
let timer: ReturnType<typeof setInterval> | null = null;

/** Đã dọn trong máy này; giữ qua reload để nút dọn không phải bấm lại mỗi lần mở panel. */
const dismissed = ref(new Set<string>(loadDismissed()));

const activeTasks = computed(() =>
  tasks.value.filter(task => task.status === 'Running' || task.status === 'AwaitingApproval')
);

const finishedTasks = computed(() =>
  tasks.value
    .filter(task => !activeTasks.value.includes(task) && !dismissed.value.has(task.id))
    .slice(0, 20)
);

const statusMeta = (status: string) => agentRunStatusMeta(status);

const timeOf = (iso: string | null): string => {
  if (!iso) return '';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleString('vi-VN', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
};

const refresh = async (): Promise<void> => {
  isLoading.value = true;
  try {
    // pageSize lớn: panel cần thấy mọi tác vụ gần đây trong một lần đọc, không phân trang UI.
    // http.get trả về Response THÔ (không phải JSON đã parse) — đây là hợp đồng của lớp http dùng
    // chung, và đọc nhầm nó thành object là cách nhanh nhất để panel im lặng mãi mãi ở trạng thái
    // rỗng trong khi API vẫn trả dữ liệu.
    const response = await http.get(pagedUrl(ApiFactory.AGENT_RUNS.BASE, 1, 50));
    if (!response.ok) {
      loadError.value = await readApiError(response, 'Không tải được danh sách tác vụ nền');
      return;
    }

    const page = (await response.json()) as PagedAgentRuns;
    tasks.value = page.items ?? [];
    loadError.value = '';
    emit('active-count', activeTasks.value.length);
  } catch (cause) {
    loadError.value = errorMessage(cause, 'Không tải được danh sách tác vụ nền.');
  } finally {
    isLoading.value = false;
  }
};

const openTask = (task: AgentRunSummary): void => {
  // Chi tiết (timeline + kết quả đầy đủ) nằm ở trang Agent Mode, không nhồi vào panel.
  // Route + tên tham số phải khớp router và AgentRunsView: đường dẫn là /agent-mode và nó đọc
  // query.runId — đi sai một trong hai thì bấm vào tác vụ sẽ ra trang trống.
  void router.push({ path: '/agent-mode', query: { runId: task.id } });
};

const cancelTask = async (task: AgentRunSummary): Promise<void> => {
  busyIds.value.add(task.id);
  try {
    const response = await http.post(ApiFactory.AGENT_RUNS.CANCEL(task.id), {});
    if (!response.ok) {
      loadError.value = await readApiError(response, 'Không dừng được tác vụ này');
      return;
    }

    await refresh();
  } catch (cause) {
    loadError.value = errorMessage(cause, 'Không dừng được tác vụ này.');
  } finally {
    busyIds.value.delete(task.id);
  }
};

const clearFinished = (): void => {
  for (const task of finishedTasks.value) dismissed.value.add(task.id);
  saveDismissed();
  // Set không phải thứ Vue theo dõi qua computed → thay tham chiếu để danh sách tính lại.
  tasks.value = [...tasks.value];
};

function loadDismissed(): string[] {
  try {
    const raw = localStorage.getItem(DISMISSED_KEY);
    return raw ? (JSON.parse(raw) as string[]) : [];
  } catch {
    return [];
  }
}

function saveDismissed(): void {
  try {
    // Trần 200 id: id tích tụ mãi thì localStorage phình vô ích.
    const ids = [...dismissed.value].slice(-200);
    dismissed.value = new Set(ids);
    localStorage.setItem(DISMISSED_KEY, JSON.stringify(ids));
  } catch {
    // localStorage bị chặn (chế độ riêng tư) — panel vẫn dùng được trong phiên hiện tại.
  }
}

onMounted(() => {
  void refresh();
  timer = setInterval(() => void refresh(), POLL_MS);
});

onBeforeUnmount(() => {
  if (timer) clearInterval(timer);
});
</script>
