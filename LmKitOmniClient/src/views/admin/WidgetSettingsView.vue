<template>
  <div class="flex-1 overflow-y-auto bg-gray-50 p-4 md:p-6">
    <div class="max-w-3xl mx-auto">
      <header class="mb-6 flex items-center gap-4">
        <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-blue-500 to-blue-600 flex items-center justify-center shadow-md shadow-blue-500/20 flex-shrink-0">
          <i class="pi pi-objects-column text-white text-sm" aria-hidden="true"></i>
        </div>
        <div>
          <h1 class="text-xl font-bold text-gray-900 tracking-tight">Widget nhúng</h1>
          <p class="text-sm text-gray-500">Cho phép các trang web của bạn nhúng widget chat công khai qua khóa + origin allowlist.</p>
        </div>
      </header>

      <div v-if="loadError" class="mb-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700" role="alert">{{ loadError }}</div>

      <form class="space-y-6" @submit.prevent="save">
        <!-- Enable -->
        <section class="rounded-xl border border-gray-200 bg-white p-5">
          <label class="flex items-center gap-3">
            <input v-model="form.isActive" type="checkbox" class="h-4 w-4 accent-blue-600" />
            <span class="text-sm font-semibold text-gray-900">Bật widget công khai</span>
          </label>
          <p class="mt-2 text-xs text-gray-500">Khi bật, bất kỳ trang nào có khóa widget và origin khớp allowlist đều có thể trò chuyện bằng tài nguyên AI của tenant này.</p>
        </section>

        <!-- Origins -->
        <section class="rounded-xl border border-gray-200 bg-white p-5">
          <label for="origins" class="text-sm font-semibold text-gray-900">Origin được phép nhúng</label>
          <p class="mt-1 text-xs text-gray-500">Mỗi dòng một origin dạng <code>https://example.com</code> (tối đa 20). origins khác allowlist sẽ bị từ chối (403).</p>
          <textarea
            id="origins"
            v-model="originsText"
            rows="4"
            class="mt-2 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm font-mono focus:border-blue-400 focus:ring-1 focus:ring-blue-300"
            placeholder="https://example.com"
          ></textarea>
        </section>

        <!-- Quotas -->
        <section class="rounded-xl border border-gray-200 bg-white p-5 grid grid-cols-1 sm:grid-cols-2 gap-4">
          <div>
            <label for="rpm" class="text-sm font-semibold text-gray-900">Giới hạn / phút</label>
            <input id="rpm" v-model.number="form.requestsPerMinute" type="number" min="0" max="600" class="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" />
            <p class="mt-1 text-xs text-gray-500">0 = mặc định hệ thống (60).</p>
          </div>
          <div>
            <label for="rpd" class="text-sm font-semibold text-gray-900">Giới hạn / ngày</label>
            <input id="rpd" v-model.number="form.requestsPerDay" type="number" min="0" max="100000" class="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" />
            <p class="mt-1 text-xs text-gray-500">0 = mặc định hệ thống (10.000).</p>
          </div>
        </section>

        <!-- Branding -->
        <section class="rounded-xl border border-gray-200 bg-white p-5 grid grid-cols-1 sm:grid-cols-2 gap-4">
          <div>
            <label for="title" class="text-sm font-semibold text-gray-900">Tiêu đề widget</label>
            <input id="title" v-model="form.widgetTitle" type="text" maxlength="50" class="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" placeholder="CILA - AI Agent" />
          </div>
          <div>
            <label for="brand" class="text-sm font-semibold text-gray-900">Màu thương hiệu (#RRGGBB)</label>
            <input id="brand" v-model="form.brandColor" type="text" maxlength="7" class="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" placeholder="#2563eb" />
          </div>
          <div class="sm:col-span-2">
            <label for="welcome" class="text-sm font-semibold text-gray-900">Lời chào</label>
            <input id="welcome" v-model="form.welcomeMessage" type="text" maxlength="500" class="mt-1 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" placeholder="Xin chào! Tôi có thể giúp gì cho bạn?" />
          </div>
        </section>

        <div class="flex items-center gap-3">
          <button type="submit" :disabled="saving" class="rounded-lg bg-blue-600 px-5 py-2.5 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-50">
            {{ saving ? 'Đang lưu…' : 'Lưu cấu hình' }}
          </button>
          <button type="button" :disabled="saving || rotating" class="rounded-lg border border-gray-300 bg-white px-5 py-2.5 text-sm font-semibold text-gray-700 hover:bg-gray-50 disabled:opacity-50" @click="rotateKey">
            {{ rotating ? 'Đang tạo…' : 'Tạo khóa mới' }}
          </button>
          <span v-if="savedMessage" class="text-sm text-green-700" role="status">{{ savedMessage }}</span>
          <span v-if="actionError" class="text-sm text-red-700" role="alert">{{ actionError }}</span>
        </div>
      </form>

      <!-- Raw key modal -->
      <div v-if="createdKey" class="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" role="dialog" aria-modal="true" aria-label="Khóa widget mới">
        <div class="w-full max-w-lg rounded-xl bg-white p-6 shadow-xl">
          <h2 class="text-lg font-bold text-gray-900">Khóa widget mới</h2>
          <p class="mt-2 text-sm text-amber-700 bg-amber-50 border border-amber-200 rounded-lg px-3 py-2">
            Khóa chỉ hiển thị MỘT lần duy nhất. Sao chép và lưu ngay — sau khi đóng không thể xem lại.
          </p>
          <code class="mt-3 block break-all rounded-lg bg-gray-50 border border-gray-200 px-3 py-2 font-mono text-sm">{{ createdKey }}</code>
          <div class="mt-4 flex items-center justify-between gap-3">
            <button type="button" class="rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium" @click="copyKey">{{ copied ? 'Đã sao chép' : 'Sao chép khóa' }}</button>
            <button type="button" class="rounded-lg bg-gray-900 px-4 py-2 text-sm font-semibold text-white" @click="createdKey = null">Đã lưu khóa</button>
          </div>
          <h3 class="mt-5 text-sm font-semibold text-gray-900">Mã nhúng đã kèm khóa</h3>
          <p class="mt-1 text-xs text-gray-500">Copy nguyên đoạn dưới đây, dán vào trang web là chạy ngay (không cần thay gì).</p>
          <pre class="mt-2 overflow-x-auto rounded-lg bg-gray-900 px-4 py-3 text-xs text-gray-100"><code>{{ widgetEmbedScript }}</code></pre>
          <button type="button" class="mt-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white hover:bg-blue-700" @click="copyEmbed('script')">
            {{ copiedField === 'script' ? 'Đã sao chép!' : 'Copy mã nhúng (đã kèm khóa)' }}
          </button>
        </div>
      </div>

      <!-- Embed snippets: hai biến thể đều copy-paste-run. Khóa thật chỉ có trong
           phiên sau khi bấm "Tạo khóa mới" (key show-once, BE chỉ lưu hash) —
           trước đó snippet dùng chỗ giữ chỗ <WIDGET_KEY>. -->
      <section v-if="form.isActive" class="mt-8 rounded-xl border border-gray-200 bg-white p-5">
        <h2 class="text-sm font-semibold text-gray-900">Mã nhúng</h2>
        <p class="mt-1 text-xs text-gray-500">
          Chọn một trong hai cách, dán vào trang web của bạn là chạy ngay. Nhớ thêm origin của trang đích vào
          mục <strong>Origin được phép nhúng</strong> ở trên — thiếu origin widget sẽ bị chặn (403).
        </p>

        <!-- Cách 1: loader script (nút chat nổi) -->
        <div class="mt-4">
          <div class="flex flex-wrap items-center justify-between gap-2">
            <h3 class="text-sm font-medium text-gray-900">Cách 1 — Nút chat nổi (khuyên dùng)</h3>
            <button type="button" class="rounded-lg border border-gray-300 bg-white px-3 py-1.5 text-xs font-semibold text-gray-700 hover:bg-gray-50" @click="copyEmbed('script')">
              {{ copiedField === 'script' ? 'Đã sao chép!' : 'Copy' }}
            </button>
          </div>
          <p class="mt-1 text-xs text-gray-500">Dán vào cuối <code>&lt;body&gt;</code>. Tự gắn nút chat tròn góc phải dưới + cửa sổ chat 380×600, hỗ trợ nút thu nhỏ.</p>
          <pre class="mt-2 overflow-x-auto rounded-lg bg-gray-900 px-4 py-3 text-xs text-gray-100"><code>{{ widgetEmbedScript }}</code></pre>
        </div>

        <!-- Cách 2: iframe trực tiếp -->
        <div class="mt-6">
          <div class="flex flex-wrap items-center justify-between gap-2">
            <h3 class="text-sm font-medium text-gray-900">Cách 2 — Nhúng iframe trực tiếp</h3>
            <button type="button" class="rounded-lg border border-gray-300 bg-white px-3 py-1.5 text-xs font-semibold text-gray-700 hover:bg-gray-50" @click="copyEmbed('html')">
              {{ copiedField === 'html' ? 'Đã sao chép!' : 'Copy' }}
            </button>
          </div>
          <p class="mt-1 text-xs text-gray-500">Cửa sổ chat cố định góc phải dưới, hiện sẵn khi tải trang (không có nút nổi). Khối listener đi kèm để nút thu nhỏ trong widget hoạt động.</p>
          <pre class="mt-2 overflow-x-auto rounded-lg bg-gray-900 px-4 py-3 text-xs text-gray-100"><code>{{ widgetEmbedHtml }}</code></pre>
        </div>

        <p v-if="!embedKey" class="mt-4 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
          <i class="pi pi-info-circle mr-1" aria-hidden="true"></i>
          Đoạn mã đang dùng chỗ giữ chỗ <code>&lt;WIDGET_KEY&gt;</code>. Bấm “Tạo khóa mới” ở trên để nhận khóa thật — mã nhúng sẽ tự điền sẵn khóa để copy-paste là chạy (khóa chỉ hiển thị một lần).
        </p>
        <p v-else class="mt-4 rounded-lg border border-green-200 bg-green-50 px-3 py-2 text-xs text-green-800">
          <i class="pi pi-check-circle mr-1" aria-hidden="true"></i>
          Đoạn mã đã điền sẵn khóa của phiên này — copy và dán vào trang web là dùng được ngay.
        </p>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage } from '@/api/errors';

interface WidgetSettings {
  isActive: boolean;
  allowedOrigins: string[];
  requestsPerMinute: number;
  requestsPerDay: number;
  widgetTitle?: string | null;
  welcomeMessage?: string | null;
  brandColor?: string | null;
}

const form = ref<WidgetSettings>({
  isActive: false,
  allowedOrigins: [],
  requestsPerMinute: 0,
  requestsPerDay: 0,
  widgetTitle: '',
  welcomeMessage: '',
  brandColor: ''
});
const originsText = ref('');
const loadError = ref('');
const savedMessage = ref('');
const actionError = ref('');
const saving = ref(false);
const rotating = ref(false);
const createdKey = ref<string | null>(null);
const copied = ref(false);
const copiedField = ref<'script' | 'html' | null>(null);
const appOrigin = window.location.origin;
// Khóa thô của lần rotate GẦN NHẤT trong phiên này (không persist, BE chỉ lưu
// hash). Đưa thẳng vào snippet để copy-paste là chạy; hết phiên thì fallback về
// chỗ giữ chỗ <WIDGET_KEY> vì thiết kế show-once không cho xem lại khóa.
const embedKey = ref<string | null>(null);

// `<` của thẻ script/iframe trong chuỗi JS viết dạng \u003C để trình phân tích
// SFC không thấy thẻ đóng script (dạng chữ) và cắt khối script của file .vue.
const widgetEmbedScript = computed(() => {
  const key = embedKey.value ?? '<WIDGET_KEY>';
  return `\u003Cscript
  src="${appOrigin}/widget.js"
  data-widget-key="${key}">\u003C/script>`;
});

const widgetEmbedHtml = computed(() => {
  const key = embedKey.value ?? '<WIDGET_KEY>';
  return `\u003Ciframe
  id="cila-widget-frame"
  src="${appOrigin}/widget/chat?key=${encodeURIComponent(key)}"
  style="position:fixed;bottom:16px;right:16px;width:380px;height:560px;border:0;border-radius:16px;box-shadow:0 10px 30px rgba(0,0,0,.25);z-index:99999"
  title="CILA - AI Agent"
  allow="clipboard-write"></iframe>
\u003Cscript>
window.addEventListener('message', function (e) {
  if (e.data && e.data.type === 'lmkit-close-widget') {
    var f = document.getElementById('cila-widget-frame');
    if (f) f.style.display = 'none';
  }
});
\u003C/script>`;
});

const load = async () => {
  loadError.value = '';
  try {
    const response = await http.get(ApiFactory.WIDGET.ADMIN_SETTINGS);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const data = await response.json();
    form.value = { ...form.value, ...data };
    originsText.value = (data.allowedOrigins ?? []).join('\n');
  } catch (error) {
    loadError.value = errorMessage(error, 'Không thể tải cấu hình widget.');
  }
};

const save = async () => {
  saving.value = true;
  savedMessage.value = '';
  actionError.value = '';
  try {
    const payload = {
      isActive: form.value.isActive,
      allowedOrigins: originsText.value.split('\n').map((line) => line.trim()).filter((line) => line.length > 0),
      requestsPerMinute: form.value.requestsPerMinute,
      requestsPerDay: form.value.requestsPerDay,
      widgetTitle: form.value.widgetTitle,
      welcomeMessage: form.value.welcomeMessage,
      brandColor: form.value.brandColor
    };
    const response = await http.put(ApiFactory.WIDGET.ADMIN_SETTINGS, payload);
    if (!response.ok) throw new Error(await response.text() || `HTTP ${response.status}`);
    savedMessage.value = 'Đã lưu cấu hình.';
    await load();
  } catch (error) {
    actionError.value = errorMessage(error, 'Không thể lưu cấu hình widget.');
  } finally {
    saving.value = false;
  }
};

const rotateKey = async () => {
  rotating.value = true;
  savedMessage.value = '';
  actionError.value = '';
  copied.value = false;
  try {
    const response = await http.post(ApiFactory.WIDGET.ADMIN_ROTATE);
    if (!response.ok) {
      const body = await response.text();
      throw new Error(body || `HTTP ${response.status}`);
    }
    const data = await response.json();
    createdKey.value = data.rawKey;
    embedKey.value = data.rawKey;
    copiedField.value = null;
  } catch (error) {
    actionError.value = errorMessage(error, 'Không thể tạo khóa widget mới.');
  } finally {
    rotating.value = false;
  }
};

const copyKey = async () => {
  if (!createdKey.value) return;
  try {
    await navigator.clipboard.writeText(createdKey.value);
    copied.value = true;
  } catch {
    /* clipboard unavailable — key stays visible for manual copy */
  }
};

const copyEmbed = async (which: 'script' | 'html') => {
  const text = which === 'script' ? widgetEmbedScript.value : widgetEmbedHtml.value;
  try {
    await navigator.clipboard.writeText(text);
    copiedField.value = which;
    setTimeout(() => { if (copiedField.value === which) copiedField.value = null; }, 2000);
  } catch {
    /* clipboard unavailable — snippet stays visible for manual copy */
  }
};

onMounted(() => {
  void load();
});
</script>
