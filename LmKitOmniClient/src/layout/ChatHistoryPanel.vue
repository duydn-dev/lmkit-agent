<template>
  <div class="mt-4 border-t border-gray-100 pt-1">
    <div class="text-[10px] text-gray-400 font-semibold mb-1.5 mt-2 px-2 uppercase tracking-widest flex items-center justify-between">
      <span>Lịch sử chat</span>
      <button @click="emit('new')" class="hover:text-gray-600 transition-colors rounded-md hover:bg-gray-100 w-8 h-8 flex items-center justify-center" aria-label="Tạo phiên chat mới">
        <i class="pi pi-plus text-xs"></i>
      </button>
    </div>
    <div class="relative mb-2">
      <i class="pi pi-search absolute left-2.5 top-1/2 -translate-y-1/2 text-gray-400 text-xs" aria-hidden="true"></i>
      <input
        v-model="query"
        type="search"
        class="w-full h-9 rounded-lg border border-gray-200 bg-gray-50 pl-8 pr-3 text-xs text-gray-900 placeholder:text-gray-400 focus:border-blue-600 focus:bg-white transition-colors"
        placeholder="Tìm kiếm..."
        aria-label="Tìm kiếm đoạn chat" />
    </div>
    <!-- Vùng cuộn RIÊNG của danh sách: cuộn tới đáy sẽ tải thêm trang kế tiếp
         (infinite scroll) thay vì render toàn bộ lịch sử cùng lúc. -->
    <div ref="listRoot" class="max-h-72 overflow-y-auto pr-1" @scroll="onScroll">
      <div v-if="loading" class="px-3 py-2 text-xs text-gray-400 italic" role="status">Đang tìm kiếm...</div>
      <template v-else>
        <div v-if="sessions.length === 0" class="px-3 py-2 text-xs text-gray-400 italic text-center">
          {{ searching ? 'Không tìm thấy.' : 'Chưa có đoạn chat.' }}
        </div>
        <div v-for="session in sessions" :key="session.id" class="w-full flex items-center gap-2 px-3 py-1.5 text-gray-600 hover:text-gray-900 hover:bg-gray-50 rounded-lg transition-colors text-xs truncate group mx-1 my-0.5">
          <template v-if="editingSessionId === session.id">
            <input
              :ref="focusRenameInput"
              v-model="editingTitle"
              type="text"
              class="flex-1 min-w-0 h-8 rounded-md border border-blue-600 bg-white px-2 text-xs text-gray-900"
              :aria-label="`Tên mới cho đoạn chat ${session.title || 'mới'}`"
              @keydown.enter.prevent="emit('save-rename', session.id)"
              @keydown.esc.prevent="emit('cancel-rename')"
              @blur="emit('cancel-rename')" />
          </template>
          <template v-else>
            <button type="button" class="flex items-center gap-2 truncate flex-1 cursor-pointer text-left min-h-8" @click="emit('select', session.id)">
              <i class="pi pi-message text-gray-400 text-[11px]"></i>
              <span class="truncate text-left flex-1">{{ session.title || 'Đoạn chat mới' }}</span>
            </button>
            <!-- Trên mobile không có hover: hiện luôn nút hành động (mờ) thay vì ẩn hẳn. -->
            <button @click.stop="emit('start-rename', session)" class="text-gray-400 hover:text-gray-700 opacity-60 md:opacity-0 md:group-hover:opacity-100 md:group-focus-within:opacity-100 w-7 h-7 rounded hover:bg-gray-200/60 transition-all flex-shrink-0 cursor-pointer flex items-center justify-center" :aria-label="`Đổi tên đoạn chat ${session.title || 'mới'}`">
              <i class="pi pi-pencil text-[10px]"></i>
            </button>
            <button @click.stop="emit('delete', session.id)" class="text-gray-400 hover:text-red-500 opacity-60 md:opacity-0 md:group-hover:opacity-100 md:group-focus-within:opacity-100 w-7 h-7 rounded hover:bg-gray-200/60 transition-all flex-shrink-0 cursor-pointer flex items-center justify-center" :aria-label="`Xóa đoạn chat ${session.title || 'mới'}`">
              <i class="pi pi-trash text-[10px]"></i>
            </button>
          </template>
        </div>
        <!-- Placeholder đang tải trang kế (infinite scroll). -->
        <div v-if="loadingMore" class="px-3 py-2 text-xs text-gray-400 italic text-center" role="status">
          <i class="pi pi-spin pi-spinner text-[10px] mr-1" aria-hidden="true"></i>Đang tải thêm...
        </div>
        <!-- Hết lịch sử: không còn trang nào phía sau. -->
        <div v-else-if="!hasMore && sessions.length > 0" class="px-3 py-2 text-[10px] text-gray-300 text-center select-none">— Đã hết —</div>
      </template>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue';

/**
 * Khối "Lịch sử chat" dùng chung cho sidebar desktop và mobile drawer.
 * State (sessions, search, rename, phân trang) nằm ở AppLayout — component này
 * chỉ trình bày, phát sự kiện và báo khi cuộn chạm đáy (@scroll → loadMore).
 */
interface ChatSession {
  id: string;
  title: string;
  createdAt: string;
}

const props = defineProps<{
  sessions: ChatSession[];
  loading: boolean;
  /** Đang tải trang kế tiếp (infinite scroll). */
  loadingMore: boolean;
  /** Còn trang nào phía sau không. */
  hasMore: boolean;
  searching: boolean;
  editingSessionId: string | null;
}>();

const query = defineModel<string>('query', { required: true });
const editingTitle = defineModel<string>('editingTitle', { default: '' });

const emit = defineEmits<{
  (e: 'new'): void;
  (e: 'select', id: string): void;
  (e: 'start-rename', session: ChatSession): void;
  (e: 'save-rename', id: string): void;
  (e: 'cancel-rename'): void;
  (e: 'delete', id: string): void;
  (e: 'load-more'): void;
}>();

const listRoot = ref<HTMLElement | null>(null);

/** Chạm đáy danh sách → xin AppLayout tải trang kế (nếu còn và không đang tải). */
const onScroll = () => {
  const el = listRoot.value;
  if (!el) return;
  if (el.scrollTop + el.clientHeight >= el.scrollHeight - 24) {
    if (props.hasMore && !props.loadingMore && !props.loading) emit('load-more');
  }
};

/** Template function-ref: focuses the rename input as soon as it mounts. */
const focusRenameInput = (el: unknown) => {
  if (el instanceof HTMLInputElement && document.activeElement !== el) el.focus();
};
</script>
