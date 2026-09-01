<template>
  <div class="flex-1 overflow-y-auto bg-gray-50 p-4 md:p-6">
    <div class="max-w-5xl mx-auto">
      <!-- Page header -->
      <header class="mb-4 flex items-center gap-4">
        <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-cyan-500 to-sky-600 flex items-center justify-center shadow-md shadow-cyan-500/20 flex-shrink-0">
          <i class="pi pi-server text-white text-sm" aria-hidden="true"></i>
        </div>
        <div>
          <h1 class="text-xl font-bold text-gray-900 tracking-tight">Kết nối cơ sở dữ liệu</h1>
          <p class="text-sm text-gray-500">
            Quản lý kết nối tới cơ sở dữ liệu bên ngoài để agent truy vấn dữ liệu về sau.
          </p>
        </div>
      </header>

      <!-- Safety note (muted) -->
      <div class="mb-6 flex items-start gap-2.5 rounded-xl border border-gray-200 bg-gray-100/70 px-4 py-3 text-xs leading-relaxed text-gray-600">
        <i class="pi pi-shield mt-0.5 text-gray-400" aria-hidden="true"></i>
        <p>
          Agent chỉ truy vấn CHỈ-ĐỌC. Chuỗi kết nối được mã hoá và không bao giờ hiển thị lại. Hãy dùng tài khoản DB quyền chỉ-đọc.
          Công cụ truy vấn cơ sở dữ liệu vẫn tắt cho đến khi người vận hành bật.
        </p>
      </div>

      <!-- Create form -->
      <section aria-labelledby="db-create-heading" class="mb-6">
        <h2 id="db-create-heading" class="text-sm font-semibold text-gray-900 mb-2">Thêm kết nối mới</h2>
        <form @submit.prevent="createConnection" class="grid gap-3 p-4 bg-white border border-gray-200 rounded-xl">
          <div v-if="createError" role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
            {{ createError }}
          </div>
          <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
            <div class="grid gap-1">
              <label for="db-create-name" class="text-sm font-medium text-gray-700">Tên kết nối</label>
              <InputText id="db-create-name" v-model="createForm.name" required placeholder="Ví dụ kho-báo-cáo" class="w-full" />
            </div>
            <div class="grid gap-1">
              <label for="db-create-provider" class="text-sm font-medium text-gray-700">Loại CSDL</label>
              <Select
                v-model="createForm.provider"
                :options="providerOptions"
                optionLabel="label"
                optionValue="value"
                inputId="db-create-provider"
                aria-label="Loại cơ sở dữ liệu"
                class="w-full"
              />
            </div>
          </div>
          <div class="grid gap-1">
            <label for="db-create-connstr" class="text-sm font-medium text-gray-700">Chuỗi kết nối</label>
            <Textarea
              id="db-create-connstr"
              v-model="createForm.connectionString"
              rows="3"
              required
              :placeholder="providerPlaceholder(createForm.provider)"
              class="w-full font-mono"
              aria-describedby="db-create-connstr-help"
            />
            <p id="db-create-connstr-help" class="flex items-start gap-1.5 text-xs text-amber-700">
              <i class="pi pi-exclamation-triangle mt-0.5" aria-hidden="true"></i>
              <span>Hãy dùng tài khoản chỉ có quyền đọc (read-only). Chuỗi kết nối sẽ được mã hoá và không hiển thị lại.</span>
            </p>
          </div>
          <label for="db-create-writes" class="flex items-start gap-2 text-sm text-amber-800 rounded-lg border border-amber-200 bg-amber-50 p-3">
            <Checkbox inputId="db-create-writes" v-model="createForm.allowWrites" binary />
            <span>Cho phép ghi (INSERT/UPDATE/DELETE). Mặc định TẮT — nên để tắt và dùng tài khoản chỉ-đọc. Khi bật, lệnh ghi vẫn LUÔN cần bạn phê duyệt và hệ thống sao lưu bảng trước.</span>
          </label>
          <div class="flex flex-wrap items-center justify-between gap-3">
            <label for="db-create-active" class="flex items-center gap-2 text-sm text-gray-700">
              <Checkbox inputId="db-create-active" v-model="createForm.isActive" binary /> Kích hoạt
            </label>
            <Button
              type="submit"
              label="Thêm kết nối"
              icon="pi pi-plus"
              :loading="creating"
              class="!min-h-11 !px-4 !rounded-xl !text-sm !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800"
            />
          </div>
        </form>
      </section>

      <!-- Connection list -->
      <section aria-labelledby="db-list-heading">
        <h2 id="db-list-heading" class="text-sm font-semibold text-gray-900 mb-2">Kết nối đã cấu hình</h2>

        <div v-if="pageError" role="alert" class="mb-4 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {{ pageError }}
        </div>

        <div v-if="loading" class="flex flex-col items-center justify-center py-16 text-gray-500" role="status">
          <i class="pi pi-spin pi-spinner text-2xl mb-3" aria-hidden="true"></i>
          <p class="text-sm">Đang tải danh sách kết nối cơ sở dữ liệu...</p>
        </div>

        <div v-else-if="connections.length === 0" class="p-8 border border-gray-200 rounded-xl bg-white text-center">
          <i class="pi pi-database text-3xl text-gray-300 mb-3" aria-hidden="true"></i>
          <p class="text-gray-500 text-sm">Chưa có kết nối cơ sở dữ liệu nào.</p>
        </div>

        <div v-else class="grid gap-2">
          <div
            v-for="conn in connections"
            :key="conn.id"
            class="p-4 bg-white border border-gray-200 rounded-xl"
          >
            <div class="flex flex-wrap items-start justify-between gap-3">
              <div class="min-w-0 flex-1 basis-64">
                <div class="text-sm font-semibold text-gray-900 truncate">{{ conn.name }}</div>
                <div class="flex flex-wrap gap-1.5 mt-2">
                  <span class="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-semibold border bg-sky-50 text-sky-800 border-sky-200">
                    {{ providerLabel(conn.provider) }}
                  </span>
                  <span
                    class="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-semibold border"
                    :class="conn.isActive ? 'bg-emerald-50 text-emerald-900 border-emerald-200' : 'bg-gray-50 text-gray-600 border-gray-200'"
                  >
                    {{ conn.isActive ? 'Đang hoạt động' : 'Đã tắt' }}
                  </span>
                  <span
                    class="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-semibold border"
                    :class="conn.allowWrites ? 'bg-amber-50 text-amber-800 border-amber-200' : 'bg-gray-50 text-gray-600 border-gray-200'"
                  >
                    {{ conn.allowWrites ? 'Cho phép ghi (cần duyệt)' : 'Chỉ đọc' }}
                  </span>
                  <span
                    class="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-semibold border"
                    :class="conn.isIndexed ? 'bg-emerald-50 text-emerald-900 border-emerald-200' : 'bg-gray-50 text-gray-600 border-gray-200'"
                  >
                    {{ conn.isIndexed ? 'Đã lập chỉ mục' : indexStatusLabel(conn.indexStatus) }}
                  </span>
                </div>
                <p v-if="conn.lastIndexError" class="text-xs text-red-600 mt-1.5 break-words">
                  Lỗi lập chỉ mục: {{ conn.lastIndexError }}
                </p>
                <p class="text-xs text-gray-500 mt-1.5">
                  Tạo <time :datetime="conn.createdAtUtc" :title="absoluteTime(conn.createdAtUtc)">{{ relativeTime(conn.createdAtUtc) }}</time>
                  · Cập nhật <time :datetime="conn.updatedAtUtc" :title="absoluteTime(conn.updatedAtUtc)">{{ relativeTime(conn.updatedAtUtc) }}</time>
                  <template v-if="conn.isIndexed && conn.lastIndexedAtUtc">
                    · Lập chỉ mục <time :datetime="conn.lastIndexedAtUtc" :title="absoluteTime(conn.lastIndexedAtUtc)">{{ relativeTime(conn.lastIndexedAtUtc) }}</time>
                  </template>
                </p>
              </div>
              <div class="flex items-center gap-2 flex-shrink-0">
                <Button
                  label="Kiểm tra kết nối"
                  icon="pi pi-bolt"
                  outlined
                  severity="secondary"
                  :loading="testingId === conn.id"
                  :disabled="deletingId !== null || (testingId !== null && testingId !== conn.id)"
                  :aria-label="`Kiểm tra kết nối ${conn.name}`"
                  class="!min-h-11 !px-3 !rounded-xl !text-sm"
                  @click="testConnection(conn)"
                />
                <Button
                  label="Lập chỉ mục lại"
                  icon="pi pi-refresh"
                  outlined
                  severity="secondary"
                  :loading="reindexingId === conn.id"
                  :disabled="deletingId !== null || testingId !== null || (reindexingId !== null && reindexingId !== conn.id)"
                  :aria-label="`Lập chỉ mục lại ${conn.name}`"
                  class="!min-h-11 !px-3 !rounded-xl !text-sm"
                  @click="reindexConnection(conn)"
                />
                <Button
                  label="Sửa"
                  icon="pi pi-pencil"
                  outlined
                  :disabled="deletingId !== null || testingId !== null"
                  :aria-label="`Sửa kết nối ${conn.name}`"
                  class="!min-h-11 !px-3 !rounded-xl !text-sm"
                  @click="openEdit(conn)"
                />
                <Button
                  icon="pi pi-trash"
                  severity="danger"
                  text
                  rounded
                  :loading="deletingId === conn.id"
                  :disabled="testingId !== null || (deletingId !== null && deletingId !== conn.id)"
                  :aria-label="`Xóa kết nối ${conn.name}`"
                  class="!w-11 !h-11"
                  @click="deleteConnection(conn)"
                />
              </div>
            </div>

            <div
              v-if="testingId !== conn.id && testResults[conn.id]"
              role="status"
              class="mt-3 flex items-center gap-2 rounded-lg border px-3 py-2 text-xs"
              :class="testResults[conn.id]?.success ? 'bg-emerald-50 text-emerald-800 border-emerald-200' : 'bg-red-50 text-red-700 border-red-200'"
            >
              <i :class="testResults[conn.id]?.success ? 'pi pi-check-circle' : 'pi pi-times-circle'" aria-hidden="true"></i>
              <span>{{ testResults[conn.id]?.message }}</span>
            </div>
          </div>
        </div>
      </section>
    </div>

    <!-- Edit dialog -->
    <Dialog
      v-model:visible="editVisible"
      modal
      header="Chỉnh sửa kết nối cơ sở dữ liệu"
      aria-label="Chỉnh sửa kết nối cơ sở dữ liệu"
      :style="{ width: '520px' }"
      :breakpoints="{ '575px': '92vw' }"
    >
      <form @submit.prevent="saveEdit" class="grid gap-4 pt-1">
        <div v-if="editError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
          {{ editError }}
        </div>

        <div class="grid gap-1">
          <label for="db-edit-name" class="text-sm font-medium text-gray-700">Tên kết nối</label>
          <InputText id="db-edit-name" v-model="editForm.name" required class="w-full" />
        </div>

        <div class="grid gap-1">
          <label for="db-edit-provider" class="text-sm font-medium text-gray-700">Loại CSDL</label>
          <Select
            v-model="editForm.provider"
            :options="providerOptions"
            optionLabel="label"
            optionValue="value"
            inputId="db-edit-provider"
            aria-label="Loại cơ sở dữ liệu"
            class="w-full"
          />
        </div>

        <div class="grid gap-1">
          <label for="db-edit-connstr" class="text-sm font-medium text-gray-700">Chuỗi kết nối</label>
          <Textarea
            id="db-edit-connstr"
            v-model="editForm.connectionString"
            rows="3"
            :placeholder="providerPlaceholder(editForm.provider)"
            class="w-full font-mono"
            aria-describedby="db-edit-connstr-help"
          />
          <p id="db-edit-connstr-help" class="text-xs text-gray-500">
            Để trống = giữ nguyên chuỗi kết nối hiện tại. Nếu nhập, hãy dùng tài khoản chỉ-đọc.
          </p>
        </div>

        <label for="db-edit-active" class="flex items-center gap-2 text-sm text-gray-700">
          <Checkbox inputId="db-edit-active" v-model="editForm.isActive" binary /> Kích hoạt
        </label>
        <label for="db-edit-writes" class="flex items-start gap-2 text-sm text-amber-800 rounded-lg border border-amber-200 bg-amber-50 p-3">
          <Checkbox inputId="db-edit-writes" v-model="editForm.allowWrites" binary />
          <span>Cho phép ghi (INSERT/UPDATE/DELETE) — vẫn cần phê duyệt + sao lưu trước.</span>
        </label>

        <div class="flex items-center justify-end gap-2 pt-1">
          <Button type="button" label="Hủy" text severity="secondary" :disabled="savingEdit" class="!min-h-11 !px-4 !rounded-xl !text-sm" @click="editVisible = false" />
          <Button type="submit" label="Lưu thay đổi" icon="pi pi-check" :loading="savingEdit" class="!min-h-11 !px-4 !rounded-xl !text-sm !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800" />
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

interface DatabaseConnection {
  id: string;
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
interface TestResult {
  success: boolean;
  message: string;
}

// value MUST match the backend DbProvider enum names exactly.
const providerOptions = [
  { label: 'PostgreSQL', value: 'Postgres' },
  { label: 'SQLite', value: 'Sqlite' },
  { label: 'MySQL / MariaDB', value: 'MySql' },
  { label: 'SQL Server', value: 'SqlServer' },
  { label: 'Oracle', value: 'Oracle' },
  { label: 'MongoDB', value: 'Mongo' }
];

// A representative connection string per engine, shown as the textarea placeholder.
const connStringSamples: Record<string, string> = {
  Postgres: 'Host=...;Port=5432;Database=...;Username=...;Password=...',
  Sqlite: 'Data Source=/path/to/database.db',
  MySql: 'Server=...;Port=3306;Database=...;User ID=...;Password=...',
  SqlServer: 'Server=...,1433;Database=...;User ID=...;Password=...;Encrypt=True',
  Oracle: 'Data Source=host:1521/service;User ID=...;Password=...',
  Mongo: 'mongodb://user:pass@host:27017/database'
};
const providerPlaceholder = (provider: string) => connStringSamples[provider] ?? connStringSamples.Postgres;

const connections = ref<DatabaseConnection[]>([]);
const loading = ref(false);
const pageError = ref('');

const creating = ref(false);
const createError = ref('');
const emptyCreateForm = () => ({ name: '', provider: 'Postgres', connectionString: '', isActive: true, allowWrites: false });
const createForm = ref(emptyCreateForm());

const editVisible = ref(false);
const savingEdit = ref(false);
const editError = ref('');
const editForm = ref({ id: '', name: '', provider: 'Postgres', connectionString: '', isActive: true, allowWrites: false });

const deletingId = ref<string | null>(null);
const testingId = ref<string | null>(null);
const reindexingId = ref<string | null>(null);
const testResults = ref<Record<string, TestResult>>({});

const absoluteTime = (value: string | null | undefined): string => {
  const date = new Date(value ?? NaN);
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleString('vi-VN');
};

const relativeTime = (value: string | null | undefined): string => {
  const time = new Date(value ?? NaN).getTime();
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

const providerLabel = (provider: string): string =>
  providerOptions.find((o) => o.value === provider)?.label || provider || 'Không rõ';

const indexStatusLabel = (status: string): string => {
  switch (status) {
    case 'Pending':
      return 'Chờ lập chỉ mục';
    case 'Indexing':
      return 'Đang lập chỉ mục';
    case 'Indexed':
      return 'Đã lập chỉ mục';
    case 'Failed':
      return 'Lập chỉ mục thất bại';
    case 'NotIndexed':
      return 'Chưa lập chỉ mục';
    default:
      return status || 'Chưa lập chỉ mục';
  }
};

const loadConnections = async () => {
  loading.value = connections.value.length === 0;
  pageError.value = '';
  try {
    const response = await http.get(ApiFactory.DATABASE_CONNECTIONS.BASE);
    if (response.ok) connections.value = await response.json();
    else pageError.value = await readApiError(response, 'Không thể tải danh sách kết nối cơ sở dữ liệu');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể tải danh sách kết nối cơ sở dữ liệu.');
  } finally {
    loading.value = false;
  }
};

const createConnection = async () => {
  createError.value = '';
  const name = createForm.value.name.trim();
  const connectionString = createForm.value.connectionString.trim();
  if (!name || !connectionString) {
    createError.value = 'Vui lòng nhập tên kết nối và chuỗi kết nối.';
    return;
  }
  creating.value = true;
  try {
    const response = await http.post(ApiFactory.DATABASE_CONNECTIONS.BASE, {
      name,
      provider: createForm.value.provider,
      connectionString,
      isActive: createForm.value.isActive,
      allowWrites: createForm.value.allowWrites
    });
    if (!response.ok) {
      createError.value = await readApiError(response, 'Không thể thêm kết nối cơ sở dữ liệu');
      return;
    }
    createForm.value = emptyCreateForm();
    await loadConnections();
  } catch (cause) {
    createError.value = errorMessage(cause, 'Không thể thêm kết nối cơ sở dữ liệu.');
  } finally {
    creating.value = false;
  }
};

const openEdit = (conn: DatabaseConnection) => {
  // The connection string is never returned by the API, so the field starts
  // EMPTY: leaving it blank preserves the stored string on save.
  editForm.value = {
    id: conn.id,
    name: conn.name,
    provider: conn.provider,
    connectionString: '',
    isActive: conn.isActive,
    allowWrites: conn.allowWrites
  };
  editError.value = '';
  editVisible.value = true;
};

const saveEdit = async () => {
  editError.value = '';
  const name = editForm.value.name.trim();
  if (!name) {
    editError.value = 'Vui lòng nhập tên kết nối.';
    return;
  }
  const connectionString = editForm.value.connectionString.trim();
  const replaceConnectionString = connectionString.length > 0;
  savingEdit.value = true;
  try {
    const response = await http.put(ApiFactory.DATABASE_CONNECTIONS.BY_ID(editForm.value.id), {
      name,
      provider: editForm.value.provider,
      isActive: editForm.value.isActive,
      allowWrites: editForm.value.allowWrites,
      replaceConnectionString,
      connectionString: replaceConnectionString ? connectionString : undefined
    });
    if (!response.ok) {
      editError.value = await readApiError(response, 'Không thể cập nhật kết nối cơ sở dữ liệu');
      return;
    }
    editVisible.value = false;
    await loadConnections();
  } catch (cause) {
    editError.value = errorMessage(cause, 'Không thể cập nhật kết nối cơ sở dữ liệu.');
  } finally {
    savingEdit.value = false;
  }
};

const testConnection = async (conn: DatabaseConnection) => {
  // The previous result stays hidden while testing (the template gates it on
  // testingId), and every branch below writes a fresh result, so there is no
  // stale outcome to clear up front.
  testingId.value = conn.id;
  try {
    const response = await http.post(ApiFactory.DATABASE_CONNECTIONS.TEST(conn.id));
    if (response.ok) {
      testResults.value = { ...testResults.value, [conn.id]: { success: true, message: 'Kết nối thành công.' } };
    } else {
      const message = await readApiError(response, 'Kiểm tra kết nối thất bại');
      testResults.value = { ...testResults.value, [conn.id]: { success: false, message } };
    }
  } catch (cause) {
    testResults.value = {
      ...testResults.value,
      [conn.id]: { success: false, message: errorMessage(cause, 'Kiểm tra kết nối thất bại.') }
    };
  } finally {
    testingId.value = null;
  }
};

const reindexConnection = async (conn: DatabaseConnection) => {
  reindexingId.value = conn.id;
  try {
    const response = await http.post(ApiFactory.DATABASE_CONNECTIONS.REINDEX(conn.id));
    if (response.ok || response.status === 202) {
      testResults.value = { ...testResults.value, [conn.id]: { success: true, message: 'Đã yêu cầu lập chỉ mục lại; đang chạy nền.' } };
      await loadConnections();
    } else {
      const message = await readApiError(response, 'Không thể yêu cầu lập chỉ mục lại');
      testResults.value = { ...testResults.value, [conn.id]: { success: false, message } };
    }
  } catch (cause) {
    testResults.value = {
      ...testResults.value,
      [conn.id]: { success: false, message: errorMessage(cause, 'Không thể yêu cầu lập chỉ mục lại.') }
    };
  } finally {
    reindexingId.value = null;
  }
};

const deleteConnection = async (conn: DatabaseConnection) => {
  if (!confirm(`Xóa kết nối cơ sở dữ liệu "${conn.name}"?`)) return;
  deletingId.value = conn.id;
  pageError.value = '';
  try {
    const response = await http.delete(ApiFactory.DATABASE_CONNECTIONS.BY_ID(conn.id));
    if (response.ok) connections.value = connections.value.filter((c) => c.id !== conn.id);
    else pageError.value = await readApiError(response, 'Không thể xóa kết nối cơ sở dữ liệu');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể xóa kết nối cơ sở dữ liệu.');
  } finally {
    deletingId.value = null;
  }
};

onMounted(() => {
  void loadConnections();
});
</script>
