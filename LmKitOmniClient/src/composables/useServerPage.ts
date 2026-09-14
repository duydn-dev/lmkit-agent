import { computed, ref } from 'vue';
import { http } from '@/api/http';
import { errorMessage, readApiError } from '@/api/errors';
import { pagedUrl, type PagedResult } from '@/api/paging';

interface UseServerPageOptions {
  /** Số dòng mỗi trang (mặc định 20 — khớp Paging.DefaultPageSize backend). */
  pageSize?: number;
  /** Cụm danh từ cho thông báo lỗi, vd "danh sách người dùng". */
  errorLabel?: string;
}

/**
 * State chuẩn cho một màn getlist phân trang server-side (PrimeVue DataTable
 * lazy): rows/totalRecords/loading/error + tìm kiếm debounce 350ms (về trang 1)
 * và onPage cho paginator. Mọi màn quản lý dùng chung composable này để bảng
 * nào cũng cư xử giống nhau.
 */
export function useServerPage<T>(baseUrl: string, options?: UseServerPageOptions) {
  const rows = ref<T[]>([]) ;
  const totalRecords = ref(0);
  const loading = ref(false);
  const error = ref('');
  const page = ref(1);
  const pageSize = ref(options?.pageSize ?? 20);
  const search = ref('');
  const label = options?.errorLabel ?? 'danh sách';

  /** Offset 0-based cho prop :first của DataTable/Paginator. */
  const first = computed(() => (page.value - 1) * pageSize.value);

  let searchTimer: ReturnType<typeof setTimeout> | undefined;
  let requestSequence = 0;

  const load = async () => {
    const sequence = ++requestSequence;
    loading.value = true;
    error.value = '';
    try {
      const response = await http.get(pagedUrl(baseUrl, page.value, pageSize.value, search.value));
      if (sequence !== requestSequence) return; // một request mới hơn đã bắn đi
      if (!response.ok) {
        error.value = await readApiError(response, `Không thể tải ${label}`);
        return;
      }
      const result = (await response.json()) as PagedResult<T>;
      rows.value = result.items;
      totalRecords.value = result.totalCount;
      // Trang hiện tại vượt quá tổng sau khi xóa bớt → lùi về trang cuối còn dữ liệu.
      if (result.items.length === 0 && result.totalCount > 0 && page.value > 1) {
        page.value = Math.max(1, Math.ceil(result.totalCount / pageSize.value));
        await load();
      }
    } catch (cause) {
      if (sequence !== requestSequence) return;
      error.value = errorMessage(cause, `Không thể tải ${label}.`);
    } finally {
      if (sequence === requestSequence) loading.value = false;
    }
  };

  /** Handler cho @page của DataTable lazy / Paginator. */
  const onPage = (event: { first: number; rows: number }) => {
    pageSize.value = event.rows;
    page.value = Math.floor(event.first / event.rows) + 1;
    void load();
  };

  /** Gõ tìm kiếm: debounce 350ms, luôn quay về trang 1. */
  const onSearchInput = () => {
    if (searchTimer) clearTimeout(searchTimer);
    searchTimer = setTimeout(() => {
      page.value = 1;
      void load();
    }, 350);
  };

  /** Tải lại giữ nguyên trang hiện tại (sau create/update/delete). */
  const reload = () => load();

  return { rows, totalRecords, loading, error, page, pageSize, search, first, load, reload, onPage, onSearchInput };
}
