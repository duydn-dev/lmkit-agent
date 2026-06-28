<template>
  <div class="flex-1 flex flex-col h-full bg-chatgpt-dark overflow-y-auto">
    <!-- Page Header -->
    <div class="sticky top-0 z-10 bg-chatgpt-dark/80 backdrop-blur-xl border-b border-gray-200/60">
      <div class="max-w-7xl mx-auto px-6 py-4">
        <div class="flex items-center justify-between">
          <div class="flex items-center gap-4">
            <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-blue-500 to-blue-600 flex items-center justify-center shadow-md shadow-blue-500/20">
              <i class="pi pi-file text-white text-sm"></i>
            </div>
            <div>
              <h1 class="text-xl font-bold text-gray-900 tracking-tight">Kho Tài Liệu</h1>
              <p class="text-xs text-gray-500">Quản lý tài liệu cho hệ thống RAG</p>
            </div>
          </div>
          <Button
            @click="showUploadDialog = true"
            label="Tải tài liệu lên"
            icon="pi pi-cloud-upload"
            severity="info"
            class="!px-4 !py-2.5 !rounded-xl !text-sm !font-medium !shadow-md !shadow-blue-500/20"
          />
        </div>
      </div>
    </div>

    <!-- Main Content -->
    <div class="flex-1 max-w-7xl mx-auto w-full px-6 py-6">
      <!-- Stats Cards -->
      <div class="grid grid-cols-2 md:grid-cols-4 gap-4 mb-8">
        <div class="bg-white rounded-2xl border border-gray-100 p-5 shadow-sm hover:shadow-md hover:border-gray-200 transition-all duration-300">
          <div class="flex items-center justify-between mb-2">
            <div class="w-9 h-9 rounded-lg bg-gradient-to-br from-blue-50 to-blue-100 flex items-center justify-center">
              <i class="pi pi-file text-blue-500 text-sm"></i>
            </div>
          </div>
          <p class="text-2xl font-bold text-gray-900 tabular-nums">{{ documents.length }}</p>
          <p class="text-xs text-gray-500 font-medium mt-0.5">Tổng tài liệu</p>
        </div>

        <div class="bg-white rounded-2xl border border-gray-100 p-5 shadow-sm hover:shadow-md hover:border-gray-200 transition-all duration-300">
          <div class="flex items-center justify-between mb-2">
            <div class="w-9 h-9 rounded-lg bg-gradient-to-br from-emerald-50 to-emerald-100 flex items-center justify-center">
              <i class="pi pi-check-circle text-emerald-500 text-sm"></i>
            </div>
          </div>
          <p class="text-2xl font-bold text-gray-900 tabular-nums">{{ vectorizedCount }}</p>
          <p class="text-xs text-gray-500 font-medium mt-0.5">Đã vector hóa</p>
        </div>

        <div class="bg-white rounded-2xl border border-gray-100 p-5 shadow-sm hover:shadow-md hover:border-gray-200 transition-all duration-300">
          <div class="flex items-center justify-between mb-2">
            <div class="w-9 h-9 rounded-lg bg-gradient-to-br from-amber-50 to-amber-100 flex items-center justify-center">
              <i class="pi pi-spinner text-amber-500 text-sm"></i>
            </div>
          </div>
          <p class="text-2xl font-bold text-gray-900 tabular-nums">{{ pendingCount }}</p>
          <p class="text-xs text-gray-500 font-medium mt-0.5">Đang chờ xử lý</p>
        </div>

        <div class="bg-white rounded-2xl border border-gray-100 p-5 shadow-sm hover:shadow-md hover:border-gray-200 transition-all duration-300">
          <div class="flex items-center justify-between mb-2">
            <div class="w-9 h-9 rounded-lg bg-gradient-to-br from-purple-50 to-purple-100 flex items-center justify-center">
              <i class="pi pi-database text-purple-500 text-sm"></i>
            </div>
          </div>
          <p class="text-2xl font-bold text-gray-900 tabular-nums">{{ vectorizedCount }}</p>
          <p class="text-xs text-gray-500 font-medium mt-0.5">Chunk trong KB</p>
        </div>
      </div>

      <!-- Toolbar -->
      <div class="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4 mb-5">
        <div class="flex items-center gap-3">
          <div class="relative">
            <i class="pi pi-search absolute left-3.5 top-1/2 -translate-y-1/2 text-gray-400 text-sm"></i>
            <InputText
              v-model="searchQuery"
              placeholder="Tìm kiếm tài liệu..."
              class="!pl-9 !pr-4 !py-2.5 !rounded-xl !border-gray-200 !bg-white !shadow-sm !text-sm !w-72 focus:!border-blue-400 focus:!ring-2 focus:!ring-blue-100 transition-all"
            />
          </div>
          <Button
            icon="pi pi-filter-slash"
            class="!w-10 !h-10 !rounded-xl !border-gray-200 !bg-white !text-gray-500 !shadow-sm hover:!bg-gray-50"
            @click="searchQuery = ''"
            v-tooltip.top="'Xóa bộ lọc'"
          />
        </div>
        <div class="flex items-center gap-2">
          <Button
            :icon="viewMode === 'grid' ? 'pi pi-list' : 'pi pi-th-large'"
            class="!w-10 !h-10 !rounded-xl !border-gray-200 !bg-white !text-gray-500 !shadow-sm hover:!bg-gray-50"
            @click="viewMode = viewMode === 'grid' ? 'table' : 'grid'"
            v-tooltip.top="viewMode === 'grid' ? 'Chế độ bảng' : 'Chế độ lưới'"
          />
          <span class="text-xs text-gray-400 font-medium">{{ filteredDocuments.length }} tài liệu</span>
        </div>
      </div>

      <!-- Grid View -->
      <div v-if="viewMode === 'grid'">
        <div v-if="filteredDocuments.length === 0" class="flex flex-col items-center justify-center py-20 text-center">
          <div class="w-20 h-20 rounded-2xl bg-gradient-to-br from-gray-100 to-gray-200 flex items-center justify-center mb-5 shadow-inner">
            <i class="pi pi-file text-3xl text-gray-300"></i>
          </div>
          <h3 class="text-lg font-semibold text-gray-600 mb-1">Chưa có tài liệu nào</h3>
          <p class="text-sm text-gray-400 max-w-xs">Hãy tải file lên để bắt đầu xây dựng kho tri thức của bạn.</p>
        </div>
        <div v-else class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
          <div
            v-for="(doc, index) in filteredDocuments"
            :key="doc.id"
            class="group relative bg-white rounded-2xl border border-gray-100 shadow-sm hover:shadow-xl hover:border-gray-200 transition-all duration-300 overflow-hidden animate-fade-in-up cursor-pointer"
            :style="{ animationDelay: `${index * 50}ms` }"
          >
            <!-- Top accent bar -->
            <div class="h-1.5 w-full bg-gradient-to-r from-blue-500 via-cyan-400 to-blue-300 opacity-0 group-hover:opacity-100 transition-opacity duration-300"></div>
            
            <div class="p-5">
              <!-- File icon -->
              <div class="flex items-start justify-between mb-4">
                <div :class="getFileIconClass(doc.fileName)" class="w-12 h-12 rounded-xl flex items-center justify-center shadow-sm transition-transform duration-300 group-hover:scale-110">
                  <i :class="getFileIcon(doc.fileName)" class="text-lg"></i>
                </div>
                <span class="text-[10px] text-gray-400 font-mono bg-gray-50 px-2 py-1 rounded-md">{{ getFileExt(doc.fileName) }}</span>
              </div>

              <!-- File info -->
              <h4 class="font-semibold text-gray-800 text-sm leading-snug mb-1 line-clamp-2 group-hover:text-blue-600 transition-colors">{{ doc.fileName }}</h4>
              <p class="text-xs text-gray-400 mb-4">{{ formatDate(doc.uploadedAt) }}</p>

              <!-- Status bar -->
              <div class="flex items-center justify-between">
                <div class="flex items-center gap-2">
                  <span v-if="doc.isVectorized" class="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-[11px] font-semibold bg-emerald-50 text-emerald-600 border border-emerald-200">
                    <span class="w-1.5 h-1.5 rounded-full bg-emerald-500"></span>
                    Hoàn tất
                  </span>
                  <span v-else class="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-[11px] font-semibold bg-amber-50 text-amber-600 border border-amber-200">
                    <span class="w-1.5 h-1.5 rounded-full bg-amber-500 animate-pulse"></span>
                    Đang xử lý
                  </span>
                </div>
                <Button
                  icon="pi pi-trash"
                  class="!w-8 !h-8 !rounded-xl !bg-transparent !text-gray-300 hover:!text-red-500 hover:!bg-red-50 !border-none transition-all"
                  @click.stop="confirmDelete(doc)"
                  v-tooltip.top="'Xóa tài liệu'"
                />
              </div>
            </div>

            <!-- Hover glow effect -->
            <div class="absolute -inset-0.5 bg-gradient-to-r from-blue-500/0 via-blue-500/5 to-cyan-500/0 rounded-2xl opacity-0 group-hover:opacity-100 transition-opacity duration-300 -z-10 blur-xl"></div>
          </div>
        </div>
      </div>

      <!-- Table View -->
      <div v-else class="bg-white rounded-2xl border border-gray-100 shadow-sm overflow-hidden">
        <DataTable
          :value="filteredDocuments"
          paginator
          :rows="10"
          dataKey="id"
          :rowHover="true"
          class="p-datatable-sm"
          emptyMessage="Không tìm thấy tài liệu nào"
          :pt="{
            wrapper: { class: 'rounded-2xl' },
            header: { class: 'bg-gray-50/80 !rounded-t-2xl !border-b !border-gray-100' },
            paginator: { class: '!border-t !border-gray-100 !bg-gray-50/50 !rounded-b-2xl' }
          }"
        >
          <Column field="fileName" header="Tên tài liệu" sortable>
            <template #body="{ data }">
              <div class="flex items-center gap-3">
                <div :class="getFileIconClass(data.fileName)" class="w-9 h-9 rounded-lg flex items-center justify-center shadow-xs">
                  <i :class="getFileIcon(data.fileName)" class="text-sm"></i>
                </div>
                <div>
                  <span class="font-medium text-gray-800 text-sm">{{ data.fileName }}</span>
                </div>
              </div>
            </template>
          </Column>

          <Column field="uploadedAt" header="Ngày tải lên" sortable>
            <template #body="{ data }">
              <div class="flex items-center gap-2 text-sm text-gray-500">
                <i class="pi pi-calendar text-gray-300 text-xs"></i>
                <span>{{ formatDate(data.uploadedAt) }}</span>
              </div>
            </template>
          </Column>

          <Column field="isVectorized" header="Trạng thái" sortable>
            <template #body="{ data }">
              <span v-if="data.isVectorized" class="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-medium bg-emerald-50 text-emerald-600 border border-emerald-200">
                <span class="w-1.5 h-1.5 rounded-full bg-emerald-500"></span>
                Hoàn tất
              </span>
              <span v-else class="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-medium bg-amber-50 text-amber-600 border border-amber-200">
                <span class="w-1.5 h-1.5 rounded-full bg-amber-500 animate-pulse"></span>
                Đang xử lý
              </span>
            </template>
          </Column>

          <Column header="Thao tác" :exportable="false" style="min-width: 8rem">
            <template #body="{ data }">
              <div class="flex items-center gap-1">
                <Button icon="pi pi-eye" class="!w-8 !h-8 !rounded-lg !bg-transparent !text-gray-400 hover:!text-blue-500 hover:!bg-blue-50 !border-none" v-tooltip.top="'Xem chi tiết'" />
                <Button icon="pi pi-trash" class="!w-8 !h-8 !rounded-lg !bg-transparent !text-gray-400 hover:!text-red-500 hover:!bg-red-50 !border-none" @click="confirmDelete(data)" v-tooltip.top="'Xóa tài liệu'" />
              </div>
            </template>
          </Column>
        </DataTable>
      </div>
    </div>

    <!-- Upload Dialog -->
    <Dialog
      v-model:visible="showUploadDialog"
      modal
      :style="{ width: '480px' }"
      :breakpoints="{ '575px': '90vw' }"
      :pt="{
        root: '!rounded-2xl !border-none',
        mask: '!backdrop-blur-sm !bg-black/30',
        header: '!rounded-t-2xl !border-b !border-gray-100 !pb-4',
        content: '!p-0',
        footer: '!rounded-b-2xl !border-t !border-gray-100'
      }"
    >
      <template #header>
        <div class="flex items-center gap-3">
          <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-blue-400 to-blue-600 flex items-center justify-center shadow-md">
            <i class="pi pi-cloud-upload text-white text-sm"></i>
          </div>
          <div>
            <h3 class="font-semibold text-gray-800 text-base m-0">Tải tài liệu lên</h3>
            <p class="text-xs text-gray-400 m-0 mt-0.5">Hỗ trợ PDF, DOCX, TXT, MD — tối đa 50MB</p>
          </div>
        </div>
      </template>

      <div class="p-6">
        <!-- Drop zone -->
        <div
          class="relative rounded-2xl border-2 border-dashed transition-all duration-300 cursor-pointer overflow-hidden"
          :class="selectedFile
            ? 'border-blue-300 bg-blue-50/50'
            : isDragging
              ? 'border-blue-400 bg-blue-50 scale-[1.02]'
              : 'border-gray-200 bg-gray-50/50 hover:border-blue-300 hover:bg-blue-50/30'"
          @dragenter.prevent="isDragging = true"
          @dragover.prevent="isDragging = true"
          @dragleave.prevent="isDragging = false"
          @drop.prevent="onDrop"
        >
          <input
            type="file"
            @change="handleFileChange"
            @dragenter="isDragging = true"
            @dragleave="isDragging = false"
            class="absolute inset-0 w-full h-full opacity-0 cursor-pointer z-10"
            accept=".pdf,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.txt,.md"
          />

          <div v-if="!selectedFile" class="flex flex-col items-center py-12 px-8">
            <!-- Animated upload illustration -->
            <div class="relative mb-5">
              <div class="w-20 h-20 rounded-2xl bg-gradient-to-br from-blue-50 to-blue-100 flex items-center justify-center shadow-inner transition-transform duration-300" :class="{ 'scale-110': isDragging }">
                <i class="pi pi-cloud-upload text-3xl text-blue-400 transition-all duration-300" :class="{ 'text-blue-500 scale-110': isDragging }"></i>
              </div>
              <!-- Dots animation -->
              <span v-if="isDragging" class="absolute -top-1 -right-1 flex h-6 w-6">
                <span class="animate-ping absolute inline-flex h-full w-full rounded-full bg-blue-400 opacity-75"></span>
                <span class="relative inline-flex rounded-full h-6 w-6 bg-blue-500 items-center justify-center">
                  <i class="pi pi-plus text-white text-[10px] font-bold"></i>
                </span>
              </span>
            </div>
            <p class="text-gray-700 font-medium text-sm mb-1">
              <span class="text-blue-500 font-semibold">Nhấp để chọn</span> hoặc kéo thả file vào đây
            </p>
            <p class="text-xs text-gray-400">PDF, DOCX, TXT, MD — Kích thước tối đa 50MB</p>
          </div>

          <!-- Selected file preview -->
          <div v-else class="flex items-center gap-4 p-5">
            <div :class="getFileIconClass(selectedFile.name)" class="w-14 h-14 rounded-xl flex items-center justify-center shadow-sm shrink-0">
              <i :class="getFileIcon(selectedFile.name)" class="text-xl"></i>
            </div>
            <div class="flex-1 min-w-0">
              <p class="font-medium text-gray-800 text-sm truncate">{{ selectedFile.name }}</p>
              <p class="text-xs text-gray-400 mt-0.5">{{ formatFileSize(selectedFile.size) }}</p>
              <div class="mt-2 h-1.5 w-full bg-gray-100 rounded-full overflow-hidden">
                <div class="h-full bg-gradient-to-r from-blue-400 to-cyan-400 rounded-full w-full"></div>
              </div>
            </div>
            <button
              class="w-8 h-8 rounded-lg bg-gray-100 hover:bg-red-50 text-gray-400 hover:text-red-500 flex items-center justify-center transition-all shrink-0 z-20"
              @click.stop="selectedFile = null"
            >
              <i class="pi pi-times text-xs"></i>
            </button>
          </div>
        </div>

        <!-- Uploading progress -->
        <div v-if="uploading" class="mt-5 p-4 rounded-xl bg-gradient-to-r from-blue-50 to-cyan-50 border border-blue-100">
          <div class="flex items-center gap-3">
            <div class="w-10 h-10 rounded-full bg-gradient-to-br from-blue-400 to-cyan-400 flex items-center justify-center animate-pulse">
              <i class="pi pi-upload text-white text-sm"></i>
            </div>
            <div class="flex-1 min-w-0">
              <div class="flex justify-between text-sm mb-1.5">
                <span class="font-medium text-gray-700">Đang tải lên & xử lý...</span>
                <span class="text-blue-500 font-semibold text-xs">{{ uploadProgress }}%</span>
              </div>
              <div class="h-2 w-full bg-blue-100 rounded-full overflow-hidden">
                <div
                  class="h-full bg-gradient-to-r from-blue-400 to-cyan-400 rounded-full transition-all duration-500 ease-out"
                  :style="{ width: uploadProgress + '%' }"
                ></div>
              </div>
              <p class="text-xs text-gray-400 mt-1.5">Đang vector hóa tài liệu để sẵn sàng cho RAG...</p>
            </div>
          </div>
        </div>
      </div>

      <template #footer>
        <div class="flex items-center justify-end gap-2 px-6 py-4">
          <Button
            label="Hủy"
            text
            severity="secondary"
            @click="showUploadDialog = false"
            :disabled="uploading"
            class="!px-4 !py-2 !rounded-xl !text-sm !font-medium"
          />
          <Button
            label="Tải lên"
            icon="pi pi-upload"
            :loading="uploading"
            @click="uploadFile"
            :disabled="!selectedFile || uploading"
            class="!px-5 !py-2 !rounded-xl !text-sm !font-medium !bg-gradient-to-r !from-blue-500 !to-blue-600 !border-none hover:!from-blue-600 hover:!to-blue-700 !shadow-lg !shadow-blue-500/20"
          />
        </div>
      </template>
    </Dialog>

    <!-- Delete Confirmation Dialog -->
    <Dialog
      v-model:visible="showDeleteDialog"
      modal
      :style="{ width: '400px' }"
      :breakpoints="{ '575px': '90vw' }"
      :pt="{
        root: '!rounded-2xl !border-none',
        mask: '!backdrop-blur-sm !bg-black/30',
        header: '!rounded-t-2xl !border-b !border-gray-100 !pb-3',
        content: '!p-6',
        footer: '!rounded-b-2xl !border-t !border-gray-100'
      }"
    >
      <template #header>
        <div class="flex items-center gap-3">
          <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-red-400 to-red-600 flex items-center justify-center shadow-md">
            <i class="pi pi-exclamation-triangle text-white text-sm"></i>
          </div>
          <div>
            <h3 class="font-semibold text-gray-800 text-base m-0">Xác nhận xóa</h3>
            <p class="text-xs text-gray-400 m-0 mt-0.5">Hành động này không thể hoàn tác</p>
          </div>
        </div>
      </template>

      <p class="text-sm text-gray-600 leading-relaxed">
        Bạn có chắc chắn muốn xóa tài liệu
        <span class="font-semibold text-gray-800">{{ docToDelete?.fileName }}</span>?
        <br/>Dữ liệu vector sẽ bị xóa khỏi knowledge base.
      </p>

      <template #footer>
        <div class="flex items-center justify-end gap-2">
          <Button
            label="Giữ lại"
            text
            severity="secondary"
            @click="showDeleteDialog = false"
            class="!px-4 !py-2 !rounded-xl !text-sm"
          />
          <Button
            label="Xóa"
            icon="pi pi-trash"
            @click="deleteDocument"
            :loading="deleting"
            class="!px-4 !py-2 !rounded-xl !text-sm !bg-gradient-to-r !from-red-500 !to-red-600 !border-none hover:!from-red-600 hover:!to-red-700 !shadow-lg !shadow-red-500/20"
          />
        </div>
      </template>
    </Dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';

interface Document {
  id: string;
  fileName: string;
  filePath: string;
  uploadedAt: string;
  isVectorized: boolean;
}

const documents = ref<Document[]>([]);
const showUploadDialog = ref(false);
const uploading = ref(false);
const selectedFile = ref<File | null>(null);
const searchQuery = ref('');
const viewMode = ref<'grid' | 'table'>('grid');
const isDragging = ref(false);
const uploadProgress = ref(0);
const showDeleteDialog = ref(false);
const docToDelete = ref<Document | null>(null);
const deleting = ref(false);

const vectorizedCount = computed(() => documents.value.filter(d => d.isVectorized).length);
const pendingCount = computed(() => documents.value.filter(d => !d.isVectorized).length);

const filteredDocuments = computed(() => {
  if (!searchQuery.value.trim()) return documents.value;
  const q = searchQuery.value.toLowerCase().trim();
  return documents.value.filter(d => d.fileName.toLowerCase().includes(q));
});

function onDrop(e: DragEvent) {
  isDragging.value = false;
  if (e.dataTransfer?.files && e.dataTransfer.files.length > 0) {
    selectedFile.value = e.dataTransfer.files[0];
  }
}

function handleFileChange(e: Event) {
  const target = e.target as HTMLInputElement;
  if (target.files && target.files.length > 0) {
    selectedFile.value = target.files[0];
  }
}

function getFileIcon(fileName: string): string {
  const ext = fileName.split('.').pop()?.toLowerCase();
  switch (ext) {
    case 'pdf': return 'pi pi-file-pdf';
    case 'doc':
    case 'docx': return 'pi pi-file-word';
    case 'xls':
    case 'xlsx': return 'pi pi-file-excel';
    case 'ppt':
    case 'pptx': return 'pi pi-file-ppt';
    case 'md': return 'pi pi-code';
    case 'txt': return 'pi pi-file';
    default: return 'pi pi-file';
  }
}

function getFileIconClass(fileName: string): string {
  const ext = fileName.split('.').pop()?.toLowerCase();
  switch (ext) {
    case 'pdf': return 'bg-gradient-to-br from-red-50 to-red-100 text-red-500';
    case 'doc':
    case 'docx': return 'bg-gradient-to-br from-blue-50 to-blue-100 text-blue-500';
    case 'xls':
    case 'xlsx': return 'bg-gradient-to-br from-emerald-50 to-emerald-100 text-emerald-500';
    case 'ppt':
    case 'pptx': return 'bg-gradient-to-br from-orange-50 to-orange-100 text-orange-500';
    case 'md': return 'bg-gradient-to-br from-purple-50 to-purple-100 text-purple-500';
    case 'txt': return 'bg-gradient-to-br from-gray-50 to-gray-100 text-gray-500';
    default: return 'bg-gradient-to-br from-gray-50 to-gray-100 text-gray-500';
  }
}

function getFileExt(fileName: string): string {
  return (fileName.split('.').pop() || '').toUpperCase();
}

function formatDate(dateStr: string): string {
  const d = new Date(dateStr);
  return d.toLocaleDateString('vi-VN', { year: 'numeric', month: '2-digit', day: '2-digit' });
}

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return bytes + ' B';
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
  return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
}

function confirmDelete(doc: Document) {
  docToDelete.value = doc;
  showDeleteDialog.value = true;
}

const deleteDocument = async () => {
  if (!docToDelete.value) return;
  deleting.value = true;
  try {
    const response = await http.delete(`${ApiFactory.DOCUMENT.BASE}/${docToDelete.value.id}`);
    if (response.ok) {
      documents.value = documents.value.filter(d => d.id !== docToDelete.value!.id);
      showDeleteDialog.value = false;
      docToDelete.value = null;
    }
  } catch (error) {
    console.error('Delete failed:', error);
  } finally {
    deleting.value = false;
  }
};

const loadDocuments = async () => {
  try {
    const response = await http.get(ApiFactory.DOCUMENT.BASE);
    if (response.ok) {
      documents.value = await response.json();
    }
  } catch (error) {
    console.error('Failed to load documents:', error);
  }
};

const uploadFile = async () => {
  if (!selectedFile.value) return;
  
  uploading.value = true;
  uploadProgress.value = 0;
  
  // Simulate upload progress
  const progressInterval = setInterval(() => {
    if (uploadProgress.value < 90) {
      uploadProgress.value += Math.random() * 15;
    }
  }, 300);

  const formData = new FormData();
  formData.append('file', selectedFile.value);
  
  const userJson = localStorage.getItem('hermes_user');
  const userId = userJson ? JSON.parse(userJson).id : '00000000-0000-0000-0000-000000000000';
  formData.append('userId', userId);

  try {
    const response = await http.post(ApiFactory.DOCUMENT.UPLOAD, formData);

    clearInterval(progressInterval);
    uploadProgress.value = 100;
    
    if (response.ok) {
      setTimeout(() => {
        selectedFile.value = null;
        showUploadDialog.value = false;
        uploadProgress.value = 0;
        loadDocuments();
      }, 500);
    } else {
      console.error('Upload failed');
      uploadProgress.value = 0;
    }
  } catch (error) {
    clearInterval(progressInterval);
    uploadProgress.value = 0;
    console.error('Upload error:', error);
  } finally {
    setTimeout(() => { uploading.value = false; }, 300);
  }
};

onMounted(() => {
  loadDocuments();
});
</script>

<style scoped>
/* Fade-in-up animation for grid cards */
@keyframes fadeInUp {
  from {
    opacity: 0;
    transform: translateY(16px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}

.animate-fade-in-up {
  animation: fadeInUp 0.4s ease-out both;
}

/* Custom scrollbar for main content area */
.flex-1.overflow-y-auto::-webkit-scrollbar {
  width: 6px;
}
.flex-1.overflow-y-auto::-webkit-scrollbar-track {
  background: transparent;
}
.flex-1.overflow-y-auto::-webkit-scrollbar-thumb {
  background: rgba(0, 0, 0, 0.08);
  border-radius: 3px;
}
.flex-1.overflow-y-auto::-webkit-scrollbar-thumb:hover {
  background: rgba(0, 0, 0, 0.15);
}

/* Line clamp utility */
.line-clamp-2 {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

/* PrimeVue table customization overrides */
:deep(.p-datatable .p-datatable-thead > tr > th) {
  background: transparent;
  color: #6b7280;
  font-weight: 600;
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  padding: 0.75rem 1rem;
  border-bottom: 1px solid #f3f4f6;
}

:deep(.p-datatable .p-datatable-tbody > tr) {
  transition: background-color 0.2s;
}

:deep(.p-datatable .p-datatable-tbody > tr > td) {
  padding: 0.75rem 1rem;
  border-bottom: 1px solid #f3f4f6;
}

:deep(.p-datatable .p-datatable-tbody > tr:last-child > td) {
  border-bottom: none;
}

:deep(.p-paginator) {
  padding: 0.75rem 1rem;
  gap: 0.25rem;
}

:deep(.p-paginator .p-paginator-page) {
  min-width: 2rem;
  height: 2rem;
  border-radius: 0.5rem;
  font-size: 0.8125rem;
}

:deep(.p-paginator .p-paginator-page.p-highlight) {
  background: #eff6ff;
  color: #3b82f6;
  font-weight: 600;
}

:deep(.p-paginator .p-paginator-prev),
:deep(.p-paginator .p-paginator-next) {
  min-width: 2rem;
  height: 2rem;
  border-radius: 0.5rem;
  color: #9ca3af;
}

/* Dialog customizations */
:deep(.p-dialog-header) {
  padding: 1.25rem 1.5rem;
}

:deep(.p-dialog-content) {
  padding: 0;
}

:deep(.p-dialog-footer) {
  padding: 0.75rem 1.5rem;
}
</style>
