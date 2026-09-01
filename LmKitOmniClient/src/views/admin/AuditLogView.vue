<template>
  <div class="flex-1 overflow-y-auto bg-gray-50 p-4 md:p-6">
    <div class="max-w-6xl mx-auto">
      <!-- Page header -->
      <header class="mb-6 flex items-center gap-4">
        <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-teal-500 to-emerald-600 flex items-center justify-center shadow-md shadow-teal-500/20 flex-shrink-0">
          <i class="pi pi-shield text-white text-sm" aria-hidden="true"></i>
        </div>
        <div>
          <h1 class="text-xl font-bold text-gray-900 tracking-tight">Nhật ký hoạt động</h1>
          <p class="text-sm text-gray-500">Lịch sử gọi công cụ của agent và hoạt động hệ thống trong tenant.</p>
        </div>
      </header>

      <!-- Filters -->
      <section aria-labelledby="audit-filter-heading" class="mb-5">
        <h2 id="audit-filter-heading" class="sr-only">Bộ lọc</h2>
        <form @submit.prevent="applyFilters" class="p-4 bg-white border border-gray-200 rounded-xl grid gap-3">
          <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
            <div class="grid gap-1">
              <label for="audit-actor" class="text-sm font-medium text-gray-700">Chủ thể</label>
              <Select v-model="filters.actorType" :options="actorTypeOptions" optionLabel="label" optionValue="value" inputId="audit-actor" aria-label="Chủ thể" class="w-full" />
            </div>
            <div class="grid gap-1">
              <label for="audit-action" class="text-sm font-medium text-gray-700">Hành động</label>
              <Select v-model="filters.action" :options="actionOptions" optionLabel="label" optionValue="value" inputId="audit-action" aria-label="Hành động" class="w-full" />
            </div>
            <div class="grid gap-1">
              <label for="audit-entity" class="text-sm font-medium text-gray-700">Đối tượng</label>
              <Select v-model="filters.entityType" :options="entityTypeOptions" optionLabel="label" optionValue="value" inputId="audit-entity" aria-label="Đối tượng" class="w-full" />
            </div>
            <div class="grid gap-1">
              <label for="audit-from" class="text-sm font-medium text-gray-700">Từ ngày</label>
              <input id="audit-from" v-model="filters.fromDate" type="date" class="min-h-11 rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-900 focus:border-sky-500 focus:outline-none" />
            </div>
            <div class="grid gap-1">
              <label for="audit-to" class="text-sm font-medium text-gray-700">Đến ngày</label>
              <input id="audit-to" v-model="filters.toDate" type="date" class="min-h-11 rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-900 focus:border-sky-500 focus:outline-none" />
            </div>
            <div class="grid gap-1">
              <label for="audit-pagesize" class="text-sm font-medium text-gray-700">Số dòng mỗi trang</label>
              <Select v-model="pageSize" :options="pageSizeOptions" optionLabel="label" optionValue="value" inputId="audit-pagesize" aria-label="Số dòng mỗi trang" class="w-full" @change="onPageSizeChange" />
            </div>
          </div>
          <div class="flex flex-wrap items-center gap-2">
            <Button type="submit" label="Lọc" icon="pi pi-filter" :loading="loading" class="!min-h-11 !px-4 !rounded-xl !text-sm !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800" />
            <Button type="button" label="Xóa lọc" icon="pi pi-filter-slash" outlined severity="secondary" :disabled="loading" class="!min-h-11 !px-4 !rounded-xl !text-sm" @click="resetFilters" />
          </div>
        </form>
      </section>

      <!-- Results -->
      <section aria-labelledby="audit-results-heading">
        <h2 id="audit-results-heading" class="sr-only">Kết quả nhật ký</h2>
        <div class="bg-white rounded-xl border border-gray-200 overflow-hidden">
          <div v-if="loading" role="status" class="flex flex-col items-center justify-center py-16 text-gray-500">
            <i class="pi pi-spin pi-spinner text-2xl mb-3" aria-hidden="true"></i>
            <p class="text-sm">Đang tải nhật ký hoạt động...</p>
          </div>

          <div v-else-if="pageError" role="alert" class="m-4 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
            {{ pageError }}
          </div>

          <div v-else-if="items.length === 0" class="flex flex-col items-center justify-center py-16 text-center text-gray-500">
            <i class="pi pi-inbox text-3xl text-gray-300 mb-3" aria-hidden="true"></i>
            <p class="text-sm">Không có bản ghi nào.</p>
          </div>

          <div v-else class="overflow-x-auto">
            <table class="w-full text-sm text-left">
              <caption class="sr-only">Danh sách bản ghi nhật ký hoạt động</caption>
              <thead class="bg-gray-50 text-gray-700 border-b border-gray-200">
                <tr>
                  <th scope="col" class="px-4 py-3 font-semibold whitespace-nowrap">Thời gian</th>
                  <th scope="col" class="px-4 py-3 font-semibold">Chủ thể</th>
                  <th scope="col" class="px-4 py-3 font-semibold">Hành động</th>
                  <th scope="col" class="px-4 py-3 font-semibold">Đối tượng</th>
                  <th scope="col" class="px-4 py-3 font-semibold text-right"><span class="sr-only">Thao tác</span></th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="item in items" :key="item.id" class="border-b border-gray-100 last:border-0 hover:bg-gray-50">
                  <td class="px-4 py-3 text-gray-700 whitespace-nowrap">{{ formatDateTime(item.createdAtUtc) }}</td>
                  <td class="px-4 py-3 text-gray-700">{{ item.actorType }}</td>
                  <td class="px-4 py-3 text-gray-900 font-medium">{{ item.action }}</td>
                  <td class="px-4 py-3 text-gray-700">{{ item.entityType }}</td>
                  <td class="px-4 py-3 text-right">
                    <button
                      type="button"
                      class="inline-flex items-center gap-1.5 min-h-11 px-3 rounded-lg text-sm font-medium text-sky-700 hover:bg-sky-50 transition-colors cursor-pointer"
                      :aria-label="`Xem chi tiết bản ghi ${item.action} lúc ${formatDateTime(item.createdAtUtc)}`"
                      @click="openDetail(item)"
                    >
                      <i class="pi pi-eye" aria-hidden="true"></i>
                      <span>Chi tiết</span>
                    </button>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>

        <!-- Pagination -->
        <div v-if="!loading && !pageError && total > 0" class="flex flex-wrap items-center justify-between gap-3 mt-4">
          <p class="text-sm text-gray-600">Trang {{ page }} / {{ totalPages }} · {{ total }} bản ghi</p>
          <div class="flex items-center gap-2">
            <Button label="Trước" icon="pi pi-chevron-left" outlined severity="secondary" :disabled="page <= 1 || loading" class="!min-h-11 !px-3 !rounded-xl !text-sm" @click="goToPage(page - 1)" />
            <Button label="Sau" icon="pi pi-chevron-right" iconPos="right" outlined severity="secondary" :disabled="page >= totalPages || loading" class="!min-h-11 !px-3 !rounded-xl !text-sm" @click="goToPage(page + 1)" />
          </div>
        </div>
      </section>
    </div>

    <!-- Detail dialog -->
    <Dialog
      v-model:visible="detailVisible"
      modal
      header="Chi tiết bản ghi nhật ký"
      aria-label="Chi tiết bản ghi nhật ký"
      :style="{ width: '640px' }"
      :breakpoints="{ '640px': '92vw' }"
      :dismissableMask="true"
    >
      <div v-if="selected" class="grid gap-4 pt-1">
        <dl class="grid grid-cols-1 sm:grid-cols-2 gap-x-4 gap-y-3 text-sm">
          <div>
            <dt class="text-xs font-medium text-gray-500">Thời gian</dt>
            <dd class="text-gray-900 mt-0.5">{{ formatDateTime(selected.createdAtUtc) }}</dd>
          </div>
          <div>
            <dt class="text-xs font-medium text-gray-500">Chủ thể</dt>
            <dd class="text-gray-900 mt-0.5">{{ selected.actorType }}</dd>
          </div>
          <div>
            <dt class="text-xs font-medium text-gray-500">Người dùng</dt>
            <dd class="text-gray-900 mt-0.5 break-all">{{ selected.actorUserId || '—' }}</dd>
          </div>
          <div>
            <dt class="text-xs font-medium text-gray-500">Hành động</dt>
            <dd class="text-gray-900 mt-0.5">{{ selected.action }}</dd>
          </div>
          <div>
            <dt class="text-xs font-medium text-gray-500">Đối tượng</dt>
            <dd class="text-gray-900 mt-0.5">{{ selected.entityType }}</dd>
          </div>
          <div>
            <dt class="text-xs font-medium text-gray-500">Mã đối tượng</dt>
            <dd class="text-gray-900 mt-0.5 break-all">{{ selected.entityId || '—' }}</dd>
          </div>
          <div class="sm:col-span-2">
            <dt class="text-xs font-medium text-gray-500">Mã tương quan</dt>
            <dd class="text-gray-900 mt-0.5 break-all">{{ selected.correlationId || '—' }}</dd>
          </div>
        </dl>
        <div>
          <h3 class="text-xs font-medium text-gray-500 mb-1">Chi tiết (JSON)</h3>
          <pre class="rounded-xl bg-gray-900 text-gray-100 text-xs leading-relaxed p-4 overflow-x-auto overflow-y-auto max-h-80 whitespace-pre-wrap break-words">{{ prettyDetails || '—' }}</pre>
        </div>
      </div>
    </Dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';

interface AuditLogDto {
  id: string;
  actorUserId?: string | null;
  actorType: string;
  action: string;
  entityType: string;
  entityId?: string | null;
  correlationId?: string | null;
  detailsJson?: string | null;
  createdAtUtc: string;
}
interface AuditFacets {
  actorTypes: string[];
  actions: string[];
  entityTypes: string[];
}

const items = ref<AuditLogDto[]>([]);
const total = ref(0);
const page = ref(1);
const pageSize = ref(25);
const loading = ref(false);
const pageError = ref('');

const facets = ref<AuditFacets>({ actorTypes: [], actions: [], entityTypes: [] });
const filters = ref({ actorType: '', action: '', entityType: '', fromDate: '', toDate: '' });

const selected = ref<AuditLogDto | null>(null);
const detailVisible = ref(false);

const withAll = (values: string[]) => [{ label: 'Tất cả', value: '' }, ...values.map((v) => ({ label: v, value: v }))];
const actorTypeOptions = computed(() => withAll(facets.value.actorTypes));
const actionOptions = computed(() => withAll(facets.value.actions));
const entityTypeOptions = computed(() => withAll(facets.value.entityTypes));
const pageSizeOptions = [
  { label: '25', value: 25 },
  { label: '50', value: 50 },
  { label: '100', value: 100 }
];

const totalPages = computed(() => Math.max(1, Math.ceil(total.value / pageSize.value)));

const prettyDetails = computed(() => {
  const raw = selected.value?.detailsJson;
  if (!raw) return '';
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
});

const formatDateTime = (value: string | null | undefined): string => {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return String(value);
  return date.toLocaleString('vi-VN');
};

/** Filter dropdowns are a convenience: any failure is swallowed (console.warn). */
const loadFacets = async () => {
  try {
    const response = await http.get(ApiFactory.AUDIT.FACETS);
    if (!response.ok) {
      console.warn('[audit] không thể tải danh mục bộ lọc', response.status);
      return;
    }
    const data = await response.json();
    facets.value = {
      actorTypes: Array.isArray(data?.actorTypes) ? data.actorTypes : [],
      actions: Array.isArray(data?.actions) ? data.actions : [],
      entityTypes: Array.isArray(data?.entityTypes) ? data.entityTypes : []
    };
  } catch (cause) {
    console.warn('[audit] không thể tải danh mục bộ lọc', cause);
  }
};

const buildQuery = (): string => {
  const params = new URLSearchParams();
  if (filters.value.actorType) params.set('actorType', filters.value.actorType);
  if (filters.value.action) params.set('action', filters.value.action);
  if (filters.value.entityType) params.set('entityType', filters.value.entityType);
  if (filters.value.fromDate) {
    const from = new Date(filters.value.fromDate);
    if (!Number.isNaN(from.getTime())) params.set('fromUtc', from.toISOString());
  }
  if (filters.value.toDate) {
    const to = new Date(filters.value.toDate);
    if (!Number.isNaN(to.getTime())) params.set('toUtc', to.toISOString());
  }
  params.set('page', String(page.value));
  params.set('pageSize', String(pageSize.value));
  return params.toString();
};

const loadAudit = async () => {
  loading.value = true;
  pageError.value = '';
  try {
    const response = await http.get(`${ApiFactory.AUDIT.BASE}?${buildQuery()}`);
    if (!response.ok) {
      pageError.value = await readApiError(response, 'Không thể tải nhật ký hoạt động');
      return;
    }
    const data = await response.json();
    items.value = Array.isArray(data?.items) ? data.items : [];
    total.value = typeof data?.total === 'number' ? data.total : items.value.length;
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể tải nhật ký hoạt động.');
  } finally {
    loading.value = false;
  }
};

const applyFilters = () => {
  page.value = 1;
  void loadAudit();
};

const resetFilters = () => {
  filters.value = { actorType: '', action: '', entityType: '', fromDate: '', toDate: '' };
  pageSize.value = 25;
  page.value = 1;
  void loadAudit();
};

const onPageSizeChange = () => {
  page.value = 1;
  void loadAudit();
};

const goToPage = (target: number) => {
  const clamped = Math.min(Math.max(1, target), totalPages.value);
  if (clamped === page.value) return;
  page.value = clamped;
  void loadAudit();
};

const openDetail = (item: AuditLogDto) => {
  selected.value = item;
  detailVisible.value = true;
};

onMounted(() => {
  void loadFacets();
  void loadAudit();
});
</script>
