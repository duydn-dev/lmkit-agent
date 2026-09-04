<template>
  <div class="flex-1 flex flex-col h-full bg-chatgpt-dark overflow-y-auto">
    <!-- Page Header -->
    <div class="sticky top-0 z-10 bg-chatgpt-dark/80 backdrop-blur-xl border-b border-gray-200/60">
      <div class="max-w-3xl mx-auto px-6 py-4 flex items-center gap-4">
        <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-violet-500 to-fuchsia-600 flex items-center justify-center shadow-md shadow-violet-500/20">
          <i class="pi pi-user-edit text-white text-sm" aria-hidden="true"></i>
        </div>
        <div>
          <h1 class="text-xl font-bold text-gray-900 tracking-tight">Hướng dẫn tùy chỉnh</h1>
          <p class="text-xs text-gray-500">Cá nhân hóa cách trợ lý AI phản hồi trong mọi đoạn chat của bạn.</p>
        </div>
      </div>
    </div>

    <!-- Main Content -->
    <div class="flex-1 max-w-3xl mx-auto w-full px-6 py-6">
      <div v-if="pageError" role="alert" class="mb-5 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        {{ pageError }}
      </div>

      <div v-if="loading" class="flex flex-col items-center justify-center py-20 text-gray-500" role="status">
        <i class="pi pi-spin pi-spinner text-2xl mb-3" aria-hidden="true"></i>
        <p class="text-sm">Đang tải hướng dẫn tùy chỉnh...</p>
      </div>

      <form v-else @submit.prevent="save" class="grid gap-6">
        <p class="text-sm text-gray-500 leading-relaxed">
          Những hướng dẫn này được thêm vào đầu system prompt của mọi đoạn chat, trước persona của
          agent hoặc dự án (nếu có) — giống tính năng "Custom instructions" của ChatGPT.
        </p>

        <div class="grid gap-1.5">
          <label for="about-user" class="text-sm font-medium text-gray-700">
            Trợ lý nên biết gì về bạn?
          </label>
          <Textarea
            id="about-user"
            v-model="aboutUser"
            rows="5"
            :maxlength="MAX_LENGTH"
            autoResize
            placeholder="Ví dụ: Tôi là kỹ sư phần mềm ở Hà Nội, làm việc chủ yếu với .NET và Vue..."
            class="w-full"
          />
          <div class="text-right text-[11px] tabular-nums" :class="aboutUser.length > MAX_LENGTH ? 'text-red-500' : 'text-gray-400'">
            {{ aboutUser.length }} / {{ MAX_LENGTH }}
          </div>
        </div>

        <div class="grid gap-1.5">
          <label for="response-style" class="text-sm font-medium text-gray-700">
            Bạn muốn trợ lý phản hồi như thế nào?
          </label>
          <Textarea
            id="response-style"
            v-model="responseStyle"
            rows="5"
            :maxlength="MAX_LENGTH"
            autoResize
            placeholder="Ví dụ: Trả lời ngắn gọn, ưu tiên ví dụ mã nguồn, luôn dùng tiếng Việt..."
            class="w-full"
          />
          <div class="text-right text-[11px] tabular-nums" :class="responseStyle.length > MAX_LENGTH ? 'text-red-500' : 'text-gray-400'">
            {{ responseStyle.length }} / {{ MAX_LENGTH }}
          </div>
        </div>

        <div class="flex items-center justify-between gap-3 pt-1">
          <span v-if="updatedAt" class="text-xs text-gray-400">Cập nhật lần cuối: {{ formatDate(updatedAt) }}</span>
          <span v-else class="text-xs text-gray-400">Chưa lưu hướng dẫn nào.</span>
          <Button
            type="submit"
            label="Lưu hướng dẫn"
            icon="pi pi-check"
            :loading="saving"
            :disabled="aboutUser.length > MAX_LENGTH || responseStyle.length > MAX_LENGTH"
            class="!min-h-11 !px-4 !rounded-xl !text-sm !bg-violet-700 !border-violet-700 hover:!bg-violet-800 hover:!border-violet-800"
          />
        </div>
      </form>
    </div>

    <Toast position="bottom-right" />
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { useToast } from 'primevue/usetoast';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import { formatDate } from '@/utils/date';

// Mirrors UserPreference.MaxLength / UserPreferenceRules.MaxFieldLength on the server.
const MAX_LENGTH = 2000;

const toast = useToast();

const aboutUser = ref('');
const responseStyle = ref('');
const updatedAt = ref<string | null>(null);

const loading = ref(false);
const saving = ref(false);
const pageError = ref('');

const load = async () => {
  loading.value = true;
  pageError.value = '';
  try {
    const response = await http.get(ApiFactory.USER.CUSTOM_INSTRUCTIONS);
    if (response.ok) {
      const data = await response.json() as { aboutUser?: string | null; responseStyle?: string | null; updatedAtUtc?: string | null };
      aboutUser.value = data.aboutUser ?? '';
      responseStyle.value = data.responseStyle ?? '';
      updatedAt.value = data.updatedAtUtc ?? null;
    } else {
      pageError.value = await readApiError(response, 'Không thể tải hướng dẫn tùy chỉnh');
    }
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể tải hướng dẫn tùy chỉnh.');
  } finally {
    loading.value = false;
  }
};

const save = async () => {
  if (aboutUser.value.length > MAX_LENGTH || responseStyle.value.length > MAX_LENGTH) return;
  saving.value = true;
  pageError.value = '';
  try {
    const response = await http.put(ApiFactory.USER.CUSTOM_INSTRUCTIONS, {
      aboutUser: aboutUser.value.trim() || null,
      responseStyle: responseStyle.value.trim() || null
    });
    if (!response.ok) {
      pageError.value = await readApiError(response, 'Không thể lưu hướng dẫn tùy chỉnh');
      return;
    }
    const data = await response.json() as { aboutUser?: string | null; responseStyle?: string | null; updatedAtUtc?: string | null };
    aboutUser.value = data.aboutUser ?? '';
    responseStyle.value = data.responseStyle ?? '';
    updatedAt.value = data.updatedAtUtc ?? null;
    toast.add({ severity: 'success', summary: 'Đã lưu', detail: 'Hướng dẫn tùy chỉnh sẽ áp dụng cho các đoạn chat mới.', life: 5000 });
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể lưu hướng dẫn tùy chỉnh.');
  } finally {
    saving.value = false;
  }
};

onMounted(() => {
  void load();
});
</script>
