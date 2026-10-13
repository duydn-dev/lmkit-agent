<template>
  <div class="flex-1 flex flex-col h-full bg-chatgpt-dark overflow-y-auto">
    <!-- Page Header -->
    <div class="sticky top-0 z-10 bg-chatgpt-dark/80 backdrop-blur-xl border-b border-gray-200/60">
      <div class="max-w-5xl mx-auto px-6 py-4">
        <div class="flex items-center gap-4">
          <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-sky-500 to-indigo-600 flex items-center justify-center flex-shrink-0 shadow-md shadow-sky-500/20">
            <i class="pi pi-video text-white text-sm" aria-hidden="true"></i>
          </div>
          <div>
            <h1 class="text-xl font-bold text-gray-900 tracking-tight">Dịch video</h1>
            <p class="text-xs text-gray-500">Preview video local hoặc URL media, chọn ngôn ngữ đích. Dịch AI và xuất video thật đang chờ backend.</p>
          </div>
        </div>
      </div>
    </div>

    <!-- Main Content -->
    <div class="flex-1 max-w-5xl mx-auto w-full px-6 py-6">
      <div class="mb-6 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900 flex items-start gap-2">
        <i class="pi pi-info-circle mt-0.5 flex-shrink-0" aria-hidden="true"></i>
        <span>Giai đoạn UI-first: không gửi URL hay tệp lên backend, không gọi model. Nút <strong>Xuất video</strong> đang tắt.</span>
      </div>

      <div class="grid grid-cols-1 lg:grid-cols-3 gap-6">
        <!-- Controls -->
        <section aria-label="Nguồn video và ngôn ngữ" class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5 space-y-5 lg:col-span-1">
          <div>
            <label for="vt-url" class="mb-1.5 block text-sm font-medium text-gray-800">URL video</label>
            <input
              id="vt-url"
              v-model="sourceUrl"
              type="url"
              inputmode="url"
              autocomplete="off"
              placeholder="https://example.com/video.mp4"
              class="w-full rounded-lg border border-gray-200 px-3 py-2 text-sm focus:border-indigo-400 focus:ring-2 focus:ring-indigo-100"
            />
            <p class="mt-1 text-xs text-gray-500">Chỉ phát sau khi bấm “Xem trước”. URL trang Facebook/YouTube có thể không phát được.</p>
            <button
              type="button"
              data-testid="preview-btn"
              class="mt-3 inline-flex items-center gap-2 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700"
              @click="previewFromUrl"
            >
              <i class="pi pi-eye text-xs" aria-hidden="true"></i>
              Xem trước
            </button>
          </div>

          <div>
            <span class="mb-1.5 block text-sm font-medium text-gray-800">Video local</span>
            <div class="flex flex-wrap items-center gap-2">
              <label
                for="vt-file"
                class="inline-flex cursor-pointer items-center gap-2 rounded-lg border border-gray-200 px-4 py-2 text-sm font-medium text-gray-700 hover:border-indigo-300 hover:bg-indigo-50/40"
              >
                <i class="pi pi-folder-open text-xs" aria-hidden="true"></i>
                Chọn video
              </label>
              <input id="vt-file" type="file" accept="video/*" class="sr-only" @change="onFileChange" />
              <button
                v-if="selectedFileName"
                type="button"
                data-testid="clear-file-btn"
                class="rounded-lg border border-gray-200 px-3 py-2 text-xs font-medium text-gray-600 hover:bg-gray-50"
                @click="clearFile"
              >
                Xoá
              </button>
            </div>
            <p v-if="selectedFileName" class="mt-1 truncate text-sm text-gray-500">{{ selectedFileName }}</p>
            <p v-else class="mt-1 text-xs text-gray-500">MP4/WebM tùy codec trình duyệt hỗ trợ.</p>
          </div>

          <div>
            <label for="vt-lang-search" class="mb-1.5 block text-sm font-medium text-gray-800">Ngôn ngữ đích</label>
            <input
              id="vt-lang-search"
              v-model="targetQuery"
              type="text"
              autocomplete="off"
              placeholder="Tìm: vi, Vietnamese, Tiếng Việt…"
              class="w-full rounded-lg border border-gray-200 px-3 py-2 text-sm focus:border-indigo-400 focus:ring-2 focus:ring-indigo-100"
            />
            <select
              v-model="targetLocale"
              aria-label="Ngôn ngữ đích"
              class="mt-2 w-full rounded-lg border border-gray-200 px-3 py-2 text-sm focus:border-indigo-400"
            >
              <option v-for="lang in filteredLanguages" :key="lang.code" :value="lang.code">
                {{ lang.nativeName }} — {{ lang.englishName }} ({{ lang.code }})
              </option>
            </select>
            <p class="mt-1 text-xs text-gray-500">{{ filteredLanguages.length }} / {{ VIDEO_LANGUAGES.length }} ngôn ngữ (ISO 639-1).</p>
          </div>

          <div v-if="errorMessage" role="alert" data-testid="video-error" class="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">
            {{ errorMessage }}
          </div>
          <div v-if="socialWarning" role="note" class="rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-xs text-amber-900">
            Link Facebook/YouTube dạng trang xem thường không phải URL media trực tiếp — trình duyệt có thể
            chặn nhúng/CORS/đăng nhập/DRM. Giai đoạn này không tải hộ hay bóc audio từ các nền tảng đó.
          </div>
        </section>

        <!-- Viewer -->
        <section aria-label="Xem trước video" class="lg:col-span-2">
          <div class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5">
            <div class="relative w-full overflow-hidden rounded-xl bg-black" style="aspect-ratio: 16 / 9;">
              <video
                v-if="previewSource"
                data-testid="preview-video"
                controls
                :src="previewSource"
                class="absolute inset-0 h-full w-full"
                @error="onVideoError"
              ></video>
              <div v-else class="absolute inset-0 flex items-center justify-center p-6 text-center">
                <p class="text-sm text-white/70">Chưa có nguồn preview — nhập URL rồi bấm “Xem trước” hoặc chọn video local.</p>
              </div>
              <!-- Caption demo bottom-center, không che controls -->
              <div v-if="previewSource" class="pointer-events-none absolute inset-x-0 bottom-10 flex justify-center px-4">
                <div data-testid="demo-caption" class="max-w-[90%] rounded-md bg-black/70 px-3 py-1.5 text-center text-sm font-medium text-white" style="display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden;">
                  {{ demoCaption }}
                  <span class="ml-2 rounded bg-amber-400/90 px-1.5 py-0.5 text-[11px] font-bold text-black">Phụ đề minh họa — AI chưa kết nối</span>
                </div>
              </div>
            </div>

            <div v-if="mediaError" role="alert" data-testid="media-error" class="mt-3 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">
              Không phát được video (codec/container không hỗ trợ hoặc URL không phải media trực tiếp).
              Hãy thử file MP4/WebM khác hoặc URL media truy cập trực tiếp được.
            </div>

            <div class="mt-4 flex flex-wrap items-center gap-3">
              <button
                type="button"
                data-testid="export-btn"
                disabled
                title="Cần backend xử lý và kết xuất video"
                class="inline-flex cursor-not-allowed items-center gap-2 rounded-lg bg-gray-200 px-4 py-2 text-sm font-medium text-gray-500"
              >
                <i class="pi pi-cloud-download text-xs" aria-hidden="true"></i>
                Xuất video
              </button>
              <p class="text-xs text-gray-500">
                Xuất video đang <strong>chưa khả dụng</strong> — cần backend xử lý media/transcription/translation/rendering.
                Không có file tải xuống ở giai đoạn này.
              </p>
            </div>
          </div>
        </section>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue';
import {
  VIDEO_LANGUAGES,
  filterVideoLanguages,
  isAllowedVideoSource,
  isSocialPageUrl,
} from './videoLanguages';

const DEMO_CAPTION = 'Xin chào — đây là phụ đề minh họa.';

const sourceUrl = ref('');
const targetQuery = ref('');
const targetLocale = ref('vi');
const previewSource = ref<string | null>(null);
const previewKind = ref<'local' | 'url' | null>(null);
const selectedFileName = ref<string | null>(null);
const errorMessage = ref<string | null>(null);
const mediaError = ref(false);
const socialWarning = ref(false);

let objectUrl: string | null = null;

const filteredLanguages = computed(() => filterVideoLanguages(targetQuery.value));
const demoCaption = computed(() => DEMO_CAPTION);

function revokeObjectUrl(): void {
  if (objectUrl) {
    URL.revokeObjectURL(objectUrl);
    objectUrl = null;
  }
}

/** Gắn URL vào player — chỉ sau click Preview rõ ràng, đã validate cú pháp. */
function previewFromUrl(): void {
  const raw = sourceUrl.value.trim();
  mediaError.value = false;
  if (!isAllowedVideoSource(raw)) {
    errorMessage.value = 'URL chưa hợp lệ — cần URL tuyệt đối bắt đầu bằng http:// hoặc https://.';
    socialWarning.value = false;
    return;
  }
  errorMessage.value = null;
  socialWarning.value = isSocialPageUrl(raw);
  revokeObjectUrl();
  selectedFileName.value = null;
  previewKind.value = 'url';
  previewSource.value = raw;
}

function onFileChange(event: Event): void {
  const input = event.target as HTMLInputElement;
  const picked = input.files?.[0] ?? null;
  input.value = '';
  if (!picked) return;
  if (!picked.type.startsWith('video/')) {
    errorMessage.value = 'Chỉ nhận tệp video.';
    return;
  }
  errorMessage.value = null;
  mediaError.value = false;
  socialWarning.value = false;
  revokeObjectUrl();
  selectedFileName.value = picked.name;
  previewKind.value = 'local';
  objectUrl = URL.createObjectURL(picked);
  previewSource.value = objectUrl;
}

function clearFile(): void {
  revokeObjectUrl();
  selectedFileName.value = null;
  if (previewKind.value === 'local') {
    previewKind.value = null;
    previewSource.value = null;
  }
  mediaError.value = false;
  errorMessage.value = null;
}

function onVideoError(): void {
  mediaError.value = true;
}

onBeforeUnmount(() => {
  revokeObjectUrl();
});
</script>
