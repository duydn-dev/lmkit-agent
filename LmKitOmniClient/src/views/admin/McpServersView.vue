<template>
  <div class="flex-1 overflow-y-auto bg-gray-50 p-4 md:p-6">
    <div class="max-w-5xl mx-auto">
      <!-- Page header -->
      <header class="mb-6 flex items-center gap-4">
        <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-cyan-500 to-sky-600 flex items-center justify-center shadow-md shadow-cyan-500/20 flex-shrink-0">
          <i class="pi pi-server text-white text-sm" aria-hidden="true"></i>
        </div>
        <div>
          <h1 class="text-xl font-bold text-gray-900 tracking-tight">Máy chủ MCP</h1>
          <p class="text-sm text-gray-500">
            Kết nối MCP Streamable HTTP theo tenant. Header bí mật được mã hóa và không bao giờ trả lại giao diện.
          </p>
        </div>
      </header>

      <!-- Create form -->
      <section aria-labelledby="mcp-create-heading" class="mb-6">
        <h2 id="mcp-create-heading" class="text-sm font-semibold text-gray-900 mb-2">Thêm máy chủ mới</h2>
        <form @submit.prevent="createServer" class="grid gap-3 p-4 bg-white border border-gray-200 rounded-xl">
          <div v-if="createError" role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
            {{ createError }}
          </div>
          <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
            <div class="grid gap-1">
              <label for="mcp-create-name" class="text-sm font-medium text-gray-700">Tên máy chủ</label>
              <InputText id="mcp-create-name" v-model="createForm.name" required placeholder="Ví dụ crm-tools" class="w-full" />
            </div>
            <div class="grid gap-1">
              <label for="mcp-create-url" class="text-sm font-medium text-gray-700">URL máy chủ</label>
              <InputText id="mcp-create-url" v-model="createForm.url" required type="url" placeholder="https://mcp.example.com" class="w-full" />
            </div>
          </div>
          <div class="grid gap-1">
            <label for="mcp-create-headers" class="text-sm font-medium text-gray-700">Header JSON tùy chọn</label>
            <Textarea id="mcp-create-headers" v-model="createForm.headersJson" rows="3" placeholder='Ví dụ {"Authorization":"Bearer ..."}' class="w-full" />
          </div>
          <div class="grid gap-1">
            <label for="mcp-create-authmode" class="text-sm font-medium text-gray-700">Phương thức xác thực</label>
            <Select
              inputId="mcp-create-authmode"
              v-model="createForm.authMode"
              :options="authModeOptions"
              optionLabel="label"
              optionValue="value"
              aria-label="Phương thức xác thực máy chủ MCP"
              class="w-full"
            />
          </div>
          <fieldset v-if="createForm.authMode === 'ClientCredentials'" class="grid gap-3 rounded-lg border border-indigo-200 bg-indigo-50/60 p-3">
            <legend class="px-1 text-xs font-semibold text-indigo-900">OAuth 2.0 Client Credentials</legend>
            <p class="text-xs text-indigo-900">Hệ thống tự lấy access token từ Token URL và gắn vào header <code>Authorization</code>. Client secret được mã hóa và không bao giờ trả lại giao diện.</p>
            <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
              <div class="grid gap-1">
                <label for="mcp-create-clientid" class="text-sm font-medium text-gray-700">Client ID</label>
                <InputText id="mcp-create-clientid" v-model="createForm.oauthClientId" class="w-full" autocomplete="off" />
              </div>
              <div class="grid gap-1">
                <label for="mcp-create-clientsecret" class="text-sm font-medium text-gray-700">Client Secret</label>
                <InputText id="mcp-create-clientsecret" v-model="createForm.oauthClientSecret" type="password" class="w-full" autocomplete="off" />
              </div>
            </div>
            <div class="grid gap-1">
              <label for="mcp-create-tokenurl" class="text-sm font-medium text-gray-700">Token URL</label>
              <InputText id="mcp-create-tokenurl" v-model="createForm.oauthTokenUrl" type="url" placeholder="https://issuer.example.com/oauth/token" class="w-full" />
            </div>
            <div class="grid gap-1">
              <label for="mcp-create-scopes" class="text-sm font-medium text-gray-700">Scopes (tùy chọn)</label>
              <InputText id="mcp-create-scopes" v-model="createForm.oauthScopes" placeholder="read:tools write:tools" class="w-full" />
            </div>
          </fieldset>
          <label for="mcp-create-trust" class="flex items-start gap-2 text-sm text-amber-800 rounded-lg border border-amber-200 bg-amber-50 p-3">
            <Checkbox inputId="mcp-create-trust" v-model="createForm.trustReadOnlyAnnotations" binary />
            <span>Tin cậy khai báo <code>readOnlyHint</code> của máy chủ này. Chỉ bật khi đã xác minh nhà cung cấp; nếu tắt, mọi MCP tool đều cần phê duyệt.</span>
          </label>
          <div class="flex flex-wrap items-center justify-between gap-3">
            <label for="mcp-create-active" class="flex items-center gap-2 text-sm text-gray-700">
              <Checkbox inputId="mcp-create-active" v-model="createForm.isActive" binary /> Kích hoạt
            </label>
            <Button
              type="submit"
              label="Thêm máy chủ"
              icon="pi pi-plus"
              :loading="creating"
              class="!min-h-11 !px-4 !rounded-xl !text-sm !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800"
            />
          </div>
        </form>
      </section>

      <!-- Catalog quick-add (hidden when the catalog is empty or failed to load) -->
      <section v-if="catalog.length > 0" aria-labelledby="mcp-catalog-heading" class="mb-6">
        <h2 id="mcp-catalog-heading" class="text-sm font-semibold text-gray-900 mb-2">Gợi ý kết nối</h2>
        <div class="grid gap-2 sm:grid-cols-2">
          <div v-for="entry in catalog" :key="entry.name" class="flex items-center justify-between gap-3 p-3 bg-white border border-gray-200 rounded-xl">
            <div class="min-w-0">
              <div class="text-sm font-medium text-gray-900 truncate">{{ entry.name }}</div>
              <div class="text-xs text-gray-500 truncate">{{ entry.description }}</div>
              <div class="text-xs text-gray-500 truncate">{{ entry.baseUrl }}</div>
            </div>
            <Button
              type="button"
              label="Điền nhanh"
              icon="pi pi-bolt"
              outlined
              :aria-label="`Điền nhanh ${entry.name} vào biểu mẫu`"
              class="!min-h-11 !px-3 !rounded-xl !text-sm flex-shrink-0"
              @click="applyCatalogEntry(entry)"
            />
          </div>
        </div>
        <p class="text-xs text-gray-500 mt-2">Điền nhanh chỉ nhập sẵn tên và URL vào biểu mẫu phía trên — bạn vẫn xem lại rồi bấm "Thêm máy chủ".</p>
      </section>

      <!-- Server list -->
      <section aria-labelledby="mcp-list-heading">
        <h2 id="mcp-list-heading" class="text-sm font-semibold text-gray-900 mb-2">Máy chủ đã kết nối</h2>

        <div v-if="pageError" role="alert" class="mb-4 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {{ pageError }}
        </div>

        <div v-if="loading" class="flex flex-col items-center justify-center py-16 text-gray-500" role="status">
          <i class="pi pi-spin pi-spinner text-2xl mb-3" aria-hidden="true"></i>
          <p class="text-sm">Đang tải danh sách máy chủ MCP...</p>
        </div>

        <div v-else-if="servers.length === 0" class="p-8 border border-gray-200 rounded-xl bg-white text-center">
          <i class="pi pi-database text-3xl text-gray-300 mb-3" aria-hidden="true"></i>
          <p class="text-gray-500 text-sm">Chưa có máy chủ MCP nào được kết nối.</p>
        </div>

        <div v-else class="grid gap-2">
          <div
            v-for="server in servers"
            :key="server.id"
            class="flex flex-wrap items-center justify-between gap-3 p-4 bg-white border border-gray-200 rounded-xl"
          >
            <div class="min-w-0 flex-1 basis-64">
              <div class="text-sm font-semibold text-gray-900 truncate">{{ server.name }}</div>
              <div class="text-xs text-gray-500 truncate">{{ server.url }}</div>
              <div class="flex flex-wrap gap-1.5 mt-2">
                <span class="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-semibold border" :class="server.isActive ? 'bg-emerald-50 text-emerald-900 border-emerald-200' : 'bg-gray-50 text-gray-600 border-gray-200'">
                  {{ server.isActive ? 'Đang hoạt động' : 'Đã tắt' }}
                </span>
                <span class="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-semibold border" :class="server.hasHeaders ? 'bg-sky-50 text-sky-800 border-sky-200' : 'bg-gray-50 text-gray-600 border-gray-200'">
                  {{ server.hasHeaders ? 'Có header bảo mật' : 'Không có header' }}
                </span>
                <span class="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-semibold border" :class="server.trustReadOnlyAnnotations ? 'bg-amber-50 text-amber-800 border-amber-200' : 'bg-gray-50 text-gray-600 border-gray-200'">
                  {{ server.trustReadOnlyAnnotations ? 'Tin cậy read-only' : 'Mọi tool cần duyệt' }}
                </span>
                <span v-if="server.authMode === 'ClientCredentials'" class="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-semibold border bg-indigo-50 text-indigo-800 border-indigo-200">
                  OAuth Client Credentials
                </span>
              </div>
            </div>
            <div class="flex items-center gap-2 flex-shrink-0">
              <Button
                label="Sửa"
                icon="pi pi-pencil"
                outlined
                :disabled="togglingId !== null || deletingId !== null"
                :aria-label="`Sửa máy chủ MCP ${server.name}`"
                class="!min-h-11 !px-3 !rounded-xl !text-sm"
                @click="openEdit(server)"
              />
              <Button
                :label="server.isActive ? 'Tắt' : 'Bật'"
                :icon="server.isActive ? 'pi pi-pause' : 'pi pi-play'"
                outlined
                severity="secondary"
                :loading="togglingId === server.id"
                :disabled="(togglingId !== null && togglingId !== server.id) || deletingId !== null"
                :aria-label="`${server.isActive ? 'Tắt' : 'Bật'} máy chủ MCP ${server.name}`"
                class="!min-h-11 !px-3 !rounded-xl !text-sm"
                @click="toggleActive(server)"
              />
              <Button
                icon="pi pi-trash"
                severity="danger"
                text
                rounded
                :loading="deletingId === server.id"
                :disabled="togglingId !== null || (deletingId !== null && deletingId !== server.id)"
                :aria-label="`Xóa máy chủ MCP ${server.name}`"
                class="!w-11 !h-11"
                @click="deleteServer(server)"
              />
            </div>
          </div>
        </div>
      </section>
    </div>

    <!-- Edit dialog -->
    <Dialog
      v-model:visible="editVisible"
      modal
      header="Chỉnh sửa máy chủ MCP"
      aria-label="Chỉnh sửa máy chủ MCP"
      :style="{ width: '520px' }"
      :breakpoints="{ '575px': '92vw' }"
    >
      <form @submit.prevent="saveEdit" class="grid gap-4 pt-1">
        <div v-if="editError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
          {{ editError }}
        </div>

        <div class="grid gap-1">
          <label for="mcp-edit-name" class="text-sm font-medium text-gray-700">Tên máy chủ</label>
          <InputText id="mcp-edit-name" v-model="editForm.name" required class="w-full" />
        </div>

        <div class="grid gap-1">
          <label for="mcp-edit-url" class="text-sm font-medium text-gray-700">URL máy chủ</label>
          <InputText id="mcp-edit-url" v-model="editForm.url" required type="url" class="w-full" />
        </div>

        <div class="grid gap-1">
          <label for="mcp-edit-headers" class="text-sm font-medium text-gray-700">Header JSON</label>
          <Textarea id="mcp-edit-headers" v-model="editForm.headersJson" rows="3" placeholder='Ví dụ {"Authorization":"Bearer ..."}' class="w-full" aria-describedby="mcp-edit-headers-help" />
          <p id="mcp-edit-headers-help" class="text-xs text-gray-500">Để trống = giữ nguyên header hiện tại. Nhập JSON mới để thay thế.</p>
        </div>

        <div class="grid gap-1">
          <label for="mcp-edit-authmode" class="text-sm font-medium text-gray-700">Phương thức xác thực</label>
          <Select
            inputId="mcp-edit-authmode"
            v-model="editForm.authMode"
            :options="authModeOptions"
            optionLabel="label"
            optionValue="value"
            aria-label="Phương thức xác thực máy chủ MCP"
            class="w-full"
          />
        </div>

        <fieldset v-if="editForm.authMode === 'ClientCredentials'" class="grid gap-3 rounded-lg border border-indigo-200 bg-indigo-50/60 p-3">
          <legend class="px-1 text-xs font-semibold text-indigo-900">OAuth 2.0 Client Credentials</legend>
          <div class="grid gap-1">
            <label for="mcp-edit-clientid" class="text-sm font-medium text-gray-700">Client ID</label>
            <InputText id="mcp-edit-clientid" v-model="editForm.oauthClientId" class="w-full" autocomplete="off" />
          </div>
          <div class="grid gap-1">
            <label for="mcp-edit-clientsecret" class="text-sm font-medium text-gray-700">Client Secret</label>
            <InputText id="mcp-edit-clientsecret" v-model="editForm.oauthClientSecret" type="password" class="w-full" autocomplete="off" aria-describedby="mcp-edit-clientsecret-help" />
            <p id="mcp-edit-clientsecret-help" class="text-xs text-gray-500">Để trống = giữ nguyên secret hiện tại.</p>
          </div>
          <div class="grid gap-1">
            <label for="mcp-edit-tokenurl" class="text-sm font-medium text-gray-700">Token URL</label>
            <InputText id="mcp-edit-tokenurl" v-model="editForm.oauthTokenUrl" type="url" placeholder="https://issuer.example.com/oauth/token" class="w-full" />
          </div>
          <div class="grid gap-1">
            <label for="mcp-edit-scopes" class="text-sm font-medium text-gray-700">Scopes (tùy chọn)</label>
            <InputText id="mcp-edit-scopes" v-model="editForm.oauthScopes" placeholder="read:tools write:tools" class="w-full" />
          </div>
        </fieldset>

        <label for="mcp-edit-trust" class="flex items-start gap-2 text-sm text-amber-800 rounded-lg border border-amber-200 bg-amber-50 p-3">
          <Checkbox inputId="mcp-edit-trust" v-model="editForm.trustReadOnlyAnnotations" binary />
          <span>Tin cậy khai báo <code>readOnlyHint</code> của máy chủ này. Chỉ bật khi đã xác minh nhà cung cấp; nếu tắt, mọi MCP tool đều cần phê duyệt.</span>
        </label>

        <label for="mcp-edit-active" class="flex items-center gap-2 text-sm text-gray-700">
          <Checkbox inputId="mcp-edit-active" v-model="editForm.isActive" binary /> Kích hoạt
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

type AuthMode = 'Static' | 'ClientCredentials';

interface McpServer {
  id: string;
  name: string;
  url: string;
  isActive: boolean;
  hasHeaders: boolean;
  trustReadOnlyAnnotations: boolean;
  authMode: AuthMode;
  oauthClientId?: string | null;
  oauthTokenUrl?: string | null;
  oauthScopes?: string | null;
  hasOAuthSecret: boolean;
}

const authModeOptions: { label: string; value: AuthMode }[] = [
  { label: 'Chỉ dùng header tĩnh (Static)', value: 'Static' },
  { label: 'OAuth 2.0 Client Credentials', value: 'ClientCredentials' }
];
interface McpCatalogEntry {
  name: string;
  baseUrl: string;
  description: string;
}

const servers = ref<McpServer[]>([]);
const catalog = ref<McpCatalogEntry[]>([]);
const loading = ref(false);
const pageError = ref('');

const creating = ref(false);
const createError = ref('');
const emptyCreateForm = () => ({
  name: '', url: '', headersJson: '', isActive: true, trustReadOnlyAnnotations: false,
  authMode: 'Static' as AuthMode, oauthClientId: '', oauthClientSecret: '', oauthTokenUrl: '', oauthScopes: ''
});
const createForm = ref(emptyCreateForm());

const editVisible = ref(false);
const savingEdit = ref(false);
const editError = ref('');
const editForm = ref({
  id: '', name: '', url: '', headersJson: '', isActive: true, trustReadOnlyAnnotations: false,
  authMode: 'Static' as AuthMode, oauthClientId: '', oauthClientSecret: '', oauthTokenUrl: '', oauthScopes: ''
});

const togglingId = ref<string | null>(null);
const deletingId = ref<string | null>(null);

const loadServers = async () => {
  loading.value = servers.value.length === 0;
  pageError.value = '';
  try {
    const response = await http.get(ApiFactory.MCP.BASE);
    if (response.ok) servers.value = await response.json();
    else pageError.value = await readApiError(response, 'Không thể tải cấu hình MCP');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể tải cấu hình MCP.');
  } finally {
    loading.value = false;
  }
};

/**
 * Connection suggestions are a convenience: any failure to load the catalog is
 * swallowed (console.warn only) and simply hides the "Gợi ý kết nối" section.
 */
const loadCatalog = async () => {
  try {
    const response = await http.get(ApiFactory.MCP.CATALOG);
    if (response.ok) catalog.value = await response.json();
    else console.warn('[mcp] không thể tải danh mục gợi ý kết nối', response.status);
  } catch (cause) {
    console.warn('[mcp] không thể tải danh mục gợi ý kết nối', cause);
  }
};

/** Pre-fills the create form only — the admin still reviews and submits it. */
const applyCatalogEntry = (entry: McpCatalogEntry) => {
  createForm.value.name = entry.name;
  createForm.value.url = entry.baseUrl;
};

/**
 * OAuth fields for the request body. Sent only in ClientCredentials mode; scopes are
 * omitted when blank. On edit, a blank secret is intentionally sent as '' so the API
 * keeps the stored secret (matching the headers "leave blank = keep" convention).
 */
const oauthPayload = (form: { authMode: AuthMode; oauthClientId: string; oauthClientSecret: string; oauthTokenUrl: string; oauthScopes: string }) =>
  form.authMode === 'ClientCredentials'
    ? {
        oauthClientId: form.oauthClientId.trim(),
        oauthClientSecret: form.oauthClientSecret,
        oauthTokenUrl: form.oauthTokenUrl.trim(),
        oauthScopes: form.oauthScopes.trim() || undefined
      }
    : {};

const createServer = async () => {
  createError.value = '';
  let headers: Record<string, string> | undefined;
  try {
    headers = createForm.value.headersJson.trim() ? JSON.parse(createForm.value.headersJson) : undefined;
  } catch {
    createError.value = 'Header JSON không hợp lệ.';
    return;
  }
  if (createForm.value.authMode === 'ClientCredentials') {
    if (!createForm.value.oauthClientId.trim() || !createForm.value.oauthTokenUrl.trim() || !createForm.value.oauthClientSecret) {
      createError.value = 'OAuth Client Credentials cần Client ID, Client Secret và Token URL.';
      return;
    }
  }
  creating.value = true;
  try {
    const response = await http.post(ApiFactory.MCP.BASE, {
      name: createForm.value.name.trim(),
      url: createForm.value.url.trim(),
      headers,
      replaceHeaders: true,
      isActive: createForm.value.isActive,
      trustReadOnlyAnnotations: createForm.value.trustReadOnlyAnnotations,
      authMode: createForm.value.authMode,
      ...oauthPayload(createForm.value)
    });
    if (!response.ok) {
      createError.value = await readApiError(response, 'Không thể thêm máy chủ MCP');
      return;
    }
    createForm.value = emptyCreateForm();
    await loadServers();
  } catch (cause) {
    createError.value = errorMessage(cause, 'Không thể thêm máy chủ MCP.');
  } finally {
    creating.value = false;
  }
};

const openEdit = (server: McpServer) => {
  // Headers are never returned by the API (only hasHeaders), so the field starts
  // EMPTY: leaving it blank preserves the stored headers on save.
  editForm.value = {
    id: server.id,
    name: server.name,
    url: server.url,
    headersJson: '',
    isActive: server.isActive,
    trustReadOnlyAnnotations: server.trustReadOnlyAnnotations,
    authMode: server.authMode ?? 'Static',
    // Non-secret OAuth config is pre-filled; the secret field starts EMPTY and a blank
    // value on save preserves the stored secret.
    oauthClientId: server.oauthClientId ?? '',
    oauthClientSecret: '',
    oauthTokenUrl: server.oauthTokenUrl ?? '',
    oauthScopes: server.oauthScopes ?? ''
  };
  editError.value = '';
  editVisible.value = true;
};

const saveEdit = async () => {
  editError.value = '';
  const name = editForm.value.name.trim();
  const url = editForm.value.url.trim();
  if (!name || !url) {
    editError.value = 'Vui lòng nhập tên và URL máy chủ.';
    return;
  }
  const headersText = editForm.value.headersJson.trim();
  const replaceHeaders = headersText.length > 0;
  let headers: Record<string, string> | undefined;
  if (replaceHeaders) {
    try {
      headers = JSON.parse(headersText);
    } catch {
      editError.value = 'Header JSON không hợp lệ.';
      return;
    }
  }
  if (editForm.value.authMode === 'ClientCredentials' && (!editForm.value.oauthClientId.trim() || !editForm.value.oauthTokenUrl.trim())) {
    editError.value = 'OAuth Client Credentials cần Client ID và Token URL.';
    return;
  }
  savingEdit.value = true;
  try {
    const response = await http.put(ApiFactory.MCP.BY_ID(editForm.value.id), {
      name,
      url,
      isActive: editForm.value.isActive,
      trustReadOnlyAnnotations: editForm.value.trustReadOnlyAnnotations,
      headers,
      replaceHeaders,
      authMode: editForm.value.authMode,
      ...oauthPayload(editForm.value)
    });
    if (!response.ok) {
      editError.value = await readApiError(response, 'Không thể cập nhật máy chủ MCP');
      return;
    }
    editVisible.value = false;
    await loadServers();
  } catch (cause) {
    editError.value = errorMessage(cause, 'Không thể cập nhật máy chủ MCP.');
  } finally {
    savingEdit.value = false;
  }
};

const toggleActive = async (server: McpServer) => {
  togglingId.value = server.id;
  pageError.value = '';
  try {
    // replaceHeaders:false so flipping the active flag never wipes stored headers.
    // The current auth mode + OAuth config are resent (secret left blank = kept) so the
    // PUT preserves them instead of resetting the server to Static.
    const response = await http.put(ApiFactory.MCP.BY_ID(server.id), {
      name: server.name,
      url: server.url,
      isActive: !server.isActive,
      trustReadOnlyAnnotations: server.trustReadOnlyAnnotations,
      replaceHeaders: false,
      authMode: server.authMode,
      ...(server.authMode === 'ClientCredentials'
        ? {
            oauthClientId: server.oauthClientId ?? '',
            oauthClientSecret: '',
            oauthTokenUrl: server.oauthTokenUrl ?? '',
            oauthScopes: server.oauthScopes ?? undefined
          }
        : {})
    });
    if (response.ok) await loadServers();
    else pageError.value = await readApiError(response, 'Không thể cập nhật trạng thái máy chủ MCP');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể cập nhật trạng thái máy chủ MCP.');
  } finally {
    togglingId.value = null;
  }
};

const deleteServer = async (server: McpServer) => {
  if (!confirm(`Xóa máy chủ MCP "${server.name}"?`)) return;
  deletingId.value = server.id;
  pageError.value = '';
  try {
    const response = await http.delete(ApiFactory.MCP.BY_ID(server.id));
    if (response.ok) servers.value = servers.value.filter((s) => s.id !== server.id);
    else pageError.value = await readApiError(response, 'Không thể xóa máy chủ MCP');
  } catch (cause) {
    pageError.value = errorMessage(cause, 'Không thể xóa máy chủ MCP.');
  } finally {
    deletingId.value = null;
  }
};

onMounted(() => {
  void loadCatalog();
  void loadServers();
});
</script>
