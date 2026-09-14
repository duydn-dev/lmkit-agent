/**
 * Shape getlist chuẩn mọi endpoint quản lý trả về sau đợt chuẩn hóa phân trang
 * (PagedResult<T> phía backend): { items, page, pageSize, totalCount, totalPages }.
 */
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

/** Gắn page/pageSize/search vào URL getlist (bỏ search trống). */
export function pagedUrl(base: string, page: number, pageSize: number, search?: string): string {
  const params = new URLSearchParams();
  params.set('page', String(page));
  params.set('pageSize', String(pageSize));
  const trimmed = search?.trim();
  if (trimmed) params.set('search', trimmed);
  return `${base}${base.includes('?') ? '&' : '?'}${params.toString()}`;
}
