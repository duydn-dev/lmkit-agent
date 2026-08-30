<template>
  <div class="flex-1 flex flex-col h-full bg-chatgpt-dark overflow-y-auto">
    <!-- Page Header -->
    <div class="sticky top-0 z-10 bg-chatgpt-dark/80 backdrop-blur-xl border-b border-gray-200/60">
      <div class="max-w-7xl mx-auto px-6 py-4">
        <div class="flex items-center justify-between gap-4">
          <div class="flex items-center gap-4">
            <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-amber-500 to-orange-600 flex items-center justify-center shadow-md shadow-amber-500/20">
              <i class="pi pi-key text-white text-sm"></i>
            </div>
            <div>
              <h1 class="text-xl font-bold text-gray-900 tracking-tight">API Keys</h1>
              <p class="text-xs text-gray-500">Cấp quyền gọi API cho ứng dụng bên ngoài qua header X-Api-Key</p>
            </div>
          </div>
          <Button
            @click="openCreateForm"
            label="Tạo API key"
            icon="pi pi-plus"
            class="!min-h-11 !px-4 !py-2.5 !rounded-xl !text-sm !font-medium !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800"
          />
        </div>
      </div>
    </div>

    <!-- Main Content -->
    <div class="flex-1 max-w-5xl mx-auto w-full px-6 py-6">
      <!-- One-time raw key panel: the key is only ever available here, until dismissed. -->
      <section
        v-if="createdKey"
        aria-label="API key vừa tạo"
        class="mb-6 rounded-2xl border-2 border-amber-300 bg-amber-50 p-5"
      >
        <div class="flex items-start gap-2 text-amber-900">
          <i class="pi pi-exclamation-triangle mt-0.5" aria-hidden="true"></i>
          <div class="min-w-0 flex-1">
            <h2 class="text-sm font-semibold">Khóa chỉ hiển thị một lần — hãy lưu ngay</h2>
            <p class="text-xs text-amber-800 mt-1">
              API key "{{ createdKey.name }}" đã được tạo. Sau khi bạn đóng bảng này, hệ thống không thể hiển thị lại khóa.
            </p>
          </div>
        </div>
        <div class="flex flex-wrap items-center gap-2 mt-3">
          <code class="flex-1 min-w-0 basis-64 block truncate rounded-lg bg-white border border-amber-200 px-3 py-2.5 text-sm font-mono text-gray-900">{{ createdKey.rawKey }}</code>
          <Button
            :icon="rawKeyCopied ? 'pi pi-check' : 'pi pi-copy'"
            :label="rawKeyCopied ? 'Đã sao chép' : 'Sao chép'"
            @click="copyRawKey"
            class="!min-h-11 !rounded-xl !text-sm flex-shrink-0 !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800"
          />
          <Button
            label="Tôi đã lưu khóa"
            icon="pi pi-times"
            outlined
            severity="secondary"
            @click="dismissCreatedKey"
            class="!min-h-11 !rounded-xl !text-sm flex-shrink-0"
          />
        </div>
      </section>

      <!-- Usage explanation -->
      <section aria-labelledby="apikey-usage-heading" class="mb-6 rounded-2xl border border-gray-200 bg-white p-5">
        <h2 id="apikey-usage-heading" class="text-sm font-semibold text-gray-900 mb-1">Cách sử dụng API key</h2>
        <p class="text-xs text-gray-500 mb-3">
          Gửi khóa trong header <code class="font-mono text-[11px] bg-gray-100 border border-gray-200 rounded px-1 py-0.5 text-gray-900">X-Api-Key</code>
          với mỗi yêu cầu đến API. Ví dụ với curl:
        </p>
        <pre class="rounded-xl bg-gray-900 text-gray-100 text-xs leading-relaxed p-4 overflow-x-auto"><code>curl -H "X-Api-Key: KHOA_CUA_BAN" \
  https://may-chu-cua-ban/api/chat/sessions</code></pre>
      </section>

      <div v-if="pageError" role="alert" class="mb-5 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        {{ pageError }}
      </div>

      <div v-if="loading" class="flex flex-col items-center justify-center py-16 text-gray-500" role="status">
        <i class="pi pi-spin pi-spinner text-2xl mb-3" aria-hidden="true"></i>
        <p class="text-sm">Đang tải danh sách API key...</p>
      </div>

      <div v-else-if="apiKeys.length === 0" class="flex flex-col items-center justify-center py-16 text-center">
        <div class="w-20 h-20 rounded-2xl bg-gradient-to-br from-gray-100 to-gray-200 flex items-center justify-center mb-5 shadow-inner">
          <i class="pi pi-key text-3xl text-gray-300" aria-hidden="true"></i>
        </div>
        <h3 class="text-lg font-semibold text-gray-600 mb-1">Chưa có API key nào</h3>
        <p class="text-sm text-gray-400 max-w-xs mb-4">Tạo API key đầu tiên để tích hợp hệ thống bên ngoài với nền tảng.</p>
        <Button label="Tạo API key" icon="pi pi-plus" @click="openCreateForm" class="!min-h-11 !px-4 !rounded-xl !text-sm !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800" />
      </div>

      <div v-else class="grid gap-2">
        <div
          v-for="apiKey in apiKeys"
          :key="apiKey.id"
          class="flex flex-wrap items-center justify-between gap-3 p-4 bg-white border border-gray-200 rounded-2xl"
        >
          <div class="min-w-0 flex-1 basis-64">
            <div class="flex items-center gap-2 flex-wrap">
              <span class="text-sm font-semibold text-gray-900 truncate">{{ apiKey.name }}</span>
              <span
                class="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-semibold border"
                :class="keyStatus(apiKey).classes"
              >{{ keyStatus(apiKey).label }}</span>
            </div>
            <p class="text-xs text-gray-500 mt-1">
              {{ usageLabel(apiKey) }} · {{ expiryLabel(apiKey) }} · Tạo ngày {{ formatDate(apiKey.createdAtUtc) }}
            </p>
          </div>
          <Button
            icon="pi pi-ban"
            label="Thu hồi"
            severity="danger"
            outlined
            :disabled="!apiKey.isActive || revokingId !== null"
            :loading="revokingId === apiKey.id"
            @click="revokeKey(apiKey)"
            :aria-label="`Thu hồi API key ${apiKey.name}`"
            class="!min-h-11 !px-3 !rounded-xl !text-sm flex-shrink-0"
          />
        </div>
      </div>
    </div>

    <!-- Create Dialog -->
    <Dialog
      v-model:visible="showForm"
      modal
      header="Tạo API key mới"
      :style="{ width: '480px' }"
      :breakpoints="{ '575px': '90vw' }"
    >
      <form @submit.prevent="createKey" class="grid gap-4 pt-1">
        <div v-if="formError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
          {{ formError }}
        </div>

        <div class="grid gap-1">
          <label for="apikey-name" class="text-sm font-medium text-gray-700">Tên khóa</label>
          <InputText id="apikey-name" v-model="form.name" required maxlength="100" placeholder="Ví dụ: Tích hợp CRM" />
        </div>

        <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <div class="grid gap-1">
            <label for="apikey-expires" class="text-sm font-medium text-gray-700">Hết hạn sau (ngày)</label>
            <InputNumber inputId="apikey-expires" v-model="form.expiresInDays" :min="1" :max="3650" showButtons />
            <p class="text-xs text-gray-400">Mặc định 90 ngày.</p>
          </div>
          <div class="grid gap-1">
            <label for="apikey-max" class="text-sm font-medium text-gray-700">Giới hạn lượt gọi</label>
            <InputNumber inputId="apikey-max" v-model="form.maxRequests" :min="0" showButtons />
            <p class="text-xs text-gray-400">0 = không giới hạn.</p>
          </div>
        </div>

        <div class="flex items-center justify-end gap-2 pt-1">
          <Button type="button" label="Hủy" text severity="secondary" :disabled="saving" @click="showForm = false" class="!min-h-11 !px-4 !rounded-xl !text-sm" />
          <Button
            type="submit"
            label="Tạo khóa"
            icon="pi pi-check"
            :loading="saving"
            class="!min-h-11 !px-4 !rounded-xl !text-sm !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800"
          />
        </div>
      </form>
    </Dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';

interface ApiKey {
  id: string;
  name: string;
  /** 0 = unlimited. */
  maxRequests: number;
  usedRequests: number;
  expiresAtUtc: string | null;
  createdAtUtc: string;
  isActive: boolean;
}

/** 201 response of POST /api/api-keys. rawKey is returned exactly once. */
interface CreatedApiKey {
  id: string;
  name: string;
  rawKey: string;
  expiresAtUtc: string | null;
}

interface ApiKeyForm {
  name: string;
  expiresInDays: number | null;
  maxRequests: number | null;
}

const apiKeys = ref<ApiKey[]>([]);
const loading = ref(false);
const pageError = ref('');
const revokingId = ref<string | null>(null);

const showForm = ref(false);
const saving = ref(false);
const formError = ref('');

/**
 * The freshly created key. Kept in component state until the user dismisses it
 * (the backend never returns rawKey again). NEVER log or persist this value.
 */
const createdKey = ref<CreatedApiKey | null>(null);
const rawKeyCopied = ref(false);
let copyResetTimer: ReturnType<typeof setTimeout> | undefined;

const emptyForm = (): ApiKeyForm => ({ name: '', expiresInDays: 90, maxRequests: 0 });
const form = ref<ApiKeyForm>(emptyForm());

const loadKeys = async () => {
  loading.value = apiKeys.value.length === 0;
  pageError.value = '';
  try {
    const response = await http.get(ApiFactory.APIKEYS.BASE);
    if (response.ok) apiKeys.value = await response.json();
    else pageError.value = await readApiError(response, 'Không thể tải danh sách API key');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể tải danh sách API key.');
  } finally {
    loading.value = false;
  }
};

const openCreateForm = () => {
  form.value = emptyForm();
  formError.value = '';
  showForm.value = true;
};

const createKey = async () => {
  const name = form.value.name.trim();
  if (!name) {
    formError.value = 'Vui lòng nhập tên khóa.';
    return;
  }
  formError.value = '';
  saving.value = true;
  try {
    const response = await http.post(ApiFactory.APIKEYS.BASE, {
      name,
      expiresInDays: form.value.expiresInDays ?? undefined,
      maxRequests: form.value.maxRequests ?? undefined
    });
    if (!response.ok) {
      formError.value = await readApiError(response, 'Không thể tạo API key');
      return;
    }
    createdKey.value = await response.json() as CreatedApiKey;
    rawKeyCopied.value = false;
    showForm.value = false;
    await loadKeys();
  } catch (cause) {
    formError.value = errorMessage(cause, 'Không thể tạo API key.');
  } finally {
    saving.value = false;
  }
};

const copyRawKey = async () => {
  if (!createdKey.value) return;
  try {
    await navigator.clipboard.writeText(createdKey.value.rawKey);
    rawKeyCopied.value = true;
    if (copyResetTimer) clearTimeout(copyResetTimer);
    copyResetTimer = setTimeout(() => { rawKeyCopied.value = false; }, 2500);
  } catch {
    pageError.value = 'Không thể sao chép tự động. Hãy bôi đen khóa và sao chép thủ công.';
  }
};

const dismissCreatedKey = () => {
  createdKey.value = null;
  rawKeyCopied.value = false;
};

const revokeKey = async (apiKey: ApiKey) => {
  if (!confirm(`Thu hồi API key "${apiKey.name}"? Ứng dụng đang dùng khóa này sẽ mất quyền truy cập ngay lập tức.`)) return;
  revokingId.value = apiKey.id;
  pageError.value = '';
  try {
    const response = await http.delete(ApiFactory.APIKEYS.BY_ID(apiKey.id));
    if (response.ok) await loadKeys();
    else pageError.value = await readApiError(response, 'Không thể thu hồi API key');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể thu hồi API key.');
  } finally {
    revokingId.value = null;
  }
};

const isExpired = (apiKey: ApiKey): boolean => {
  if (!apiKey.expiresAtUtc) return false;
  const time = new Date(apiKey.expiresAtUtc).getTime();
  return !Number.isNaN(time) && time < Date.now();
};

const keyStatus = (apiKey: ApiKey): { label: string; classes: string } => {
  if (!apiKey.isActive) return { label: 'Đã thu hồi', classes: 'bg-gray-50 text-gray-500 border-gray-200' };
  if (isExpired(apiKey)) return { label: 'Hết hạn', classes: 'bg-amber-50 text-amber-800 border-amber-200' };
  return { label: 'Hoạt động', classes: 'bg-emerald-50 text-emerald-900 border-emerald-200' };
};

const usageLabel = (apiKey: ApiKey): string => {
  if (apiKey.maxRequests === 0) return `${apiKey.usedRequests} lượt gọi · Không giới hạn`;
  return `${apiKey.usedRequests}/${apiKey.maxRequests} lượt gọi`;
};

const expiryLabel = (apiKey: ApiKey): string => {
  if (!apiKey.expiresAtUtc) return 'Không hết hạn';
  return `${isExpired(apiKey) ? 'Đã hết hạn' : 'Hết hạn'} ${formatDate(apiKey.expiresAtUtc)}`;
};

const formatDate = (iso: string): string => {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleDateString('vi-VN', { year: 'numeric', month: '2-digit', day: '2-digit' });
};

onMounted(() => {
  void loadKeys();
});

onUnmounted(() => {
  if (copyResetTimer) clearTimeout(copyResetTimer);
});
</script>
