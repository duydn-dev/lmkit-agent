<template>
  <div class="flex-1 flex flex-col h-full bg-chatgpt-dark overflow-y-auto">
    <!-- Page Header -->
    <div class="sticky top-0 z-10 bg-chatgpt-dark/80 backdrop-blur-xl border-b border-gray-200/60">
      <div class="max-w-6xl mx-auto px-6 py-4">
        <div class="flex items-center gap-4">
          <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-violet-500 to-purple-600 flex items-center justify-center shadow-md shadow-violet-500/20">
            <i class="pi pi-align-left text-white text-sm" aria-hidden="true"></i>
          </div>
          <div>
            <h1 class="text-xl font-bold text-gray-900 tracking-tight">Phân tích văn bản</h1>
            <p class="text-xs text-gray-500">Bộ công cụ NLP: cảm xúc, thực thể, phân loại, ngôn ngữ, từ khóa, embeddings.</p>
          </div>
        </div>
      </div>
    </div>

    <!-- Main Content -->
    <div class="flex-1 max-w-6xl mx-auto w-full px-6 py-6">
      <!-- Shared input -->
      <section aria-labelledby="text-input-heading" class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5 mb-6">
        <div class="grid gap-1.5">
          <label id="text-input-heading" for="text-input" class="text-sm font-medium text-gray-700">Văn bản đầu vào</label>
          <Textarea
            id="text-input"
            v-model="text"
            rows="8"
            aria-describedby="text-input-note text-input-counter"
            placeholder="Dán hoặc nhập văn bản cần phân tích..."
            class="w-full !text-base"
            :class="overLimit ? '!border-red-300' : ''"
          />
          <div class="flex flex-wrap items-center justify-between gap-2">
            <p id="text-input-note" class="text-xs text-gray-500">
              Các công cụ này chạy trên mô hình đã được cấu hình cho tổ chức của bạn.
            </p>
            <span
              id="text-input-counter"
              class="text-xs tabular-nums"
              :class="overLimit ? 'text-red-700 font-semibold' : 'text-gray-500'"
            >{{ numberVi(charCount) }} / {{ maxCharsLabel }} ký tự</span>
          </div>
        </div>

        <div
          v-if="overLimit"
          role="alert"
          class="mt-3 rounded-xl border border-amber-200 bg-amber-50 px-4 py-2.5 text-sm text-amber-800"
        >
          Văn bản vượt quá giới hạn {{ maxCharsLabel }} ký tự. Vui lòng rút ngắn để sử dụng các công cụ.
        </div>
      </section>

      <div class="grid gap-4">
        <!-- 1) Analyze (full width) -->
        <section aria-labelledby="tool-analyze-heading" class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5">
          <div class="flex flex-wrap items-center justify-between gap-3 mb-1">
            <div class="flex items-center gap-2.5 min-w-0">
              <div class="w-9 h-9 rounded-lg bg-gradient-to-br from-sky-50 to-sky-100 flex items-center justify-center flex-shrink-0">
                <i class="pi pi-chart-bar text-sky-600 text-sm" aria-hidden="true"></i>
              </div>
              <h2 id="tool-analyze-heading" class="text-sm font-semibold text-gray-900">Phân tích tổng hợp</h2>
            </div>
            <Button
              label="Phân tích"
              icon="pi pi-play"
              :loading="analyzeLoading"
              :disabled="!canRun || analyzeLoading"
              :class="PRIMARY_BTN"
              @click="runAnalyze"
            />
          </div>
          <p class="text-xs text-gray-500 mb-3">Cảm xúc, thực thể và ẩn thông tin nhạy cảm trong một lần gọi.</p>

          <div>
            <div v-if="analyzeLoading" role="status" class="flex items-center gap-2 text-sm text-gray-500">
              <i class="pi pi-spin pi-spinner" aria-hidden="true"></i> Đang phân tích...
            </div>
            <div v-else-if="analyzeError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-3 py-2.5 text-sm text-red-700">
              {{ analyzeError }}
            </div>
            <div v-else-if="analyzeResult" class="grid gap-3">
              <div class="flex flex-wrap items-center gap-2">
                <span class="text-xs font-medium text-gray-500">Cảm xúc:</span>
                <span
                  class="inline-flex items-center px-2.5 py-1 rounded-full text-[11px] font-semibold border"
                  :class="sentimentClasses(analyzeResult.sentiment)"
                >{{ analyzeResult.sentiment || '—' }}</span>
                <span class="text-xs text-gray-500">Độ tin cậy {{ toPercent(analyzeResult.sentimentConfidence) }}</span>
              </div>

              <div>
                <p class="text-xs font-medium text-gray-500 mb-1.5">Thực thể trích xuất</p>
                <ul v-if="(analyzeResult.extractedEntities || []).length" class="flex flex-wrap gap-1.5" aria-label="Danh sách thực thể">
                  <li
                    v-for="(entity, index) in analyzeResult.extractedEntities"
                    :key="`${entity}-${index}`"
                    class="inline-flex items-center px-2.5 py-1 rounded-full text-[11px] font-semibold bg-sky-50 text-sky-900 border border-sky-200"
                  >{{ entity }}</li>
                </ul>
                <p v-else class="text-xs text-gray-400">Không phát hiện thực thể.</p>
              </div>

              <div>
                <p class="text-xs font-medium text-gray-500 mb-1.5">Văn bản đã ẩn thông tin nhạy cảm</p>
                <p class="text-sm text-gray-800 whitespace-pre-wrap break-words rounded-lg bg-gray-50 border border-gray-100 p-3">{{ analyzeResult.redactedText || '—' }}</p>
              </div>
            </div>
            <p v-else class="text-xs text-gray-400">Chưa có kết quả.</p>
          </div>
        </section>

        <div class="grid grid-cols-1 lg:grid-cols-2 gap-4 items-start">
          <!-- 2) Classify -->
          <section aria-labelledby="tool-classify-heading" class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5">
            <div class="flex items-center gap-2.5 mb-1">
              <div class="w-9 h-9 rounded-lg bg-gradient-to-br from-violet-50 to-violet-100 flex items-center justify-center flex-shrink-0">
                <i class="pi pi-tags text-violet-600 text-sm" aria-hidden="true"></i>
              </div>
              <h2 id="tool-classify-heading" class="text-sm font-semibold text-gray-900">Phân loại</h2>
            </div>
            <p class="text-xs text-gray-500 mb-3">Gán văn bản vào một trong các danh mục bạn cung cấp.</p>

            <div class="grid gap-1.5 mb-3">
              <label for="text-categories" class="text-xs font-medium text-gray-700">Danh mục (phân tách bằng dấu phẩy)</label>
              <InputText
                id="text-categories"
                v-model="categoriesRaw"
                placeholder="Ví dụ: Thể thao, Kinh tế, Giải trí"
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
              label="Phân loại"
              icon="pi pi-play"
              :loading="classifyLoading"
              :disabled="!canRun || classifyLoading || categoryList.length === 0"
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
          </section>

          <!-- 3) Detect language -->
          <section aria-labelledby="tool-language-heading" class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5">
            <div class="flex items-center gap-2.5 mb-1">
              <div class="w-9 h-9 rounded-lg bg-gradient-to-br from-emerald-50 to-emerald-100 flex items-center justify-center flex-shrink-0">
                <i class="pi pi-language text-emerald-600 text-sm" aria-hidden="true"></i>
              </div>
              <h2 id="tool-language-heading" class="text-sm font-semibold text-gray-900">Nhận diện ngôn ngữ</h2>
            </div>
            <p class="text-xs text-gray-500 mb-3">Xác định ngôn ngữ chính của văn bản.</p>

            <Button
              label="Nhận diện ngôn ngữ"
              icon="pi pi-play"
              :loading="languageLoading"
              :disabled="!canRun || languageLoading"
              :class="PRIMARY_BTN"
              class="w-full sm:w-auto"
              @click="runDetectLanguage"
            />

            <div class="mt-3">
              <div v-if="languageLoading" role="status" class="flex items-center gap-2 text-sm text-gray-500">
                <i class="pi pi-spin pi-spinner" aria-hidden="true"></i> Đang nhận diện...
              </div>
              <div v-else-if="languageError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-3 py-2.5 text-sm text-red-700">
                {{ languageError }}
              </div>
              <div v-else-if="languageResult !== null">
                <p v-if="languageResult" class="text-sm text-gray-800">Ngôn ngữ nhận diện: <span class="font-semibold text-gray-900">{{ languageResult }}</span></p>
                <p v-else class="text-xs text-gray-400">Không xác định được ngôn ngữ.</p>
              </div>
              <p v-else class="text-xs text-gray-400">Chưa có kết quả.</p>
            </div>
          </section>

          <!-- 4) Keywords -->
          <section aria-labelledby="tool-keywords-heading" class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5">
            <div class="flex items-center gap-2.5 mb-1">
              <div class="w-9 h-9 rounded-lg bg-gradient-to-br from-amber-50 to-amber-100 flex items-center justify-center flex-shrink-0">
                <i class="pi pi-hashtag text-amber-600 text-sm" aria-hidden="true"></i>
              </div>
              <h2 id="tool-keywords-heading" class="text-sm font-semibold text-gray-900">Trích xuất từ khóa</h2>
            </div>
            <p class="text-xs text-gray-500 mb-3">Lấy ra các từ khóa nổi bật nhất của văn bản.</p>

            <Button
              label="Trích xuất từ khóa"
              icon="pi pi-play"
              :loading="keywordsLoading"
              :disabled="!canRun || keywordsLoading"
              :class="PRIMARY_BTN"
              class="w-full sm:w-auto"
              @click="runKeywords"
            />

            <div class="mt-3">
              <div v-if="keywordsLoading" role="status" class="flex items-center gap-2 text-sm text-gray-500">
                <i class="pi pi-spin pi-spinner" aria-hidden="true"></i> Đang trích xuất...
              </div>
              <div v-else-if="keywordsError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-3 py-2.5 text-sm text-red-700">
                {{ keywordsError }}
              </div>
              <div v-else-if="keywordsResult">
                <ul v-if="(keywordsResult.keywords || []).length" class="flex flex-wrap gap-1.5" aria-label="Danh sách từ khóa">
                  <li
                    v-for="(keyword, index) in keywordsResult.keywords"
                    :key="`${keyword}-${index}`"
                    class="inline-flex items-center px-2.5 py-1 rounded-full text-[11px] font-semibold bg-amber-50 text-amber-900 border border-amber-200"
                  >{{ keyword }}</li>
                </ul>
                <p v-else class="text-xs text-gray-400">Không tìm thấy từ khóa.</p>
                <p v-if="(keywordsResult.keywords || []).length" class="text-xs text-gray-500 mt-2">Độ tin cậy {{ toPercent(keywordsResult.confidence) }}</p>
              </div>
              <p v-else class="text-xs text-gray-400">Chưa có kết quả.</p>
            </div>
          </section>

          <!-- 5) Embeddings -->
          <section aria-labelledby="tool-embeddings-heading" class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5">
            <div class="flex items-center gap-2.5 mb-1">
              <div class="w-9 h-9 rounded-lg bg-gradient-to-br from-rose-50 to-rose-100 flex items-center justify-center flex-shrink-0">
                <i class="pi pi-sliders-h text-rose-600 text-sm" aria-hidden="true"></i>
              </div>
              <h2 id="tool-embeddings-heading" class="text-sm font-semibold text-gray-900">Sinh embeddings</h2>
            </div>
            <p class="text-xs text-gray-500 mb-3">Tạo vector số biểu diễn ngữ nghĩa của văn bản.</p>

            <Button
              label="Sinh embeddings"
              icon="pi pi-play"
              :loading="embeddingsLoading"
              :disabled="!canRun || embeddingsLoading"
              :class="PRIMARY_BTN"
              class="w-full sm:w-auto"
              @click="runEmbeddings"
            />

            <div class="mt-3">
              <div v-if="embeddingsLoading" role="status" class="flex items-center gap-2 text-sm text-gray-500">
                <i class="pi pi-spin pi-spinner" aria-hidden="true"></i> Đang sinh embeddings...
              </div>
              <div v-else-if="embeddingsError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-3 py-2.5 text-sm text-red-700">
                {{ embeddingsError }}
              </div>
              <div v-else-if="embeddingsResult">
                <p class="text-sm text-gray-700">Số chiều: <span class="font-semibold text-gray-900 tabular-nums">{{ numberVi(embeddingsResult.length) }}</span></p>
                <div v-if="embeddingsResult.length" class="mt-2 overflow-x-auto rounded-lg bg-gray-900 px-3 py-2">
                  <code class="text-xs font-mono text-gray-100 whitespace-nowrap">[{{ embeddingPreview.join(', ') }}{{ embeddingsResult.length > embeddingPreview.length ? ', …' : '' }}]</code>
                </div>
                <p class="text-xs text-gray-500 mt-1.5">Hiển thị {{ embeddingPreview.length }} giá trị đầu tiên; vector đầy đủ rất lớn nên không hiển thị toàn bộ.</p>
              </div>
              <p v-else class="text-xs text-gray-400">Chưa có kết quả.</p>
            </div>
          </section>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';

const PRIMARY_BTN =
  '!min-h-11 !px-4 !rounded-xl !text-sm !font-medium !bg-sky-700 !border-sky-700 hover:!bg-sky-800 hover:!border-sky-800';

const MAX_CHARS = 50000;
const maxCharsLabel = MAX_CHARS.toLocaleString('vi-VN');

const text = ref('');
const charCount = computed(() => text.value.length);
const overLimit = computed(() => charCount.value > MAX_CHARS);
const hasText = computed(() => text.value.trim().length > 0);
// Every action is gated on this: non-empty text that is within the cap.
const canRun = computed(() => hasText.value && !overLimit.value);

const numberVi = (value: number): string => value.toLocaleString('vi-VN');

/** Confidences may arrive as 0..1 or 0..100; normalise both to a rounded percent. */
const toPercent = (value: number): string => {
  if (typeof value !== 'number' || Number.isNaN(value)) return '—';
  const pct = value <= 1 ? value * 100 : value;
  return `${Math.round(pct)}%`;
};

const sentimentClasses = (sentiment: string): string => {
  const value = (sentiment || '').toLowerCase();
  if (/(pos|tích cực|good|happy|vui)/.test(value)) return 'bg-emerald-50 text-emerald-900 border-emerald-200';
  if (/(neg|tiêu cực|bad|angry|sad|buồn|giận)/.test(value)) return 'bg-red-50 text-red-800 border-red-200';
  if (/(neu|trung)/.test(value)) return 'bg-gray-100 text-gray-700 border-gray-200';
  return 'bg-sky-50 text-sky-900 border-sky-200';
};

// --- 1) Analyze -----------------------------------------------------------
interface AnalyzeResult {
  sentiment: string;
  sentimentConfidence: number;
  extractedEntities: string[];
  redactedText: string;
}
const analyzeLoading = ref(false);
const analyzeError = ref('');
const analyzeResult = ref<AnalyzeResult | null>(null);

const runAnalyze = async () => {
  if (!canRun.value || analyzeLoading.value) return;
  analyzeLoading.value = true;
  analyzeError.value = '';
  analyzeResult.value = null;
  try {
    const response = await http.post(ApiFactory.TEXT_ANALYSIS.ANALYZE, { text: text.value });
    if (response.ok) analyzeResult.value = (await response.json()) as AnalyzeResult;
    else analyzeError.value = await readApiError(response, 'Không thể phân tích văn bản');
  } catch (cause) {
    analyzeError.value = errorMessage(cause, 'Không thể phân tích văn bản.');
  } finally {
    analyzeLoading.value = false;
  }
};

// --- 2) Classify ----------------------------------------------------------
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
  if (!canRun.value || classifyLoading.value) return;
  if (categoryList.value.length === 0) {
    classifyError.value = 'Vui lòng nhập ít nhất một danh mục.';
    return;
  }
  classifyLoading.value = true;
  classifyError.value = '';
  classifyResult.value = null;
  try {
    const response = await http.post(ApiFactory.TEXT_ANALYSIS.CLASSIFY, {
      text: text.value,
      categories: categoryList.value
    });
    if (response.ok) classifyResult.value = (await response.json()) as ClassifyResult;
    else classifyError.value = await readApiError(response, 'Không thể phân loại văn bản');
  } catch (cause) {
    classifyError.value = errorMessage(cause, 'Không thể phân loại văn bản.');
  } finally {
    classifyLoading.value = false;
  }
};

// --- 3) Detect language ---------------------------------------------------
const languageLoading = ref(false);
const languageError = ref('');
const languageResult = ref<string | null>(null);

const runDetectLanguage = async () => {
  if (!canRun.value || languageLoading.value) return;
  languageLoading.value = true;
  languageError.value = '';
  languageResult.value = null;
  try {
    const response = await http.post(ApiFactory.TEXT_ANALYSIS.DETECT_LANGUAGE, { text: text.value });
    if (response.ok) {
      const data = (await response.json()) as { language: string };
      languageResult.value = data.language ?? '';
    } else languageError.value = await readApiError(response, 'Không thể nhận diện ngôn ngữ');
  } catch (cause) {
    languageError.value = errorMessage(cause, 'Không thể nhận diện ngôn ngữ.');
  } finally {
    languageLoading.value = false;
  }
};

// --- 4) Keywords ----------------------------------------------------------
interface KeywordsResult {
  keywords: string[];
  confidence: number;
}
const keywordsLoading = ref(false);
const keywordsError = ref('');
const keywordsResult = ref<KeywordsResult | null>(null);

const runKeywords = async () => {
  if (!canRun.value || keywordsLoading.value) return;
  keywordsLoading.value = true;
  keywordsError.value = '';
  keywordsResult.value = null;
  try {
    const response = await http.post(ApiFactory.TEXT_ANALYSIS.KEYWORDS, { text: text.value });
    if (response.ok) keywordsResult.value = (await response.json()) as KeywordsResult;
    else keywordsError.value = await readApiError(response, 'Không thể trích xuất từ khóa');
  } catch (cause) {
    keywordsError.value = errorMessage(cause, 'Không thể trích xuất từ khóa.');
  } finally {
    keywordsLoading.value = false;
  }
};

// --- 5) Embeddings --------------------------------------------------------
const embeddingsLoading = ref(false);
const embeddingsError = ref('');
const embeddingsResult = ref<number[] | null>(null);
const embeddingPreview = computed(() =>
  (embeddingsResult.value ?? []).slice(0, 12).map((v) => Number(v).toFixed(4))
);

const runEmbeddings = async () => {
  if (!canRun.value || embeddingsLoading.value) return;
  embeddingsLoading.value = true;
  embeddingsError.value = '';
  embeddingsResult.value = null;
  try {
    const response = await http.post(ApiFactory.TEXT_ANALYSIS.EMBEDDINGS, { text: text.value });
    if (response.ok) {
      const data = (await response.json()) as { embeddings: number[] };
      embeddingsResult.value = Array.isArray(data.embeddings) ? data.embeddings : [];
    } else embeddingsError.value = await readApiError(response, 'Không thể sinh embeddings');
  } catch (cause) {
    embeddingsError.value = errorMessage(cause, 'Không thể sinh embeddings.');
  } finally {
    embeddingsLoading.value = false;
  }
};
</script>
