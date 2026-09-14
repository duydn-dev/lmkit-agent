<template>
  <div class="flex-1 overflow-y-auto bg-gray-50 p-4 md:p-6">
    <div class="max-w-6xl mx-auto">
      <header class="mb-4 flex items-center gap-4">
        <div class="w-10 h-10 rounded-lg bg-[--color-gov-red] flex items-center justify-center shadow-md flex-shrink-0">
          <i class="pi pi-calendar-clock text-white text-sm" aria-hidden="true"></i>
        </div>
        <div class="flex-1">
          <h1 class="text-xl font-bold text-gray-900 tracking-tight">Task Scheduler</h1>
          <p class="text-sm text-gray-500">
            Chạy prompt tự động theo chu kỳ/ngày/tuần. Chế độ <strong>Automation Agent</strong> cho phép lịch dùng tool
            (query CSDL đã index, tri thức, web…) và trả kết quả đã định dạng qua thông báo.
          </p>
        </div>
      </header>

      <div v-if="list.error.value" role="alert" class="mb-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        {{ list.error.value }}
      </div>

      <div class="mb-3 flex flex-wrap items-center justify-between gap-3">
        <span class="relative">
          <i class="pi pi-search absolute left-3 top-1/2 -translate-y-1/2 text-gray-400 text-xs" aria-hidden="true"></i>
          <InputText v-model="list.search.value" placeholder="Tìm theo tên, prompt…" class="!pl-8 w-72 max-w-full" aria-label="Tìm kiếm lịch" @input="list.onSearchInput" />
        </span>
        <div class="flex items-center gap-2">
          <Button icon="pi pi-refresh" severity="secondary" outlined :loading="list.loading.value" aria-label="Tải lại" @click="list.reload" />
          <Button label="Tạo lịch" icon="pi pi-plus" @click="openCreateForm" />
        </div>
      </div>

      <DataTable
        :value="list.rows.value"
        lazy
        paginator
        :rows="list.pageSize.value"
        :first="list.first.value"
        :totalRecords="list.totalRecords.value"
        :loading="list.loading.value"
        :rowsPerPageOptions="[10, 20, 50]"
        dataKey="id"
        class="bg-white rounded-lg border border-gray-200 overflow-hidden"
        @page="list.onPage">
        <template #empty>
          <div class="p-10 text-center text-gray-500 text-sm">
            <i class="pi pi-calendar-clock text-3xl text-gray-300 block mb-3" aria-hidden="true"></i>
            {{ list.search.value ? 'Không tìm thấy lịch phù hợp.' : 'Chưa có lịch tác vụ nào — tạo lịch để trợ lý chạy prompt định kỳ.' }}
          </div>
        </template>

        <Column field="name" header="Tên lịch" style="min-width: 14rem">
          <template #body="{ data }">
            <div class="font-semibold text-gray-900 text-sm">{{ data.name }}</div>
            <div class="text-xs text-gray-400 mt-0.5 max-w-72 truncate" :title="data.prompt">{{ data.prompt }}</div>
          </template>
        </Column>
        <Column field="runMode" header="Chế độ" style="min-width: 9rem">
          <template #body="{ data }">
            <Tag :value="data.runMode === 'agent' ? 'Automation Agent' : 'Completion'" :severity="data.runMode === 'agent' ? 'danger' : 'secondary'" />
          </template>
        </Column>
        <Column header="Chu kỳ" style="min-width: 11rem">
          <template #body="{ data }"><span class="text-sm text-gray-700">{{ scheduleSummary(data) }}</span></template>
        </Column>
        <Column header="Trạng thái" style="min-width: 10rem">
          <template #body="{ data }">
            <Tag v-if="data.lastStatus" :value="statusLabel(data.lastStatus)" :severity="statusSeverity(data.lastStatus)" :title="data.lastError ?? undefined" />
            <span v-else class="text-xs text-gray-400">Chưa chạy</span>
            <p v-if="data.lastStatus === 'Failed' && data.lastError" class="text-[11px] text-red-600 mt-1 max-w-56 truncate" :title="data.lastError">{{ data.lastError }}</p>
          </template>
        </Column>
        <Column header="Chạy kế tiếp / cuối" style="min-width: 12rem">
          <template #body="{ data }">
            <div class="text-xs text-gray-600 tabular-nums">Kế tiếp: {{ formatUtcDate(data.nextRunUtc) }}</div>
            <div class="text-xs text-gray-400 tabular-nums">Cuối: {{ formatUtcDate(data.lastRunUtc) }}</div>
          </template>
        </Column>
        <Column header="Bật" style="width: 5rem">
          <template #body="{ data }">
            <ToggleSwitch
              :modelValue="data.enabled"
              :inputId="`schedule-enabled-${data.id}`"
              :aria-label="`Bật tắt lịch ${data.name}`"
              :disabled="togglingId === data.id"
              @update:model-value="toggleSchedule(data)" />
          </template>
        </Column>
        <Column header="Thao tác" style="width: 7rem">
          <template #body="{ data }">
            <div class="flex items-center gap-1">
              <Button icon="pi pi-pencil" text rounded severity="secondary" :aria-label="`Chỉnh sửa lịch ${data.name}`" @click="openEditForm(data)" />
              <Button icon="pi pi-trash" text rounded severity="danger" :loading="deletingId === data.id" :aria-label="`Xóa lịch ${data.name}`" @click="confirmDelete(data)" />
            </div>
          </template>
        </Column>
      </DataTable>

      <!-- Create / Edit Dialog -->
      <Dialog v-model:visible="showForm" modal :header="editingId ? 'Chỉnh sửa lịch tác vụ' : 'Tạo lịch tác vụ'" class="w-[36rem] max-w-[95vw]">
        <form @submit.prevent="saveSchedule" class="grid gap-4 pt-1">
          <div v-if="formError" role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
            {{ formError }}
          </div>

          <p class="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
            <i class="pi pi-info-circle mr-1" aria-hidden="true"></i>Tối đa 10 lịch đang bật cho mỗi người dùng.
          </p>

          <div class="grid gap-1">
            <label for="schedule-name" class="text-sm font-medium text-gray-700">Tên lịch <span class="text-red-500">*</span></label>
            <InputText id="schedule-name" v-model="form.name" required maxlength="100" placeholder="Ví dụ: Báo cáo dữ liệu quan trắc buổi sáng" />
          </div>

          <div class="grid gap-1">
            <label for="schedule-prompt" class="text-sm font-medium text-gray-700">Prompt <span class="text-red-500">*</span></label>
            <Textarea id="schedule-prompt" v-model="form.prompt" rows="4" required maxlength="2000" placeholder="Nội dung yêu cầu trợ lý thực hiện mỗi lần chạy…" class="w-full" />
          </div>

          <div class="grid gap-1">
            <label for="schedule-runmode" class="text-sm font-medium text-gray-700">Chế độ chạy</label>
            <Select v-model="form.runMode" :options="runModeOptions" optionLabel="label" optionValue="value" inputId="schedule-runmode" class="w-full" />
            <p class="text-xs text-gray-500">
              <template v-if="form.runMode === 'agent'">
                Automation Agent: lịch chạy qua pipeline agent đầy đủ — truy vấn CSDL đã index, tri thức, web…; từng bước được lưu
                trong Automation Agent. Bước cần phê duyệt sẽ tạm dừng chờ bạn duyệt (HITL).
              </template>
              <template v-else>
                Completion: một lượt suy luận thuần, không dùng tool — nhanh và nhẹ, phù hợp nhắc việc/tóm tắt.
              </template>
            </p>
          </div>

          <div class="grid gap-1">
            <label for="schedule-kind" class="text-sm font-medium text-gray-700">Chu kỳ chạy</label>
            <Select v-model="form.scheduleKind" :options="kindOptions" optionLabel="label" optionValue="value" inputId="schedule-kind" class="w-full" />
          </div>

          <div v-if="form.scheduleKind === 'interval'" class="grid gap-1">
            <label for="schedule-interval" class="text-sm font-medium text-gray-700">Chạy mỗi (phút)</label>
            <InputNumber v-model="form.intervalMinutes" inputId="schedule-interval" :min="15" :useGrouping="false" showButtons suffix=" phút" class="w-full" />
            <p class="text-xs text-gray-400">Tối thiểu 15 phút.</p>
          </div>

          <div v-if="form.scheduleKind === 'weekly'" class="grid gap-1">
            <label for="schedule-day" class="text-sm font-medium text-gray-700">Ngày trong tuần</label>
            <Select v-model="form.dayOfWeek" :options="dayOptions" optionLabel="label" optionValue="value" inputId="schedule-day" class="w-full" />
          </div>

          <div v-if="form.scheduleKind !== 'interval'" class="grid gap-1">
            <label for="schedule-time" class="text-sm font-medium text-gray-700">Giờ chạy (UTC)</label>
            <input
              id="schedule-time"
              v-model="form.timeOfDay"
              type="time"
              required
              class="min-h-11 rounded-md border border-gray-300 bg-white px-3 text-sm text-gray-900 focus:border-[--color-gov-red]" />
            <p class="text-xs text-gray-400">Giờ tính theo UTC (giờ Việt Nam = UTC + 7).</p>
          </div>
        </form>
        <template #footer>
          <Button label="Hủy" severity="secondary" outlined :disabled="saving" @click="showForm = false" />
          <Button :label="editingId ? 'Lưu thay đổi' : 'Tạo lịch'" icon="pi pi-check" :loading="saving" @click="saveSchedule" />
        </template>
      </Dialog>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useConfirm } from 'primevue/useconfirm';
import { useToast } from 'primevue/usetoast';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import { useServerPage } from '@/composables/useServerPage';

type ScheduleKind = 'interval' | 'daily' | 'weekly';
type RunMode = 'completion' | 'agent';

interface Schedule {
  id: string;
  name: string;
  prompt: string;
  runMode: RunMode;
  scheduleKind: ScheduleKind;
  intervalMinutes: number | null;
  timeOfDayMinutes: number | null;
  dayOfWeek: number | null;
  enabled: boolean;
  nextRunUtc: string | null;
  lastRunUtc: string | null;
  lastStatus: string | null;
  lastError: string | null;
}

interface ScheduleForm {
  name: string;
  prompt: string;
  runMode: RunMode;
  scheduleKind: ScheduleKind;
  intervalMinutes: number | null;
  /** "HH:mm" from the native time input; converted to minutes on save. */
  timeOfDay: string;
  dayOfWeek: number;
}

const confirm = useConfirm();
const toast = useToast();
const list = useServerPage<Schedule>(ApiFactory.SCHEDULES.BASE, { errorLabel: 'lịch tác vụ' });

const kindOptions = [
  { label: 'Theo chu kỳ (phút)', value: 'interval' },
  { label: 'Hàng ngày', value: 'daily' },
  { label: 'Hàng tuần', value: 'weekly' }
];
const runModeOptions = [
  { label: 'Completion — một lượt, không tool', value: 'completion' },
  { label: 'Automation Agent — đủ tool (CSDL, tri thức, web…)', value: 'agent' }
];

const DAY_SHORT = ['CN', 'T2', 'T3', 'T4', 'T5', 'T6', 'T7'];
const DAY_FULL = ['Chủ nhật', 'Thứ 2', 'Thứ 3', 'Thứ 4', 'Thứ 5', 'Thứ 6', 'Thứ 7'];
const dayOptions = DAY_SHORT.map((label, value) => ({ label, value }));

const togglingId = ref<string | null>(null);
const deletingId = ref<string | null>(null);

const showForm = ref(false);
const editingId = ref<string | null>(null);
const editingEnabled = ref(true);
const saving = ref(false);
const formError = ref('');

const emptyForm = (): ScheduleForm => ({
  name: '',
  prompt: '',
  runMode: 'completion',
  scheduleKind: 'interval',
  intervalMinutes: 60,
  timeOfDay: '08:00',
  dayOfWeek: 1
});
const form = ref<ScheduleForm>(emptyForm());

// --- Formatting helpers -----------------------------------------------------

const formatUtcTime = (minutes: number | null): string => {
  const total = minutes ?? 0;
  const hh = String(Math.floor(total / 60)).padStart(2, '0');
  const mm = String(total % 60).padStart(2, '0');
  return `${hh}:${mm}`;
};

const scheduleSummary = (schedule: Schedule): string => {
  if (schedule.scheduleKind === 'interval') return `Mỗi ${schedule.intervalMinutes ?? 0} phút`;
  if (schedule.scheduleKind === 'daily') return `Hàng ngày ${formatUtcTime(schedule.timeOfDayMinutes)} UTC`;
  const day = DAY_FULL[schedule.dayOfWeek ?? 0] ?? 'Chủ nhật';
  return `${day} ${formatUtcTime(schedule.timeOfDayMinutes)} UTC`;
};

const formatUtcDate = (iso: string | null): string => {
  if (!iso) return '—';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleString('vi-VN', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit' });
};

// Trạng thái backend ghi: Succeeded | Failed | Skipped | AwaitingApproval.
const statusLabel = (status: string): string => {
  switch (status) {
    case 'Succeeded': return 'Thành công';
    case 'Failed': return 'Thất bại';
    case 'Skipped': return 'Bỏ lượt (bận/thiếu model)';
    case 'AwaitingApproval': return 'Chờ phê duyệt';
    default: return status;
  }
};
const statusSeverity = (status: string): string => {
  switch (status) {
    case 'Succeeded': return 'success';
    case 'Failed': return 'danger';
    case 'AwaitingApproval': return 'warn';
    default: return 'secondary';
  }
};

// --- CRUD -------------------------------------------------------------------

const openCreateForm = () => {
  editingId.value = null;
  editingEnabled.value = true;
  form.value = emptyForm();
  formError.value = '';
  showForm.value = true;
};

const openEditForm = (schedule: Schedule) => {
  editingId.value = schedule.id;
  editingEnabled.value = schedule.enabled;
  form.value = {
    name: schedule.name,
    prompt: schedule.prompt,
    runMode: schedule.runMode === 'agent' ? 'agent' : 'completion',
    scheduleKind: schedule.scheduleKind,
    intervalMinutes: schedule.intervalMinutes ?? 60,
    timeOfDay: formatUtcTime(schedule.timeOfDayMinutes ?? 480),
    dayOfWeek: schedule.dayOfWeek ?? 1
  };
  formError.value = '';
  showForm.value = true;
};

const parseTimeOfDay = (value: string): number | null => {
  const match = value.match(/^(\d{1,2}):(\d{2})$/);
  if (!match) return null;
  const hours = Number(match[1]);
  const minutes = Number(match[2]);
  if (hours > 23 || minutes > 59) return null;
  return hours * 60 + minutes;
};

const saveSchedule = async () => {
  const name = form.value.name.trim();
  const prompt = form.value.prompt.trim();
  if (!name) { formError.value = 'Vui lòng nhập tên lịch.'; return; }
  if (!prompt) { formError.value = 'Vui lòng nhập prompt.'; return; }

  const kind = form.value.scheduleKind;
  let intervalMinutes: number | null = null;
  let timeOfDayMinutes: number | null = null;
  let dayOfWeek: number | null = null;

  if (kind === 'interval') {
    intervalMinutes = form.value.intervalMinutes;
    if (intervalMinutes === null || intervalMinutes < 15) {
      formError.value = 'Chu kỳ tối thiểu là 15 phút.';
      return;
    }
  } else {
    timeOfDayMinutes = parseTimeOfDay(form.value.timeOfDay);
    if (timeOfDayMinutes === null) {
      formError.value = 'Vui lòng chọn giờ chạy hợp lệ.';
      return;
    }
    if (kind === 'weekly') dayOfWeek = form.value.dayOfWeek;
  }

  formError.value = '';
  saving.value = true;
  const payload = {
    name,
    prompt,
    runMode: form.value.runMode,
    scheduleKind: kind,
    intervalMinutes,
    timeOfDayMinutes,
    dayOfWeek,
    // New schedules start enabled; edits preserve the current switch state
    // (the dedicated toggle endpoint owns on/off changes).
    enabled: editingId.value ? editingEnabled.value : true
  };

  try {
    const response = editingId.value
      ? await http.put(ApiFactory.SCHEDULES.BY_ID(editingId.value), payload)
      : await http.post(ApiFactory.SCHEDULES.BASE, payload);
    if (!response.ok) {
      formError.value = await readApiError(response, 'Không thể lưu lịch tác vụ');
      return;
    }
    showForm.value = false;
    toast.add({ severity: 'success', summary: editingId.value ? 'Đã cập nhật lịch' : 'Đã tạo lịch', detail: name, life: 3000 });
    await list.reload();
  } catch (cause) {
    formError.value = errorMessage(cause, 'Không thể lưu lịch tác vụ.');
  } finally {
    saving.value = false;
  }
};

const toggleSchedule = async (schedule: Schedule) => {
  if (togglingId.value) return;
  togglingId.value = schedule.id;
  try {
    const response = await http.post(ApiFactory.SCHEDULES.TOGGLE(schedule.id));
    if (response.ok) await list.reload();
    else toast.add({ severity: 'error', summary: 'Không thể bật/tắt lịch', detail: await readApiError(response, 'Không thể bật/tắt lịch'), life: 6000 });
  } catch (cause) {
    toast.add({ severity: 'error', summary: 'Không thể bật/tắt lịch', detail: errorMessage(cause, 'Không thể bật/tắt lịch.'), life: 6000 });
  } finally {
    togglingId.value = null;
  }
};

const confirmDelete = (schedule: Schedule) => {
  confirm.require({
    header: 'Xóa lịch tác vụ',
    message: `Xóa lịch "${schedule.name}"? Hành động này không thể hoàn tác.`,
    icon: 'pi pi-exclamation-triangle',
    acceptLabel: 'Xóa',
    rejectLabel: 'Hủy',
    acceptProps: { severity: 'danger' },
    rejectProps: { severity: 'secondary', outlined: true },
    accept: () => { void performDelete(schedule); }
  });
};

const performDelete = async (schedule: Schedule) => {
  deletingId.value = schedule.id;
  try {
    const response = await http.delete(ApiFactory.SCHEDULES.BY_ID(schedule.id));
    if (response.ok) {
      toast.add({ severity: 'success', summary: 'Đã xóa lịch', detail: schedule.name, life: 3000 });
      await list.reload();
    } else {
      toast.add({ severity: 'error', summary: 'Không thể xóa lịch', detail: await readApiError(response, 'Không thể xóa lịch'), life: 6000 });
    }
  } catch (cause) {
    toast.add({ severity: 'error', summary: 'Không thể xóa lịch', detail: errorMessage(cause, 'Không thể xóa lịch.'), life: 6000 });
  } finally {
    deletingId.value = null;
  }
};

onMounted(() => { void list.load(); });
</script>
