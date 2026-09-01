<template>
  <div class="flex-1 flex flex-col h-full bg-chatgpt-dark overflow-y-auto">
    <!-- Page Header -->
    <div class="sticky top-0 z-10 bg-chatgpt-dark/80 backdrop-blur-xl border-b border-gray-200/60">
      <div class="max-w-5xl mx-auto px-6 py-4">
        <div class="flex items-center gap-4">
          <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-fuchsia-500 to-pink-600 flex items-center justify-center shadow-md shadow-fuchsia-500/20">
            <i class="pi pi-eye text-white text-sm" aria-hidden="true"></i>
          </div>
          <div>
            <h1 class="text-xl font-bold text-gray-900 tracking-tight">Thị giác ảnh</h1>
            <p class="text-xs text-gray-500">Tải ảnh lên rồi mô tả, OCR, phân loại hoặc xóa nền.</p>
          </div>
        </div>
      </div>
    </div>

    <!-- Main Content -->
    <div class="flex-1 max-w-5xl mx-auto w-full px-6 py-6">
      <div class="mb-6 rounded-xl border border-sky-200 bg-sky-50 px-4 py-3 text-sm text-sky-900 flex items-start gap-2">
        <i class="pi pi-info-circle mt-0.5 flex-shrink-0" aria-hidden="true"></i>
        <span>Cần model VLM được cấu hình trên máy chủ để chạy suy luận.</span>
      </div>

      <!-- STEP 1 — Upload -->
      <section aria-labelledby="vision-step1-heading" class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5 mb-6">
        <h2 id="vision-step1-heading" class="text-sm font-semibold text-gray-900 mb-1">Bước 1 · Tải ảnh lên</h2>
        <p class="text-xs text-gray-500 mb-4">Chọn một ảnh để bắt đầu. Ảnh sẽ được tải lên máy chủ trước khi chạy các thao tác.</p>

        <div
          class="relative rounded-2xl border-2 border-dashed border-gray-200 bg-gray-50/50 transition-colors hover:border-fuchsia-300 hover:bg-fuchsia-50/30 focus-within:border-fuchsia-400 focus-within:ring-2 focus-within:ring-fuchsia-100"
        >
          <input
            id="vision-file"
            type="file"
            :accept="ACCEPTED_TYPES"
            aria-label="Chọn ảnh để tải lên và phân tích"
            class="absolute inset-0 w-full h-full opacity-0 cursor-pointer"
            @change="onFileChange"
          />
          <div class="flex flex-col items-center py-10 px-6 text-center pointer-events-none">
            <div class="w-16 h-16 rounded-2xl bg-gradient-to-br from-fuchsia-50 to-pink-100 flex items-center justify-center mb-4">
              <i class="pi pi-image text-2xl text-fuchsia-500" aria-hidden="true"></i>
            </div>
            <p class="text-sm font-medium text-gray-700">
              <span class="text-fuchsia-700 font-semibold">Nhấp để chọn ảnh</span> hoặc kéo thả vào đây
            </p>
            <p class="text-xs text-gray-500 mt-1">PNG, JPEG, WebP, GIF, BMP, TIFF — tối đa 20MB</p>
          </div>
        </div>

        <!-- Preview + upload status -->
        <div v-if="objectUrl" class="mt-4 flex flex-col sm:flex-row items-start gap-4">
          <img
            :src="objectUrl"
            alt="Ảnh đã chọn để phân tích"
            class="w-32 h-32 object-cover rounded-xl border border-gray-200 flex-shrink-0"
          />
          <div class="min-w-0">
            <p class="text-sm font-medium text-gray-800 break-words">{{ selectedFile?.name }}</p>
            <p class="text-xs text-gray-500 mt-0.5">{{ selectedFile ? formatFileSize(selectedFile.size) : '' }}</p>
            <div v-if="uploading" role="status" class="mt-2 inline-flex items-center gap-2 text-sm text-gray-500">
              <i class="pi pi-spin pi-spinner" aria-hidden="true"></i> Đang tải lên...
            </div>
            <div v-else-if="imagePath" role="status" class="mt-2 inline-flex items-center gap-1.5 text-sm text-emerald-900">
              <i class="pi pi-check-circle" aria-hidden="true"></i> Đã tải lên
            </div>
          </div>
        </div>

        <div v-if="uploadError" role="alert" class="mt-4 rounded-xl border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
          {{ uploadError }}
        </div>
      </section>

      <!-- STEP 2 — Operations -->
      <section aria-labelledby="vision-step2-heading">
        <div class="flex flex-wrap items-center justify-between gap-2 mb-3">
          <h2 id="vision-step2-heading" class="text-sm font-semibold text-gray-900">Bước 2 · Thao tác trên ảnh</h2>
          <span
            v-if="!imagePath"
            class="inline-flex items-center gap-1.5 text-xs text-gray-500"
          >
            <i class="pi pi-lock" aria-hidden="true"></i> Hãy tải một ảnh lên để mở khóa
          </span>
        </div>

        <div class="grid grid-cols-1 lg:grid-cols-2 gap-4 items-start">
          <!-- 1) Describe -->
          <div class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5">
            <div class="flex items-center gap-2.5 mb-1">
              <div class="w-9 h-9 rounded-lg bg-gradient-to-br from-sky-50 to-sky-100 flex items-center justify-center flex-shrink-0">
                <i class="pi pi-comment text-sky-600 text-sm" aria-hidden="true"></i>
              </div>
              <h3 class="text-sm font-semibold text-gray-900">Mô tả ảnh</h3>
            </div>
            <p class="text-xs text-gray-500 mb-3">Yêu cầu mô hình mô tả nội dung của ảnh.</p>

            <div class="grid gap-1.5 mb-3">
              <label for="vision-prompt" class="text-xs font-medium text-gray-700">Câu hỏi/nhắc</label>
              <InputText
                id="vision-prompt"
                v-model="describePrompt"
                :disabled="!imagePath"
                placeholder="Describe this image."
                class="w-full !text-sm"
              />
            </div>

            <Button
              label="Mô tả ảnh"
              icon="pi pi-play"
              :loading="describeLoading"
              :disabled="!imagePath || describeLoading"
              :class="PRIMARY_BTN"
              class="w-full sm:w-auto"
              @click="runDescribe"
            />

            <div class="mt-3">
              <div v-if="describeLoading" role="status" class="flex items-center gap-2 text-sm text-gray-500">
                <i class="pi pi-spin pi-spinner" aria-hidden="true"></i> Đang xử lý...
              </div>
              <div v-else-if="describeError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-3 py-2.5 text-sm text-red-700">
                {{ describeError }}
              </div>
              <div v-else-if="describeResult !== null">
                <p v-if="describeResult" class="text-sm text-gray-800 whitespace-pre-wrap break-words">{{ describeResult }}</p>
                <p v-else class="text-xs text-gray-400">Mô hình không trả về mô tả.</p>
              </div>
              <p v-else class="text-xs text-gray-400">Chưa có kết quả.</p>
            </div>
          </div>

          <!-- 2) OCR -->
          <div class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5">
            <div class="flex items-center gap-2.5 mb-1">
              <div class="w-9 h-9 rounded-lg bg-gradient-to-br from-emerald-50 to-emerald-100 flex items-center justify-center flex-shrink-0">
                <i class="pi pi-file-word text-emerald-600 text-sm" aria-hidden="true"></i>
              </div>
              <h3 class="text-sm font-semibold text-gray-900">OCR (trích xuất chữ)</h3>
            </div>
            <p class="text-xs text-gray-500 mb-3">Nhận diện và trích xuất văn bản có trong ảnh.</p>

            <Button
              label="Trích xuất chữ"
              icon="pi pi-play"
              :loading="ocrLoading"
              :disabled="!imagePath || ocrLoading"
              :class="PRIMARY_BTN"
              class="w-full sm:w-auto"
              @click="runOcr"
            />

            <div class="mt-3">
              <div v-if="ocrLoading" role="status" class="flex items-center gap-2 text-sm text-gray-500">
                <i class="pi pi-spin pi-spinner" aria-hidden="true"></i> Đang trích xuất...
              </div>
              <div v-else-if="ocrError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-3 py-2.5 text-sm text-red-700">
                {{ ocrError }}
              </div>
              <div v-else-if="ocrResult !== null">
                <p v-if="ocrResult" class="text-sm text-gray-800 whitespace-pre-wrap break-words rounded-lg bg-gray-50 border border-gray-100 p-3">{{ ocrResult }}</p>
                <p v-else class="text-xs text-gray-400">Không tìm thấy văn bản trong ảnh.</p>
              </div>
              <p v-else class="text-xs text-gray-400">Chưa có kết quả.</p>
            </div>
          </div>

          <!-- 3) Classify -->
          <div class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5">
            <div class="flex items-center gap-2.5 mb-1">
              <div class="w-9 h-9 rounded-lg bg-gradient-to-br from-violet-50 to-violet-100 flex items-center justify-center flex-shrink-0">
                <i class="pi pi-tags text-violet-600 text-sm" aria-hidden="true"></i>
              </div>
              <h3 class="text-sm font-semibold text-gray-900">Phân loại ảnh</h3>
            </div>
            <p class="text-xs text-gray-500 mb-3">Gán ảnh vào một trong các danh mục bạn cung cấp.</p>

            <div class="grid gap-1.5 mb-3">
              <label for="vision-categories" class="text-xs font-medium text-gray-700">Danh mục (phân tách bằng dấu phẩy)</label>
              <InputText
                id="vision-categories"
                v-model="categoriesRaw"
                :disabled="!imagePath"
                placeholder="Ví dụ: Động vật, Phong cảnh, Món ăn"
                class="w-full !text-sm"
              />
              <ul v-if="categoryList.length" class="flex flex-wrap gap-1.5 mt-0.5" aria-label="Danh mục đã nhập">
                <li
                  v-for="category in categoryList"
                  :key="category"
                  class="inline-flex items-center px-2.5 py-1 rounded-full text-[11px] font-semibold bg-violet-50 text-violet-900 border border-violet-200"
                >{{ category }}</li>
              </ul>
              <p v-else class="text-xs text-gray-400">Cần ít nhất một danh mục để phân loại.</p>
            </div>

            <Button
              label="Phân loại ảnh"
              icon="pi pi-play"
              :loading="classifyLoading"
              :disabled="!imagePath || classifyLoading || categoryList.length === 0"
              :class="PRIMARY_BTN"
              class="w-full sm:w-auto"
              @click="runClassify"
            />

            <div class="mt-3">
              <div v-if="classifyLoading" role="status" class="flex items-center gap-2 text-sm text-gray-500">
                <i class="pi pi-spin pi-spinner" aria-hidden="true"></i> Đang phân loại...
              </div>
              <div v-else-if="classifyError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-3 py-2.5 text-sm text-red-700">
                {{ classifyError }}
              </div>
              <div v-else-if="classifyResult" class="flex flex-wrap items-center gap-2">
                <span class="inline-flex items-center px-2.5 py-1 rounded-full text-[11px] font-semibold bg-violet-50 text-violet-900 border border-violet-200">{{ classifyResult.category || '—' }}</span>
                <span class="text-xs text-gray-500">Độ tin cậy {{ toPercent(classifyResult.confidence) }}</span>
              </div>
              <p v-else class="text-xs text-gray-400">Chưa có kết quả.</p>
            </div>
          </div>

          <!-- 4) Remove background -->
          <div class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5">
            <div class="flex items-center gap-2.5 mb-1">
              <div class="w-9 h-9 rounded-lg bg-gradient-to-br from-rose-50 to-rose-100 flex items-center justify-center flex-shrink-0">
                <i class="pi pi-eraser text-rose-600 text-sm" aria-hidden="true"></i>
              </div>
              <h3 class="text-sm font-semibold text-gray-900">Xóa nền</h3>
            </div>
            <p class="text-xs text-gray-500 mb-3">Tách chủ thể và loại bỏ nền của ảnh.</p>

            <Button
              label="Xóa nền"
              icon="pi pi-play"
              :loading="removeBgLoading"
              :disabled="!imagePath || removeBgLoading"
              :class="PRIMARY_BTN"
              class="w-full sm:w-auto"
              @click="runRemoveBackground"
            />

            <div class="mt-3">
              <div v-if="removeBgLoading" role="status" class="flex items-center gap-2 text-sm text-gray-500">
                <i class="pi pi-spin pi-spinner" aria-hidden="true"></i> Đang xử lý...
              </div>
              <div v-else-if="removeBgError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-3 py-2.5 text-sm text-red-700">
                {{ removeBgError }}
              </div>
              <div v-else-if="removeBgResult">
                <div class="rounded-xl border border-gray-200 p-3 vision-checkerboard inline-block max-w-full">
                  <img
                    :src="`data:image/png;base64,${removeBgResult}`"
                    alt="Ảnh đã xóa nền"
                    class="max-w-full h-auto rounded-lg"
                  />
                </div>
                <p class="text-xs text-gray-500 mt-1.5">Kết quả là ảnh PNG có nền trong suốt.</p>
              </div>
              <p v-else class="text-xs text-gray-400">Chưa có kết quả.</p>
            </div>
          </div>
        </div>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onUnmounted, ref } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';

const PRIMARY_BTN =
  '!min-h-11 !px-4 !rounded-xl !text-sm !font-medium !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800';

const ACCEPTED_TYPES = 'image/png,image/jpeg,image/webp,image/gif,image/bmp,image/tiff';
const MAX_IMAGE_SIZE = 20 * 1024 * 1024; // 20MB

// --- Upload state ---------------------------------------------------------
const selectedFile = ref<File | null>(null);
const objectUrl = ref('');
const imagePath = ref('');
const uploading = ref(false);
const uploadError = ref('');

const formatFileSize = (bytes: number): string => {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
};

/** Confidences may arrive as 0..1 or 0..100; normalise both to a rounded percent. */
const toPercent = (value: number): string => {
  if (typeof value !== 'number' || Number.isNaN(value)) return '—';
  const pct = value <= 1 ? value * 100 : value;
  return `${Math.round(pct)}%`;
};

const revokePreview = () => {
  if (objectUrl.value) {
    URL.revokeObjectURL(objectUrl.value);
    objectUrl.value = '';
  }
};

// Clearing prior results whenever the working image changes keeps stale output
// from being shown against a different picture.
const resetResults = () => {
  describeResult.value = null;
  describeError.value = '';
  ocrResult.value = null;
  ocrError.value = '';
  classifyResult.value = null;
  classifyError.value = '';
  removeBgResult.value = null;
  removeBgError.value = '';
};

const onFileChange = (event: Event) => {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  // Allow re-selecting the same file later by clearing the native value.
  input.value = '';
  if (!file) return;
  selectImage(file);
};

const selectImage = (file: File) => {
  uploadError.value = '';
  if (!file.type.startsWith('image/')) {
    uploadError.value = 'Vui lòng chọn một tệp ảnh hợp lệ.';
    return;
  }
  if (file.size > MAX_IMAGE_SIZE) {
    uploadError.value = `Ảnh vượt quá dung lượng tối đa 20MB (hiện tại ${formatFileSize(file.size)}).`;
    return;
  }

  revokePreview();
  selectedFile.value = file;
  objectUrl.value = URL.createObjectURL(file);
  imagePath.value = '';
  resetResults();
  void uploadImage();
};

const uploadImage = async () => {
  if (!selectedFile.value) return;
  uploading.value = true;
  uploadError.value = '';
  try {
    const formData = new FormData();
    formData.append('image', selectedFile.value);
    const response = await http.post(ApiFactory.VISION.UPLOAD, formData);
    if (response.ok) {
      const data = (await response.json()) as { imagePath: string; fileName: string };
      imagePath.value = data.imagePath ?? '';
      if (!imagePath.value) uploadError.value = 'Máy chủ không trả về đường dẫn ảnh.';
    } else {
      uploadError.value = await readApiError(response, 'Không thể tải ảnh lên');
    }
  } catch (cause) {
    uploadError.value = errorMessage(cause, 'Không thể tải ảnh lên.');
  } finally {
    uploading.value = false;
  }
};

// --- 1) Describe ----------------------------------------------------------
const describePrompt = ref('Describe this image.');
const describeLoading = ref(false);
const describeError = ref('');
const describeResult = ref<string | null>(null);

const runDescribe = async () => {
  if (!imagePath.value || describeLoading.value) return;
  describeLoading.value = true;
  describeError.value = '';
  describeResult.value = null;
  try {
    const response = await http.post(ApiFactory.VISION.ANALYZE, {
      imagePath: imagePath.value,
      prompt: describePrompt.value.trim() || 'Describe this image.'
    });
    if (response.ok) {
      const data = (await response.json()) as { text: string };
      describeResult.value = data.text ?? '';
    } else describeError.value = await readApiError(response, 'Không thể mô tả ảnh');
  } catch (cause) {
    describeError.value = errorMessage(cause, 'Không thể mô tả ảnh.');
  } finally {
    describeLoading.value = false;
  }
};

// --- 2) OCR ---------------------------------------------------------------
const ocrLoading = ref(false);
const ocrError = ref('');
const ocrResult = ref<string | null>(null);

const runOcr = async () => {
  if (!imagePath.value || ocrLoading.value) return;
  ocrLoading.value = true;
  ocrError.value = '';
  ocrResult.value = null;
  try {
    const response = await http.post(ApiFactory.VISION.OCR, {
      imagePath: imagePath.value,
      includeCoordinates: false
    });
    if (response.ok) {
      const data = (await response.json()) as { text: string; regions: unknown[] };
      ocrResult.value = data.text ?? '';
    } else ocrError.value = await readApiError(response, 'Không thể trích xuất chữ');
  } catch (cause) {
    ocrError.value = errorMessage(cause, 'Không thể trích xuất chữ.');
  } finally {
    ocrLoading.value = false;
  }
};

// --- 3) Classify ----------------------------------------------------------
interface ClassifyResult {
  category: string;
  confidence: number;
}
const categoriesRaw = ref('');
const categoryList = computed(() => [
  ...new Set(
    categoriesRaw.value
      .split(',')
      .map((c) => c.trim())
      .filter(Boolean)
  )
]);
const classifyLoading = ref(false);
const classifyError = ref('');
const classifyResult = ref<ClassifyResult | null>(null);

const runClassify = async () => {
  if (!imagePath.value || classifyLoading.value) return;
  if (categoryList.value.length === 0) {
    classifyError.value = 'Vui lòng nhập ít nhất một danh mục.';
    return;
  }
  classifyLoading.value = true;
  classifyError.value = '';
  classifyResult.value = null;
  try {
    const response = await http.post(ApiFactory.VISION.CLASSIFY, {
      imagePath: imagePath.value,
      categories: categoryList.value
    });
    if (response.ok) classifyResult.value = (await response.json()) as ClassifyResult;
    else classifyError.value = await readApiError(response, 'Không thể phân loại ảnh');
  } catch (cause) {
    classifyError.value = errorMessage(cause, 'Không thể phân loại ảnh.');
  } finally {
    classifyLoading.value = false;
  }
};

// --- 4) Remove background -------------------------------------------------
const removeBgLoading = ref(false);
const removeBgError = ref('');
const removeBgResult = ref<string | null>(null);

const runRemoveBackground = async () => {
  if (!imagePath.value || removeBgLoading.value) return;
  removeBgLoading.value = true;
  removeBgError.value = '';
  removeBgResult.value = null;
  try {
    const response = await http.post(ApiFactory.VISION.REMOVE_BACKGROUND, { imagePath: imagePath.value });
    if (response.ok) {
      const data = (await response.json()) as { base64Image: string };
      removeBgResult.value = data.base64Image ?? '';
      if (!removeBgResult.value) removeBgError.value = 'Máy chủ không trả về ảnh kết quả.';
    } else removeBgError.value = await readApiError(response, 'Không thể xóa nền ảnh');
  } catch (cause) {
    removeBgError.value = errorMessage(cause, 'Không thể xóa nền ảnh.');
  } finally {
    removeBgLoading.value = false;
  }
};

onUnmounted(revokePreview);
</script>

<style scoped>
/* Checkerboard behind the transparent PNG so the removed background reads as
   transparency rather than white. */
.vision-checkerboard {
  background-color: #ffffff;
  background-image:
    linear-gradient(45deg, #e5e7eb 25%, transparent 25%),
    linear-gradient(-45deg, #e5e7eb 25%, transparent 25%),
    linear-gradient(45deg, transparent 75%, #e5e7eb 75%),
    linear-gradient(-45deg, transparent 75%, #e5e7eb 75%);
  background-size: 16px 16px;
  background-position: 0 0, 0 8px, 8px -8px, -8px 0;
}
</style>
