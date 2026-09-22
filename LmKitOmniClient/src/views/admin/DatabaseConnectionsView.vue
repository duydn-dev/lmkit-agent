<template>
  <div class="flex-1 overflow-y-auto bg-gray-50 p-4 md:p-6">
    <div class="max-w-6xl mx-auto">
      <!-- Page header -->
      <header class="mb-4 flex items-center gap-4">
        <div class="w-10 h-10 rounded-lg bg-[--color-gov-red] flex items-center justify-center shadow-md flex-shrink-0">
          <i class="pi pi-database text-white text-sm" aria-hidden="true"></i>
        </div>
        <div>
          <h1 class="text-xl font-bold text-gray-900 tracking-tight">Database Connections</h1>
          <p class="text-sm text-gray-500">
            Kết nối CSDL ngoài cho agent truy vấn. Kết nối "Toàn hệ thống" dùng chung cho mọi tenant.
          </p>
        </div>
      </header>

      <!-- Safety note -->
      <div class="mb-4 flex items-start gap-2.5 rounded-lg border border-gray-200 bg-gray-100/70 px-4 py-3 text-xs leading-relaxed text-gray-600">
        <i class="pi pi-shield mt-0.5 text-gray-400" aria-hidden="true"></i>
        <p>
          Agent chỉ truy vấn CHỈ-ĐỌC. Chuỗi kết nối được mã hoá và không bao giờ hiển thị lại — hãy dùng tài khoản DB quyền chỉ-đọc.
          Lệnh ghi (nếu bật) LUÔN cần phê duyệt HITL và tự sao lưu bảng trước khi chạy.
        </p>
      </div>

      <div v-if="list.error.value" role="alert" class="mb-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        {{ list.error.value }}
      </div>

      <!-- Toolbar: tìm kiếm + thêm mới -->
      <div class="mb-3 flex flex-wrap items-center justify-between gap-3">
        <span class="relative">
          <i class="pi pi-search absolute left-3 top-1/2 -translate-y-1/2 text-gray-400 text-xs" aria-hidden="true"></i>
          <InputText
            v-model="list.search.value"
            placeholder="Tìm theo tên, loại CSDL…"
            class="!pl-8 w-72 max-w-full"
            aria-label="Tìm kiếm kết nối"
            @input="list.onSearchInput" />
        </span>
        <div class="flex items-center gap-2">
          <Button icon="pi pi-refresh" severity="secondary" outlined :loading="list.loading.value" aria-label="Tải lại" @click="list.reload" />
          <Button label="Thêm kết nối" icon="pi pi-plus" @click="openCreate" />
        </div>
      </div>

      <!-- Getlist chuẩn: DataTable lazy + phân trang server-side -->
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
          <div class="p-8 text-center text-gray-500 text-sm">
            <i class="pi pi-database text-3xl text-gray-300 block mb-3" aria-hidden="true"></i>
            {{ list.search.value ? 'Không tìm thấy kết nối phù hợp.' : 'Chưa có kết nối cơ sở dữ liệu nào.' }}
          </div>
        </template>

        <Column field="name" header="Tên kết nối">
          <template #body="{ data }">
            <div class="font-semibold text-gray-900 text-sm">{{ data.name }}</div>
            <div class="text-xs text-gray-400 mt-0.5">Cập nhật {{ relativeTime(data.updatedAtUtc) }}</div>
          </template>
        </Column>
        <Column field="tenantName" header="Phạm vi">
          <template #body="{ data }">
            <Tag v-if="data.tenantId === null" value="Toàn hệ thống" severity="warn" />
            <Tag v-else :value="data.tenantName || 'Tenant'" severity="secondary" />
          </template>
        </Column>
        <Column field="provider" header="Loại CSDL">
          <template #body="{ data }"><Tag :value="providerLabel(data.provider)" severity="info" /></template>
        </Column>
        <Column field="isActive" header="Trạng thái">
          <template #body="{ data }">
            <div class="flex flex-wrap gap-1">
              <Tag :value="data.isActive ? 'Kích hoạt' : 'Tạm tắt'" :severity="data.isActive ? 'success' : 'secondary'" />
              <Tag v-if="data.allowWrites" value="Cho phép ghi" severity="danger" />
            </div>
          </template>
        </Column>
        <Column field="indexStatus" header="Chỉ mục schema">
          <template #body="{ data }">
            <Tag :value="indexStatusLabel(data.indexStatus)" :severity="indexSeverity(data)" />
            <p v-if="data.lastIndexError" class="text-[11px] text-red-600 mt-1 max-w-56 truncate" :title="data.lastIndexError">
              {{ data.lastIndexError }}
            </p>
          </template>
        </Column>
        <Column header="Thao tác" :style="{ width: '190px' }">
          <template #body="{ data }">
            <div class="flex items-center gap-1">
              <Button icon="pi pi-sitemap" text rounded severity="secondary" aria-label="Xem sơ đồ schema" v-tooltip.top="'Xem sơ đồ schema'" @click="openDiagram(data)" />
              <Button icon="pi pi-bolt" text rounded severity="secondary" :loading="testingId === data.id" aria-label="Kiểm tra kết nối" v-tooltip.top="'Kiểm tra kết nối'" @click="testConnection(data)" />
              <Button icon="pi pi-sync" text rounded severity="secondary" :loading="reindexingId === data.id" aria-label="Lập chỉ mục lại" v-tooltip.top="'Lập chỉ mục lại'" @click="reindexConnection(data)" />
              <Button icon="pi pi-pencil" text rounded severity="secondary" aria-label="Sửa kết nối" @click="openEdit(data)" />
              <Button icon="pi pi-trash" text rounded severity="danger" :loading="deletingId === data.id" aria-label="Xóa kết nối" @click="confirmDelete(data)" />
            </div>
          </template>
        </Column>
      </DataTable>

      <!-- Modal create/update: form đủ trường theo entity (trừ secret chỉ-ghi) -->
      <Dialog
        v-model:visible="dialogVisible"
        modal
        :header="editingId ? 'Cập nhật kết nối CSDL' : 'Thêm kết nối CSDL'"
        class="w-[46rem] max-w-[95vw]">
        <form class="grid gap-3" @submit.prevent="save">
          <div v-if="formError" role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
            {{ formError }}
          </div>

          <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
            <div class="grid gap-1">
              <label for="db-name" class="text-sm font-medium text-gray-700">Tên kết nối <span class="text-red-500">*</span></label>
              <InputText id="db-name" v-model="form.name" required placeholder="Ví dụ kho-báo-cáo" class="w-full" />
            </div>
            <div class="grid gap-1">
              <label for="db-provider" class="text-sm font-medium text-gray-700">Loại CSDL <span class="text-red-500">*</span></label>
              <Select v-model="form.provider" :options="providerOptions" optionLabel="label" optionValue="value" inputId="db-provider" class="w-full" />
            </div>
          </div>

          <div class="grid gap-1">
            <label for="db-tenant" class="text-sm font-medium text-gray-700">Phạm vi sử dụng (tenant)</label>
            <Select
              v-model="form.tenantSelection"
              :options="tenantOptions"
              optionLabel="label"
              optionValue="value"
              inputId="db-tenant"
              class="w-full"
              :loading="tenantsLoading" />
            <p class="text-xs text-gray-500">
              "Toàn hệ thống" = TenantId null: mọi tenant đều thấy và truy vấn được kết nối này. Đổi phạm vi sẽ tự lập chỉ mục lại schema.
            </p>
          </div>

          <div class="grid gap-1">
            <label for="db-connstr" class="text-sm font-medium text-gray-700">
              Chuỗi kết nối <span v-if="!editingId" class="text-red-500">*</span>
            </label>
            <Textarea
              id="db-connstr"
              v-model="form.connectionString"
              rows="3"
              :required="!editingId"
              :placeholder="editingId ? 'Để trống để giữ chuỗi kết nối hiện tại (đã mã hoá)' : providerPlaceholder(form.provider)"
              class="w-full font-mono" />
            <p class="flex items-start gap-1.5 text-xs text-amber-700">
              <i class="pi pi-exclamation-triangle mt-0.5" aria-hidden="true"></i>
              <span>Dùng tài khoản chỉ-đọc (read-only). Chuỗi kết nối được mã hoá và không hiển thị lại.</span>
            </p>
          </div>

          <label for="db-writes" class="flex items-start gap-2 text-sm text-amber-800 rounded-lg border border-amber-200 bg-amber-50 p-3">
            <Checkbox inputId="db-writes" v-model="form.allowWrites" binary />
            <span>Cho phép ghi (INSERT/UPDATE/DELETE). Mặc định TẮT. Khi bật, lệnh ghi vẫn LUÔN cần phê duyệt HITL và hệ thống sao lưu bảng trước.</span>
          </label>

          <label for="db-active" class="flex items-center gap-2 text-sm text-gray-700">
            <Checkbox inputId="db-active" v-model="form.isActive" binary /> Kích hoạt kết nối
          </label>
        </form>
        <template #footer>
          <Button label="Hủy" severity="secondary" outlined @click="dialogVisible = false" />
          <Button :label="editingId ? 'Lưu thay đổi' : 'Thêm kết nối'" icon="pi pi-check" :loading="saving" @click="save" />
        </template>
      </Dialog>

      <!-- Sơ đồ schema: bảng/cột/khoá vẽ từ chính phép introspection dựng chỉ mục -->
      <Dialog
        v-model:visible="diagramVisible"
        modal
        :header="`Sơ đồ CSDL — ${diagram?.name ?? ''}`"
        class="w-[70rem] max-w-[96vw]">
        <div v-if="diagramLoading" class="flex items-center justify-center gap-2 py-12 text-sm text-gray-500">
          <i class="pi pi-spin pi-spinner" aria-hidden="true"></i> Đang đọc schema từ CSDL…
        </div>

        <div v-else-if="diagramError" role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {{ diagramError }}
        </div>

        <div v-else-if="diagram" class="grid gap-3">
          <!-- Bối cảnh: kết nối nào, bao nhiêu bảng/quan hệ, đã index hay chưa -->
          <div class="flex flex-wrap items-center gap-x-4 gap-y-2 rounded-lg border border-gray-200 bg-gray-50 px-4 py-2.5 text-xs text-gray-600">
            <span class="flex items-center gap-1.5">
              <i class="pi pi-database text-gray-400" aria-hidden="true"></i>
              <Tag :value="providerLabel(diagram.provider)" severity="info" />
            </span>
            <span><strong class="text-gray-900">{{ diagram.tableCount }}</strong> bảng</span>
            <span><strong class="text-gray-900">{{ diagram.relations.length }}</strong> quan hệ khoá ngoại</span>
            <span class="flex items-center gap-1.5">
              <Tag :value="indexStatusLabel(diagram.indexStatus)" :severity="diagram.isIndexed ? 'success' : 'warn'" />
              <span v-if="diagram.lastIndexedAtUtc">lập chỉ mục {{ relativeTime(diagram.lastIndexedAtUtc) }}</span>
            </span>
          </div>

          <ul v-if="diagramNotesList.length > 0" class="grid gap-1.5 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-xs text-amber-800">
            <li v-for="note in diagramNotesList" :key="note" class="flex items-start gap-2">
              <i class="pi pi-info-circle mt-0.5" aria-hidden="true"></i>
              <span>{{ note }}</span>
            </li>
          </ul>

          <div class="rounded-lg border border-gray-200 bg-white p-3">
            <MermaidDiagram v-if="diagramSource" :source="diagramSource" :aria-label="`Sơ đồ quan hệ của ${diagram.name}`" />
            <p v-else class="p-6 text-center text-sm text-gray-500">Không có bảng nào để vẽ sơ đồ.</p>
          </div>

          <!-- Quan hệ trỏ ra ngoài sơ đồ: phải liệt kê, không được biến mất khỏi màn hình -->
          <details v-if="diagramExternal.length > 0" class="rounded-lg border border-gray-200 bg-white px-4 py-3 text-xs text-gray-600">
            <summary class="cursor-pointer font-medium text-gray-700">
              {{ diagramExternal.length }} khoá ngoại trỏ tới bảng không nằm trong sơ đồ
            </summary>
            <ul class="mt-2 grid gap-1">
              <li v-for="(rel, index) in diagramExternal" :key="index" class="font-mono text-[11px] text-gray-500">
                {{ rel.fromTable }}.{{ rel.fromColumn }} → {{ rel.toTable }}{{ rel.toColumn ? `.${rel.toColumn}` : '' }}
              </li>
            </ul>
          </details>

          <div v-if="diagramSource">
            <div class="flex flex-wrap items-center justify-between gap-2">
              <Button
                :label="showDiagramSource ? 'Ẩn mã Mermaid' : 'Xem mã Mermaid'"
                icon="pi pi-code"
                text
                severity="secondary"
                size="small"
                @click="showDiagramSource = !showDiagramSource" />
              <Button label="Sao chép mã sơ đồ" icon="pi pi-copy" text severity="secondary" size="small" @click="copyDiagramSource" />
            </div>
            <pre v-if="showDiagramSource" class="mt-2 max-h-72 overflow-auto rounded-lg border border-gray-200 bg-gray-50 p-3 text-[11px] leading-relaxed text-gray-700">{{ diagramSource }}</pre>
          </div>
        </div>

        <template #footer>
          <Button label="Đóng" severity="secondary" outlined @click="diagramVisible = false" />
          <Button label="Tải lại" icon="pi pi-refresh" :loading="diagramLoading" @click="diagramConnection && openDiagram(diagramConnection)" />
        </template>
      </Dialog>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { useConfirm } from 'primevue/useconfirm';
import { useToast } from 'primevue/usetoast';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import { useServerPage } from '@/composables/useServerPage';
import MermaidDiagram from '@/components/database/MermaidDiagram.vue';
import { buildErDiagram, diagramNotes, externalReferences, type SchemaDiagram } from '@/utils/schemaDiagram';

interface DatabaseConnection {
  id: string;
  tenantId: string | null;
  tenantName: string | null;
  name: string;
  provider: string;
  isActive: boolean;
  allowWrites: boolean;
  isIndexed: boolean;
  indexStatus: string;
  lastIndexError?: string | null;
  lastIndexedAtUtc?: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}
interface TenantOption { id: string; name: string }

const confirm = useConfirm();
const toast = useToast();

const list = useServerPage<DatabaseConnection>(ApiFactory.DATABASE_CONNECTIONS.BASE, {
  errorLabel: 'danh sách kết nối cơ sở dữ liệu'
});

// value MUST match the backend DbProvider enum names exactly.
const providerOptions = [
  { label: 'PostgreSQL', value: 'Postgres' },
  { label: 'SQLite', value: 'Sqlite' },
  { label: 'MySQL / MariaDB', value: 'MySql' },
  { label: 'SQL Server', value: 'SqlServer' },
  { label: 'Oracle', value: 'Oracle' },
  { label: 'MongoDB', value: 'Mongo' }
];
const connStringSamples: Record<string, string> = {
  Postgres: 'Host=...;Port=5432;Database=...;Username=...;Password=...',
  Sqlite: 'Data Source=/path/to/database.db',
  MySql: 'Server=...;Port=3306;Database=...;User ID=...;Password=...',
  SqlServer: 'Server=...,1433;Database=...;User ID=...;Password=...;Encrypt=True',
  Oracle: 'Data Source=host:1521/service;User ID=...;Password=...',
  Mongo: 'mongodb://user:pass@host:27017/database'
};
const providerPlaceholder = (provider: string) => connStringSamples[provider] ?? connStringSamples.Postgres;
const providerLabel = (provider: string): string =>
  providerOptions.find((o) => o.value === provider)?.label || provider || 'Không rõ';

const indexStatusLabel = (status: string): string => {
  switch (status) {
    case 'Pending': return 'Chờ lập chỉ mục';
    case 'Indexing': return 'Đang lập chỉ mục';
    case 'Completed':
    case 'Indexed': return 'Đã lập chỉ mục';
    case 'Failed': return 'Thất bại';
    default: return status || 'Chưa lập chỉ mục';
  }
};
const indexSeverity = (row: DatabaseConnection): string => {
  if (row.indexStatus === 'Failed') return 'danger';
  if (row.isIndexed) return 'success';
  return 'warn';
};

const relativeTime = (value: string | null | undefined): string => {
  const time = new Date(value ?? NaN).getTime();
  if (Number.isNaN(time)) return '';
  const diffMin = Math.round((Date.now() - time) / 60_000);
  if (diffMin < 1) return 'vừa xong';
  if (diffMin < 60) return `${diffMin} phút trước`;
  const diffHour = Math.round(diffMin / 60);
  if (diffHour < 24) return `${diffHour} giờ trước`;
  const diffDay = Math.round(diffHour / 24);
  if (diffDay < 30) return `${diffDay} ngày trước`;
  return new Date(value as string).toLocaleDateString('vi-VN');
};

// --- Dropdown phạm vi tenant -------------------------------------------------
// Sentinel cho hai lựa chọn đặc biệt; giá trị khác là chính tenant id.
const CURRENT_TENANT = '__current__';
const GLOBAL_TENANT = '__global__';
const tenantsLoading = ref(false);
const tenantOptions = ref<{ label: string; value: string }[]>([
  { label: 'Tenant hiện tại (mặc định)', value: CURRENT_TENANT },
  { label: 'Toàn hệ thống (mọi tenant)', value: GLOBAL_TENANT }
]);

const loadTenantOptions = async () => {
  tenantsLoading.value = true;
  try {
    const response = await http.get(ApiFactory.TENANTS.OPTIONS);
    if (!response.ok) return; // dropdown vẫn dùng được với 2 lựa chọn mặc định
    const tenants = (await response.json()) as TenantOption[];
    tenantOptions.value = [
      { label: 'Tenant hiện tại (mặc định)', value: CURRENT_TENANT },
      { label: 'Toàn hệ thống (mọi tenant)', value: GLOBAL_TENANT },
      ...tenants.map((t) => ({ label: t.name, value: t.id }))
    ];
  } catch {
    /* im lặng: allowlist tenant chỉ là tiện ích chọn nhanh */
  } finally {
    tenantsLoading.value = false;
  }
};

// --- Dialog create/update ----------------------------------------------------
const dialogVisible = ref(false);
const editingId = ref<string | null>(null);
const saving = ref(false);
const formError = ref('');
const emptyForm = () => ({
  name: '',
  provider: 'Postgres',
  connectionString: '',
  isActive: true,
  allowWrites: false,
  tenantSelection: CURRENT_TENANT as string
});
const form = ref(emptyForm());

const openCreate = () => {
  editingId.value = null;
  form.value = emptyForm();
  formError.value = '';
  dialogVisible.value = true;
};

const openEdit = (conn: DatabaseConnection) => {
  editingId.value = conn.id;
  form.value = {
    name: conn.name,
    provider: conn.provider,
    connectionString: '', // secret chỉ-ghi: trống = giữ nguyên
    isActive: conn.isActive,
    allowWrites: conn.allowWrites,
    tenantSelection: conn.tenantId === null ? GLOBAL_TENANT : conn.tenantId
  };
  formError.value = '';
  dialogVisible.value = true;
};

const save = async () => {
  formError.value = '';
  const name = form.value.name.trim();
  const connectionString = form.value.connectionString.trim();
  if (!name) { formError.value = 'Vui lòng nhập tên kết nối.'; return; }
  if (!editingId.value && !connectionString) { formError.value = 'Vui lòng nhập chuỗi kết nối.'; return; }

  const selection = form.value.tenantSelection;
  const payload: Record<string, unknown> = {
    name,
    provider: form.value.provider,
    isActive: form.value.isActive,
    allowWrites: form.value.allowWrites,
    isGlobal: selection === GLOBAL_TENANT,
    tenantId: selection === GLOBAL_TENANT || selection === CURRENT_TENANT ? null : selection
  };

  saving.value = true;
  try {
    let response: Response;
    if (editingId.value) {
      const replaceConnectionString = connectionString.length > 0;
      response = await http.put(ApiFactory.DATABASE_CONNECTIONS.BY_ID(editingId.value), {
        ...payload,
        replaceConnectionString,
        connectionString: replaceConnectionString ? connectionString : undefined
      });
    } else {
      response = await http.post(ApiFactory.DATABASE_CONNECTIONS.BASE, { ...payload, connectionString });
    }
    if (!response.ok) {
      formError.value = await readApiError(response, 'Không thể lưu kết nối cơ sở dữ liệu');
      return;
    }
    dialogVisible.value = false;
    toast.add({ severity: 'success', summary: editingId.value ? 'Đã cập nhật kết nối' : 'Đã thêm kết nối', life: 3000 });
    await list.reload();
  } catch (cause) {
    formError.value = errorMessage(cause, 'Không thể lưu kết nối cơ sở dữ liệu.');
  } finally {
    saving.value = false;
  }
};

// --- Hành động dòng ----------------------------------------------------------
const testingId = ref<string | null>(null);
const reindexingId = ref<string | null>(null);
const deletingId = ref<string | null>(null);

const testConnection = async (conn: DatabaseConnection) => {
  testingId.value = conn.id;
  try {
    const response = await http.post(ApiFactory.DATABASE_CONNECTIONS.TEST(conn.id));
    if (response.ok) {
      toast.add({ severity: 'success', summary: 'Kết nối thành công', detail: conn.name, life: 3000 });
    } else {
      toast.add({ severity: 'error', summary: 'Kiểm tra thất bại', detail: await readApiError(response, 'Kiểm tra kết nối thất bại'), life: 6000 });
    }
  } catch (cause) {
    toast.add({ severity: 'error', summary: 'Kiểm tra thất bại', detail: errorMessage(cause, 'Kiểm tra kết nối thất bại.'), life: 6000 });
  } finally {
    testingId.value = null;
  }
};

const reindexConnection = async (conn: DatabaseConnection) => {
  reindexingId.value = conn.id;
  try {
    const response = await http.post(ApiFactory.DATABASE_CONNECTIONS.REINDEX(conn.id));
    if (response.ok || response.status === 202) {
      toast.add({ severity: 'info', summary: 'Đã xếp hàng lập chỉ mục lại', detail: 'Worker nền sẽ xử lý.', life: 3000 });
      await list.reload();
    } else {
      toast.add({ severity: 'error', summary: 'Không thể lập chỉ mục lại', detail: await readApiError(response, 'Không thể yêu cầu lập chỉ mục lại'), life: 6000 });
    }
  } catch (cause) {
    toast.add({ severity: 'error', summary: 'Không thể lập chỉ mục lại', detail: errorMessage(cause, 'Không thể yêu cầu lập chỉ mục lại.'), life: 6000 });
  } finally {
    reindexingId.value = null;
  }
};

const confirmDelete = (conn: DatabaseConnection) => {
  confirm.require({
    header: 'Xóa kết nối CSDL',
    message: `Xóa kết nối "${conn.name}"? Chỉ mục schema trong Qdrant sẽ không còn được agent sử dụng.`,
    icon: 'pi pi-exclamation-triangle',
    acceptLabel: 'Xóa',
    rejectLabel: 'Hủy',
    acceptProps: { severity: 'danger' },
    rejectProps: { severity: 'secondary', outlined: true },
    accept: () => { void performDelete(conn); }
  });
};

const performDelete = async (conn: DatabaseConnection) => {
  deletingId.value = conn.id;
  try {
    const response = await http.delete(ApiFactory.DATABASE_CONNECTIONS.BY_ID(conn.id));
    if (response.ok) {
      toast.add({ severity: 'success', summary: 'Đã xóa kết nối', detail: conn.name, life: 3000 });
      await list.reload();
    } else {
      toast.add({ severity: 'error', summary: 'Không thể xóa', detail: await readApiError(response, 'Không thể xóa kết nối cơ sở dữ liệu'), life: 6000 });
    }
  } catch (cause) {
    toast.add({ severity: 'error', summary: 'Không thể xóa', detail: errorMessage(cause, 'Không thể xóa kết nối cơ sở dữ liệu.'), life: 6000 });
  } finally {
    deletingId.value = null;
  }
};

// --- Sơ đồ schema ----------------------------------------------------------
// Đọc sống qua /schema: đúng phép introspection mà chỉ mục schema dùng, đi qua egress
// + read-only như mọi thao tác CSDL khác. Mermaid chỉ được tải khi thực sự mở sơ đồ.
const diagramVisible = ref(false);
const diagramLoading = ref(false);
const diagramError = ref('');
const diagram = ref<SchemaDiagram | null>(null);
const diagramConnection = ref<DatabaseConnection | null>(null);
const showDiagramSource = ref(false);

const diagramSource = computed(() => (diagram.value ? buildErDiagram(diagram.value) : ''));
const diagramNotesList = computed(() => (diagram.value ? diagramNotes(diagram.value) : []));
const diagramExternal = computed(() => (diagram.value ? externalReferences(diagram.value) : []));

const openDiagram = async (conn: DatabaseConnection) => {
  diagramConnection.value = conn;
  diagramVisible.value = true;
  diagramError.value = '';
  diagram.value = null;
  showDiagramSource.value = false;
  diagramLoading.value = true;
  try {
    const response = await http.get(ApiFactory.DATABASE_CONNECTIONS.SCHEMA(conn.id));
    if (!response.ok) {
      diagramError.value = await readApiError(response, 'Không đọc được schema của kết nối');
      return;
    }
    diagram.value = (await response.json()) as SchemaDiagram;
  } catch (cause) {
    diagramError.value = errorMessage(cause, 'Không đọc được schema của kết nối.');
  } finally {
    diagramLoading.value = false;
  }
};

const copyDiagramSource = async () => {
  try {
    await navigator.clipboard.writeText(diagramSource.value);
    toast.add({ severity: 'success', summary: 'Đã sao chép mã sơ đồ', life: 2500 });
  } catch {
    toast.add({ severity: 'error', summary: 'Không sao chép được', detail: 'Hãy mở mã sơ đồ và sao chép thủ công.', life: 4000 });
  }
};

onMounted(() => {
  void list.load();
  void loadTenantOptions();
});
</script>
