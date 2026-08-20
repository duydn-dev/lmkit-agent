<template>
  <main class="h-full overflow-y-auto bg-gray-50 p-6 md:p-10 text-gray-800">
    <div class="max-w-5xl mx-auto">
      <div class="flex items-start justify-between gap-4 mb-8">
        <div>
          <h1 class="text-2xl font-semibold">Bộ nhớ của trợ lý</h1>
          <p class="text-sm text-gray-500 mt-1">Xem và xóa các thông tin trợ lý đã ghi nhớ riêng cho tài khoản của bạn.</p>
        </div>
        <Button label="Làm mới" icon="pi pi-refresh" severity="secondary" :loading="loading" @click="loadMemories" />
      </div>

      <div v-if="error" class="p-4 mb-5 rounded-xl bg-red-50 border border-red-200 text-red-700 text-sm">{{ error }}</div>
      <div v-if="!loading && memories.length === 0" class="p-12 text-center bg-white border border-gray-100 rounded-2xl">
        <i class="pi pi-history text-4xl text-gray-300"></i>
        <p class="mt-3 text-gray-500">Trợ lý chưa lưu thông tin nào.</p>
      </div>

      <div class="grid gap-4">
        <article v-for="memory in memories" :key="memory.id" class="bg-white border border-gray-100 rounded-2xl p-5 shadow-sm">
          <div class="flex items-start justify-between gap-4">
            <div class="min-w-0">
              <div class="flex items-center gap-2 mb-2">
                <span class="text-xs font-semibold px-2 py-1 rounded-full bg-blue-50 text-blue-700">{{ memory.memoryType }}</span>
                <span class="text-xs text-gray-400">Độ tin cậy {{ Math.round(memory.confidence * 100) }}%</span>
                <span class="text-xs font-medium px-2 py-1 rounded-full" :class="memory.isConfirmed ? 'bg-green-50 text-green-700' : 'bg-amber-50 text-amber-700'">
                  {{ memory.isConfirmed ? 'Đã xác nhận' : 'Chờ xác nhận' }}
                </span>
              </div>
              <h2 class="font-semibold break-words">{{ memory.memoryKey }}</h2>
              <p class="mt-2 text-sm text-gray-600 whitespace-pre-wrap break-words">{{ memory.memoryValue }}</p>
              <p class="mt-3 text-xs text-gray-400">Cập nhật {{ formatDate(memory.updatedAtUtc) }}</p>
            </div>
            <div class="flex items-center gap-1">
              <Button v-if="!memory.isConfirmed" icon="pi pi-check" severity="success" text rounded aria-label="Xác nhận thông tin này" class="!w-11 !h-11" @click="confirmMemory(memory)" />
              <Button icon="pi pi-trash" severity="danger" text rounded aria-label="Quên thông tin này" class="!w-11 !h-11" @click="forget(memory)" />
            </div>
          </div>
        </article>
      </div>
    </div>
  </main>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { http } from '@/api/http';

interface MemoryItem {
  id: string;
  memoryType: string;
  memoryKey: string;
  memoryValue: string;
  confidence: number;
  isConfirmed: boolean;
  updatedAtUtc: string;
}

const memories = ref<MemoryItem[]>([]);
const loading = ref(false);
const error = ref('');

async function loadMemories() {
  loading.value = true;
  error.value = '';
  try {
    const response = await http.get('/api/memory');
    if (!response.ok) throw new Error('Không thể tải bộ nhớ.');
    memories.value = await response.json();
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Không thể tải bộ nhớ.';
  } finally {
    loading.value = false;
  }
}

async function forget(memory: MemoryItem) {
  if (!confirm(`Quên thông tin “${memory.memoryKey}”?`)) return;
  const response = await http.delete(`/api/memory/${memory.id}`);
  if (response.ok) memories.value = memories.value.filter(item => item.id !== memory.id);
  else error.value = 'Không thể xóa thông tin đã chọn.';
}

async function confirmMemory(memory: MemoryItem) {
  const response = await http.post(`/api/memory/${memory.id}/confirm`);
  if (response.ok) {
    memory.isConfirmed = true;
    memory.confidence = Math.max(memory.confidence, 0.95);
  } else {
    error.value = 'Không thể xác nhận thông tin đã chọn.';
  }
}

function formatDate(value: string) {
  return new Date(value).toLocaleString('vi-VN');
}

onMounted(loadMemories);
</script>
