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

      <div v-if="list.error.value" role="alert" class="mb-5 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        {{ list.error.value }}
      </div>

      <div class="mb-3 flex flex-wrap items-center justify-between gap-3">
        <span class="relative">
          <i class="pi pi-search absolute left-3 top-1/2 -translate-y-1/2 text-gray-400 text-xs" aria-hidden="true"></i>
          <InputText v-model="list.search.value" placeholder="Tìm theo tên khóa…" class="!pl-8 w-72 max-w-full" aria-label="Tìm kiếm API key" @input="list.onSearchInput" />
        </span>
        <Button icon="pi pi-refresh" severity="secondary" outlined :loading="list.loading.value" aria-label="Tải lại" @click="list.reload" />
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
            <i class="pi pi-key text-3xl text-gray-300 block mb-3" aria-hidden="true"></i>
            {{ list.search.value ? 'Không tìm thấy API key phù hợp.' : 'Chưa có API key nào — tạo khóa đầu tiên để tích hợp hệ thống ngoài.' }}
          </div>
        </template>

        <Column field="name" header="Tên khóa" style="min-width: 12rem">
          <template #body="{ data }">
            <span class="text-sm font-semibold text-gray-900">{{ data.name }}</span>
            <div class="text-xs text-gray-400 mt-0.5">Tạo ngày {{ formatDate(data.createdAtUtc) }}</div>
          </template>
        </Column>
        <Column header="Trạng thái" style="min-width: 8rem">
          <template #body="{ data }">
            <Tag :value="keyStatus(data).label" :severity="keyStatus(data).severity" />
          </template>
        </Column>
        <Column header="Sử dụng" style="min-width: 10rem">
          <template #body="{ data }"><span class="text-sm text-gray-700 tabular-nums">{{ usageLabel(data) }}</span></template>
        </Column>
        <Column header="Hết hạn" style="min-width: 10rem">
          <template #body="{ data }"><span class="text-sm text-gray-700">{{ expiryLabel(data) }}</span></template>
        </Column>
        <Column header="Thao tác" style="width: 8rem">
          <template #body="{ data }">
            <Button
              icon="pi pi-ban"
              label="Thu hồi"
              severity="danger"
              outlined
              size="small"
              :disabled="!data.isActive || revokingId !== null"
              :loading="revokingId === data.id"
              :aria-label="`Thu hồi API key ${data.name}`"
              @click="confirmRevoke(data)" />
          </template>
        </Column>
      </DataTable>
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
import { useConfirm } from 'primevue/useconfirm';
import { useToast } from 'primevue/usetoast';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import { formatDate } from '@/utils/date';
import { useServerPage } from '@/composables/useServerPage';

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

const confirm = useConfirm();
const toast = useToast();
const list = useServerPage<ApiKey>(ApiFactory.APIKEYS.BASE, { errorLabel: 'danh sách API key' });
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
    await list.reload();
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
    toast.add({ severity: 'warn', summary: 'Không thể sao chép tự động', detail: 'Hãy bôi đen khóa và sao chép thủ công.', life: 5000 });
  }
};

const dismissCreatedKey = () => {
  createdKey.value = null;
  rawKeyCopied.value = false;
};

const confirmRevoke = (apiKey: ApiKey) => {
  confirm.require({
    header: 'Thu hồi API key',
    message: `Thu hồi API key "${apiKey.name}"? Ứng dụng đang dùng khóa này sẽ mất quyền truy cập ngay lập tức.`,
    icon: 'pi pi-exclamation-triangle',
    acceptLabel: 'Thu hồi',
    rejectLabel: 'Hủy',
    acceptProps: { severity: 'danger' },
    rejectProps: { severity: 'secondary', outlined: true },
    accept: () => { void revokeKey(apiKey); }
  });
};

const revokeKey = async (apiKey: ApiKey) => {
  revokingId.value = apiKey.id;
  try {
    const response = await http.delete(ApiFactory.APIKEYS.BY_ID(apiKey.id));
    if (response.ok) {
      toast.add({ severity: 'success', summary: 'Đã thu hồi API key', detail: apiKey.name, life: 3000 });
      await list.reload();
    } else {
      toast.add({ severity: 'error', summary: 'Không thể thu hồi', detail: await readApiError(response, 'Không thể thu hồi API key'), life: 6000 });
    }
  } catch (cause) {
    toast.add({ severity: 'error', summary: 'Không thể thu hồi', detail: errorMessage(cause, 'Không thể thu hồi API key.'), life: 6000 });
  } finally {
    revokingId.value = null;
  }
};

const isExpired = (apiKey: ApiKey): boolean => {
  if (!apiKey.expiresAtUtc) return false;
  const time = new Date(apiKey.expiresAtUtc).getTime();
  return !Number.isNaN(time) && time < Date.now();
};

const keyStatus = (apiKey: ApiKey): { label: string; severity: string } => {
  if (!apiKey.isActive) return { label: 'Đã thu hồi', severity: 'secondary' };
  if (isExpired(apiKey)) return { label: 'Hết hạn', severity: 'warn' };
  return { label: 'Hoạt động', severity: 'success' };
};

const usageLabel = (apiKey: ApiKey): string => {
  if (apiKey.maxRequests === 0) return `${apiKey.usedRequests} lượt gọi · Không giới hạn`;
  return `${apiKey.usedRequests}/${apiKey.maxRequests} lượt gọi`;
};

const expiryLabel = (apiKey: ApiKey): string => {
  if (!apiKey.expiresAtUtc) return 'Không hết hạn';
  return `${isExpired(apiKey) ? 'Đã hết hạn' : 'Hết hạn'} ${formatDate(apiKey.expiresAtUtc)}`;
};

onMounted(() => {
  void list.load();
});

onUnmounted(() => {
  if (copyResetTimer) clearTimeout(copyResetTimer);
});
</script>
