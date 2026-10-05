<template>
  <div class="flex-1 flex flex-col h-full bg-chatgpt-dark overflow-y-auto">
    <!-- Page Header -->
    <div class="sticky top-0 z-10 bg-chatgpt-dark/80 backdrop-blur-xl border-b border-gray-200/60">
      <div class="max-w-4xl mx-auto px-6 py-4">
        <div class="flex items-center gap-4">
          <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-indigo-500 to-violet-600 flex items-center justify-center shadow-md shadow-indigo-500/20">
            <i class="pi pi-database text-white text-sm" aria-hidden="true"></i>
          </div>
          <div>
            <h1 class="text-xl font-bold text-gray-900 tracking-tight">Cơ sở tri thức</h1>
            <p class="text-xs text-gray-500">Nạp nội dung vào cơ sở tri thức của tenant và thử truy vấn RAG.</p>
          </div>
        </div>
      </div>
    </div>

    <!-- Main Content -->
    <div class="flex-1 max-w-4xl mx-auto w-full px-6 py-6 grid gap-6">
      <!-- Section A: Ingest -->
      <section aria-labelledby="kb-ingest-heading" class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5 md:p-6">
        <div class="flex items-center gap-2 mb-1">
          <i class="pi pi-cloud-upload text-indigo-600" aria-hidden="true"></i>
          <h2 id="kb-ingest-heading" class="text-base font-semibold text-gray-900">Nạp tri thức vào cơ sở dữ liệu</h2>
        </div>
        <p class="text-xs text-gray-500 mb-4">Thêm nội dung văn bản vào cơ sở tri thức để trợ lý có thể truy xuất khi trả lời.</p>

        <form @submit.prevent="ingest" class="grid gap-4">
          <div v-if="ingestError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
            {{ ingestError }}
          </div>
          <div v-if="ingestMessage" role="status" class="rounded-xl border border-emerald-200 bg-emerald-50 px-4 py-2.5 text-sm text-emerald-900">
            <i class="pi pi-check-circle mr-1.5" aria-hidden="true"></i>{{ ingestMessage }}
          </div>

          <div class="grid gap-1">
            <label for="kb-source" class="text-sm font-medium text-gray-700">Tên nguồn / tệp</label>
            <InputText
              id="kb-source"
              v-model="ingestForm.fileName"
              :disabled="ingesting"
              maxlength="200"
              placeholder="Ví dụ: chinh-sach-bao-hanh.txt"
              class="w-full"
            />
          </div>

          <div class="grid gap-1">
            <div class="flex items-center justify-between gap-2">
              <label for="kb-content" class="text-sm font-medium text-gray-700">Nội dung</label>
              <span class="text-xs text-gray-400" aria-hidden="true">{{ ingestForm.content.length }} ký tự</span>
            </div>
            <Textarea
              id="kb-content"
              v-model="ingestForm.content"
              rows="10"
              :disabled="ingesting"
              placeholder="Dán nội dung tri thức cần nạp vào đây..."
              class="w-full"
            />
          </div>

          <div class="flex justify-end">
            <Button
              type="submit"
              label="Nạp tri thức"
              icon="pi pi-cloud-upload"
              :loading="ingesting"
              :disabled="ingesting || !ingestForm.content.trim()"
              class="!min-h-11 !px-5 !rounded-xl !text-sm !font-medium !bg-blue-900 !border-blue-900 hover:!bg-blue-950 hover:!border-blue-950"
            />
          </div>
        </form>
      </section>

      <!-- Section B: Query tester -->
      <section aria-labelledby="kb-query-heading" class="bg-white rounded-2xl border border-gray-100 shadow-sm p-5 md:p-6">
        <div class="flex items-center gap-2 mb-1">
          <i class="pi pi-search text-indigo-600" aria-hidden="true"></i>
          <h2 id="kb-query-heading" class="text-base font-semibold text-gray-900">Truy vấn thử</h2>
        </div>
        <p class="text-xs text-gray-500 mb-4">Kiểm tra nhanh câu trả lời mà pipeline RAG tạo ra từ cơ sở tri thức.</p>

        <form @submit.prevent="runQuery" class="grid gap-4">
          <div v-if="queryError" role="alert" class="rounded-xl border border-red-200 bg-red-50 px-4 py-2.5 text-sm text-red-700">
            {{ queryError }}
          </div>

          <div class="grid gap-1">
            <label for="kb-query" class="text-sm font-medium text-gray-700">Câu hỏi</label>
            <Textarea
              id="kb-query"
              v-model="queryForm.query"
              rows="3"
              :disabled="querying"
              placeholder="Ví dụ: Chính sách bảo hành áp dụng trong bao lâu?"
              class="w-full"
            />
          </div>

          <!-- Metadata-aware retrieval filters -->
          <div class="rounded-xl border border-gray-100 bg-gray-50/60 p-4 grid gap-3">
            <div class="flex items-center gap-2">
              <i class="pi pi-filter text-indigo-600 text-xs" aria-hidden="true"></i>
              <span class="text-sm font-medium text-gray-700">Lọc theo metadata (tùy chọn)</span>
            </div>

            <div class="grid gap-1">
              <span class="text-xs font-medium text-gray-600">Loại tài liệu</span>
              <div class="flex flex-wrap gap-1.5">
                <button
                  v-for="opt in DOC_TYPE_OPTIONS"
                  :key="opt.value"
                  type="button"
                  :disabled="querying"
                  @click="toggleDocType(opt.value)"
                  :class="filters.docTypes.includes(opt.value)
                    ? '!bg-blue-900 !text-white border-blue-900'
                    : 'bg-white text-gray-600 border-gray-200 hover:bg-gray-100'"
                  class="px-2.5 py-1 rounded-full border text-xs font-medium transition-colors">
                  {{ opt.label }}
                </button>
              </div>
            </div>

            <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div class="grid gap-1">
                <label for="kb-filter-category" class="text-xs font-medium text-gray-600">Phân loại</label>
                <InputText id="kb-filter-category" v-model="filters.category" :disabled="querying" maxlength="100" placeholder="Ví dụ: Báo cáo quan trắc" class="w-full !text-sm" />
              </div>
              <div class="grid gap-1">
                <label for="kb-filter-tags" class="text-xs font-medium text-gray-600">Thẻ (phân tách bằng dấu phẩy)</label>
                <InputText id="kb-filter-tags" v-model="filters.tags" :disabled="querying" maxlength="500" placeholder="môi-trường, nước-thải" class="w-full !text-sm" />
              </div>
              <div class="grid gap-1">
                <label for="kb-filter-after" class="text-xs font-medium text-gray-600">Tải lên từ ngày</label>
                <input id="kb-filter-after" type="date" v-model="filters.uploadedAfter" :disabled="querying" class="w-full rounded-lg border border-gray-200 px-3 py-2 text-sm" />
              </div>
              <div class="grid gap-1">
                <label for="kb-filter-before" class="text-xs font-medium text-gray-600">Tải lên đến ngày</label>
                <input id="kb-filter-before" type="date" v-model="filters.uploadedBefore" :disabled="querying" class="w-full rounded-lg border border-gray-200 px-3 py-2 text-sm" />
              </div>
            </div>

            <div v-if="hasActiveFilters" class="flex justify-end">
              <Button label="Xóa bộ lọc" icon="pi pi-filter-slash" text size="small" :disabled="querying" @click="clearFilters" />
            </div>
          </div>

          <div class="flex flex-col sm:flex-row sm:items-end gap-3">
            <div class="grid gap-1">
              <label for="kb-topk" class="text-sm font-medium text-gray-700">Số đoạn ngữ cảnh (TopK)</label>
              <InputNumber
                inputId="kb-topk"
                v-model="queryForm.topK"
                :min="1"
                :max="10"
                showButtons
                :disabled="querying"
                class="w-40"
              />
            </div>
            <div class="sm:ml-auto">
              <Button
                type="submit"
                label="Truy vấn"
                icon="pi pi-search"
                :loading="querying"
                :disabled="querying || !queryForm.query.trim()"
                class="!min-h-11 !px-5 !rounded-xl !text-sm !font-medium !bg-blue-900 !border-blue-900 hover:!bg-blue-950 hover:!border-blue-950"
              />
            </div>
          </div>
        </form>

        <!-- Answer -->
        <div v-if="hasAnswer" class="mt-5">
          <h3 class="text-sm font-semibold text-gray-900 mb-2 flex items-center gap-2">
            <i class="pi pi-comment text-indigo-600" aria-hidden="true"></i>Câu trả lời
          </h3>
          <div
            v-if="answer"
            class="rounded-xl border border-gray-200 bg-gray-50 px-4 py-3 text-sm leading-relaxed text-gray-800 whitespace-pre-wrap break-words"
          >{{ answer }}</div>
          <p v-else class="rounded-xl border border-gray-200 bg-gray-50 px-4 py-3 text-sm text-gray-500">
            Không tìm thấy câu trả lời phù hợp trong cơ sở tri thức.
          </p>
        </div>

        <p class="mt-4 text-xs text-gray-500 flex items-start gap-1.5">
          <i class="pi pi-info-circle mt-0.5" aria-hidden="true"></i>
          <span>Truy vấn được thực thi qua pipeline RAG của tenant và yêu cầu đã cấu hình mô hình nhúng (embedding) cùng mô hình sinh (generation).</span>
        </p>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';

// Doc-type vocabulary mirrors the backend DocumentTypeClassifier.
const DOC_TYPE_OPTIONS = [
  { value: 'pdf', label: 'PDF' },
  { value: 'word', label: 'Word' },
  { value: 'excel', label: 'Excel' },
  { value: 'powerpoint', label: 'PowerPoint' },
  { value: 'image', label: 'Hình ảnh' },
  { value: 'text', label: 'Văn bản' },
  { value: 'markdown', label: 'Markdown' }
];

// --- Section A: ingest ------------------------------------------------------
const ingestForm = ref<{ fileName: string; content: string }>({ fileName: '', content: '' });
const ingesting = ref(false);
const ingestError = ref('');
const ingestMessage = ref('');

const ingest = async () => {
  const content = ingestForm.value.content.trim();
  if (!content) {
    ingestError.value = 'Vui lòng nhập nội dung tri thức.';
    return;
  }
  ingestError.value = '';
  ingestMessage.value = '';
  ingesting.value = true;
  try {
    const response = await http.post(ApiFactory.KNOWLEDGE.INGEST, {
      fileName: ingestForm.value.fileName.trim(),
      content
    });
    if (!response.ok) {
      ingestError.value = await readApiError(response, 'Không thể nạp tri thức');
      return;
    }
    const data = await response.json().catch(() => ({})) as { message?: string };
    ingestMessage.value = data.message?.trim() || 'Đã nạp tri thức vào cơ sở dữ liệu.';
    ingestForm.value = { fileName: '', content: '' };
  } catch (cause) {
    ingestError.value = errorMessage(cause, 'Không thể nạp tri thức.');
  } finally {
    ingesting.value = false;
  }
};

// --- Section B: query tester ------------------------------------------------
const queryForm = ref<{ query: string; topK: number | null }>({ query: '', topK: 3 });
const querying = ref(false);
const queryError = ref('');
const answer = ref('');
// Distinguishes "no query run yet" from "query returned an empty answer".
const hasAnswer = ref(false);

// Metadata-aware retrieval filters.
const filters = ref<{ docTypes: string[]; category: string; tags: string; uploadedAfter: string; uploadedBefore: string }>({
  docTypes: [], category: '', tags: '', uploadedAfter: '', uploadedBefore: ''
});

const toggleDocType = (value: string) => {
  const i = filters.value.docTypes.indexOf(value);
  if (i >= 0) filters.value.docTypes.splice(i, 1);
  else filters.value.docTypes.push(value);
};

const hasActiveFilters = computed(() =>
  filters.value.docTypes.length > 0
  || !!filters.value.category.trim()
  || !!filters.value.tags.trim()
  || !!filters.value.uploadedAfter
  || !!filters.value.uploadedBefore);

const clearFilters = () => {
  filters.value = { docTypes: [], category: '', tags: '', uploadedAfter: '', uploadedBefore: '' };
};

const runQuery = async () => {
  const query = queryForm.value.query.trim();
  if (!query) {
    queryError.value = 'Vui lòng nhập câu hỏi.';
    return;
  }
  queryError.value = '';
  querying.value = true;
  try {
    const body: Record<string, unknown> = { query, topK: queryForm.value.topK ?? 3 };
    if (filters.value.docTypes.length) body.docTypes = filters.value.docTypes;
    const category = filters.value.category.trim();
    if (category) body.categories = [category];
    const tags = filters.value.tags.split(',').map(t => t.trim()).filter(Boolean);
    if (tags.length) body.tags = tags;
    if (filters.value.uploadedAfter) body.uploadedAfter = filters.value.uploadedAfter;
    if (filters.value.uploadedBefore) body.uploadedBefore = filters.value.uploadedBefore;

    const response = await http.post(ApiFactory.KNOWLEDGE.QUERY, body);
    if (!response.ok) {
      queryError.value = await readApiError(response, 'Không thể truy vấn tri thức');
      return;
    }
    const data = await response.json().catch(() => ({})) as { answer?: string };
    answer.value = typeof data.answer === 'string' ? data.answer : '';
    hasAnswer.value = true;
  } catch (cause) {
    queryError.value = errorMessage(cause, 'Không thể truy vấn tri thức.');
  } finally {
    querying.value = false;
  }
};
</script>
