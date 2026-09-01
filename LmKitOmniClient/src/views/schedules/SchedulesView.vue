<template>
  <div class="flex-1 flex flex-col h-full bg-chatgpt-dark overflow-y-auto">
    <!-- Page Header -->
    <div class="sticky top-0 z-10 bg-chatgpt-dark/80 backdrop-blur-xl border-b border-gray-200/60">
      <div class="max-w-5xl mx-auto px-6 py-4">
        <div class="flex items-center justify-between gap-4">
          <div class="flex items-center gap-4">
            <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-amber-500 to-orange-600 flex items-center justify-center shadow-md shadow-amber-500/20">
              <i class="pi pi-calendar-clock text-white text-sm"></i>
            </div>
            <div>
              <h1 class="text-xl font-bold text-gray-900 tracking-tight">Lịch tác vụ</h1>
              <p class="text-xs text-gray-500">Chạy prompt tự động theo chu kỳ, hàng ngày hoặc hàng tuần</p>
            </div>
          </div>
          <Button
            @click="openCreateForm"
            label="Tạo lịch"
            icon="pi pi-plus"
            class="!min-h-11 !px-4 !py-2.5 !rounded-xl !text-sm !font-medium !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800"
          />
        </div>
      </div>
    </div>

    <!-- Main Content -->
    <div class="flex-1 max-w-5xl mx-auto w-full px-6 py-6">
      <div v-if="pageError" role="alert" class="mb-5 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        {{ pageError }}
      </div>

      <div v-if="loading" class="flex flex-col items-center justify-center py-20 text-gray-500" role="status">
        <i class="pi pi-spin pi-spinner text-2xl mb-3" aria-hidden="true"></i>
        <p class="text-sm">Đang tải lịch tác vụ...</p>
      </div>

      <div v-else-if="schedules.length === 0" class="flex flex-col items-center justify-center py-20 text-center">
        <div class="w-20 h-20 rounded-2xl bg-gradient-to-br from-gray-100 to-gray-200 flex items-center justify-center mb-5 shadow-inner">
          <i class="pi pi-calendar-clock text-3xl text-gray-300" aria-hidden="true"></i>
        </div>
        <h3 class="text-lg font-semibold text-gray-600 mb-1">Chưa có lịch tác vụ nào</h3>
        <p class="text-sm text-gray-400 max-w-xs mb-4">Tạo lịch để trợ lý tự động chạy prompt định kỳ và gửi thông báo kết quả.</p>
        <Button label="Tạo lịch" icon="pi pi-plus" @click="openCreateForm" class="!min-h-11 !px-4 !rounded-xl !text-sm !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800" />
      </div>

      <div v-else class="grid gap-3">
        <div v-for="schedule in schedules" :key="schedule.id" class="bg-white rounded-2xl border border-gray-100 shadow-sm hover:shadow-md hover:border-gray-200 transition-all duration-300 p-5">
          <div class="flex items-start justify-between gap-4">
            <div class="min-w-0 flex-1">
              <div class="flex flex-wrap items-center gap-2 mb-1">
                <h2 class="text-sm font-semibold text-gray-900 truncate">{{ schedule.name }}</h2>
                <span v-if="schedule.lastStatus" class="inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-[11px] font-semibold border" :class="statusChipClass(schedule.lastStatus)" :title="schedule.lastStatus === 'Failed' ? (schedule.lastError ?? '') : undefined">
                  <span class="w-1.5 h-1.5 rounded-full" :class="statusDotClass(schedule.lastStatus)" aria-hidden="true"></span>
                  {{ statusLabel(schedule.lastStatus) }}
                </span>
              </div>
              <p class="text-xs font-medium text-gray-600">{{ scheduleSummary(schedule) }}</p>
              <p class="text-xs text-gray-400 mt-1 line-clamp-2">{{ schedule.prompt }}</p>
              <div class="flex flex-wrap gap-x-4 gap-y-1 mt-2 text-[11px] text-gray-400">
                <span>Chạy kế tiếp: {{ formatUtcDate(schedule.nextRunUtc) }}</span>
                <span>Lần chạy cuối: {{ formatUtcDate(schedule.lastRunUtc) }}</span>
              </div>
              <p v-if="schedule.lastStatus === 'Failed' && schedule.lastError" class="mt-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
                {{ schedule.lastError }}
              </p>
            </div>

            <div class="flex items-center gap-2 flex-shrink-0">
              <ToggleSwitch
                :modelValue="schedule.enabled"
                :inputId="`schedule-enabled-${schedule.id}`"
                :aria-label="`Bật tắt lịch ${schedule.name}`"
                :disabled="togglingId === schedule.id"
                @update:modelValue="toggleSchedule(schedule)"
              />
              <Button
                icon="pi pi-pencil"
                severity="secondary"
                text
                @click="openEditForm(schedule)"
                :aria-label="`Chỉnh sửa lịch ${schedule.name}`"
                class="!w-11 !h-11 !rounded-xl"
              />
              <Button
                icon="pi pi-trash"
                severity="danger"
                text
                :disabled="deletingId === schedule.id"
                @click="deleteSchedule(schedule)"
                :aria-label="`Xóa lịch ${schedule.name}`"
                class="!w-11 !h-11 !rounded-xl"
              />
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Create / Edit Dialog -->
    <Dialog
      v-model:visible="showForm"
      modal
      :header="editingId ? 'Chỉnh sửa lịch tác vụ' : 'Tạo lịch tác vụ'"
      :style="{ width: '520px' }"
      :breakpoints="{ '575px': '90vw' }"
    >
      <form @submit.prevent="saveSchedule" class="grid gap-4 pt-1">
        <div v-if="formError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
          {{ formError }}
        </div>

        <p class="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
          <i class="pi pi-info-circle mr-1" aria-hidden="true"></i>Tối đa 10 lịch đang bật.
        </p>

        <div class="grid gap-1">
          <label for="schedule-name" class="text-sm font-medium text-gray-700">Tên lịch</label>
          <InputText id="schedule-name" v-model="form.name" required maxlength="100" placeholder="Ví dụ: Tổng hợp tin tức buổi sáng" />
        </div>

        <div class="grid gap-1">
          <label for="schedule-prompt" class="text-sm font-medium text-gray-700">Prompt <span class="text-red-600" aria-hidden="true">*</span></label>
          <Textarea id="schedule-prompt" v-model="form.prompt" rows="4" required placeholder="Nội dung yêu cầu trợ lý thực hiện mỗi lần chạy..." class="w-full" />
        </div>

        <div class="grid gap-1">
          <label for="schedule-kind" class="text-sm font-medium text-gray-700">Chu kỳ chạy</label>
          <Select
            v-model="form.scheduleKind"
            :options="kindOptions"
            optionLabel="label"
            optionValue="value"
            inputId="schedule-kind"
            class="w-full"
          />
        </div>

        <div v-if="form.scheduleKind === 'interval'" class="grid gap-1">
          <label for="schedule-interval" class="text-sm font-medium text-gray-700">Chạy mỗi (phút)</label>
          <InputNumber v-model="form.intervalMinutes" inputId="schedule-interval" :min="15" :useGrouping="false" showButtons suffix=" phút" class="w-full" />
          <p class="text-xs text-gray-400">Tối thiểu 15 phút.</p>
        </div>

        <div v-if="form.scheduleKind === 'weekly'" class="grid gap-1">
          <label for="schedule-day" class="text-sm font-medium text-gray-700">Ngày trong tuần</label>
          <Select
            v-model="form.dayOfWeek"
            :options="dayOptions"
            optionLabel="label"
            optionValue="value"
            inputId="schedule-day"
            class="w-full"
          />
        </div>

        <div v-if="form.scheduleKind !== 'interval'" class="grid gap-1">
          <label for="schedule-time" class="text-sm font-medium text-gray-700">Giờ chạy (UTC)</label>
          <input
            id="schedule-time"
            v-model="form.timeOfDay"
            type="time"
            required
            class="min-h-11 rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-900 focus:border-sky-500"
          />
          <p class="text-xs text-gray-400">Giờ tính theo UTC (giờ Việt Nam = UTC + 7).</p>
        </div>

        <div class="flex items-center justify-end gap-2 pt-1">
          <Button type="button" label="Hủy" text severity="secondary" :disabled="saving" @click="showForm = false" class="!min-h-11 !px-4 !rounded-xl !text-sm" />
          <Button type="submit" :label="editingId ? 'Lưu thay đổi' : 'Tạo lịch'" icon="pi pi-check" :loading="saving" class="!min-h-11 !px-4 !rounded-xl !text-sm !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800" />
        </div>
      </form>
    </Dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';

type ScheduleKind = 'interval' | 'daily' | 'weekly';

interface Schedule {
  id: string;
  name: string;
  prompt: string;
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
  scheduleKind: ScheduleKind;
  intervalMinutes: number | null;
  /** "HH:mm" from the native time input; converted to minutes on save. */
  timeOfDay: string;
  dayOfWeek: number;
}

const kindOptions = [
  { label: 'Theo chu kỳ (phút)', value: 'interval' },
  { label: 'Hàng ngày', value: 'daily' },
  { label: 'Hàng tuần', value: 'weekly' }
];

const DAY_SHORT = ['CN', 'T2', 'T3', 'T4', 'T5', 'T6', 'T7'];
const DAY_FULL = ['Chủ nhật', 'Thứ 2', 'Thứ 3', 'Thứ 4', 'Thứ 5', 'Thứ 6', 'Thứ 7'];
const dayOptions = DAY_SHORT.map((label, value) => ({ label, value }));

const schedules = ref<Schedule[]>([]);
const loading = ref(false);
const pageError = ref('');
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
  if (schedule.scheduleKind === 'daily') return `Hàng ngày lúc ${formatUtcTime(schedule.timeOfDayMinutes)} UTC`;
  const day = DAY_FULL[schedule.dayOfWeek ?? 0] ?? 'Chủ nhật';
  return `${day} hàng tuần lúc ${formatUtcTime(schedule.timeOfDayMinutes)} UTC`;
};

const formatUtcDate = (iso: string | null): string => {
  if (!iso) return '—';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleString('vi-VN', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit', year: 'numeric' });
};

const statusLabel = (status: string): string => {
  if (status === 'Success') return 'Thành công';
  if (status === 'Failed') return 'Thất bại';
  if (status === 'Running') return 'Đang chạy';
  return status;
};

const statusChipClass = (status: string): string => {
  if (status === 'Failed') return 'bg-red-50 text-red-800 border-red-200';
  if (status === 'Success') return 'bg-emerald-50 text-emerald-900 border-emerald-200';
  return 'bg-gray-50 text-gray-600 border-gray-200';
};

const statusDotClass = (status: string): string => {
  if (status === 'Failed') return 'bg-red-500';
  if (status === 'Success') return 'bg-emerald-500';
  return 'bg-gray-400';
};

// --- CRUD -------------------------------------------------------------------

const loadSchedules = async () => {
  loading.value = schedules.value.length === 0;
  pageError.value = '';
  try {
    const response = await http.get(ApiFactory.SCHEDULES.BASE);
    if (response.ok) schedules.value = await response.json();
    else pageError.value = await readApiError(response, 'Không thể tải lịch tác vụ');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể tải lịch tác vụ.');
  } finally {
    loading.value = false;
  }
};

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
  if (!name) {
    formError.value = 'Vui lòng nhập tên lịch.';
    return;
  }
  if (!prompt) {
    formError.value = 'Vui lòng nhập prompt.';
    return;
  }

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
    await loadSchedules();
  } catch (cause) {
    formError.value = errorMessage(cause, 'Không thể lưu lịch tác vụ.');
  } finally {
    saving.value = false;
  }
};

const toggleSchedule = async (schedule: Schedule) => {
  if (togglingId.value) return;
  togglingId.value = schedule.id;
  pageError.value = '';
  try {
    const response = await http.post(ApiFactory.SCHEDULES.TOGGLE(schedule.id));
    if (response.ok) await loadSchedules();
    else pageError.value = await readApiError(response, 'Không thể bật/tắt lịch');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể bật/tắt lịch.');
  } finally {
    togglingId.value = null;
  }
};

const deleteSchedule = async (schedule: Schedule) => {
  if (!confirm(`Xóa lịch "${schedule.name}"? Hành động này không thể hoàn tác.`)) return;
  deletingId.value = schedule.id;
  pageError.value = '';
  try {
    const response = await http.delete(ApiFactory.SCHEDULES.BY_ID(schedule.id));
    if (response.ok) schedules.value = schedules.value.filter((item) => item.id !== schedule.id);
    else pageError.value = await readApiError(response, 'Không thể xóa lịch');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể xóa lịch.');
  } finally {
    deletingId.value = null;
  }
};

onMounted(() => {
  void loadSchedules();
});
</script>
