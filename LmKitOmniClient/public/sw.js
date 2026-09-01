/*
 * Service worker tối giản cho LM-Kit Omni Agent (PWA v1).
 *
 * Chiến lược:
 * - /api/ và /hubs/ : KHÔNG BAO GIỜ can thiệp hay cache — dữ liệu, xác thực
 *   cookie và SSE/WebSocket phải luôn đi thẳng tới mạng.
 * - /assets/*       : cache-first (bundle Vite có hash trong tên file nên bất
 *   biến), fallback mạng khi chưa có trong cache.
 * - Còn lại (HTML, /fonts/, favicon.svg, manifest...): network-first để bản
 *   deploy mới lan tỏa ngay lập tức, fallback cache khi offline.
 *
 * Đổi CACHE_NAME (tăng phiên bản) mỗi khi thay đổi chiến lược cache; bước
 * activate sẽ dọn mọi cache phiên bản cũ.
 */

const CACHE_NAME = 'omni-static-v1';

self.addEventListener('install', () => {
  // Kích hoạt service worker mới ngay, không chờ tab cũ đóng.
  self.skipWaiting();
});

self.addEventListener('activate', (event) => {
  event.waitUntil((async () => {
    const keys = await caches.keys();
    await Promise.all(keys.filter((key) => key !== CACHE_NAME).map((key) => caches.delete(key)));
    await self.clients.claim();
  })());
});

/** Cache-first cho tài nguyên bất biến (bundle có hash). */
async function cacheFirst(request) {
  const cache = await caches.open(CACHE_NAME);
  const cached = await cache.match(request);
  if (cached) return cached;
  const response = await fetch(request);
  // Chỉ cache phản hồi 200 trọn vẹn (206/opaque/lỗi thì bỏ qua).
  if (response.status === 200) {
    await cache.put(request, response.clone()).catch(() => undefined);
  }
  return response;
}

/** Network-first cho HTML và tĩnh không hash: luôn ưu tiên bản mới nhất. */
async function networkFirst(request) {
  const cache = await caches.open(CACHE_NAME);
  try {
    const response = await fetch(request);
    if (response.status === 200) {
      await cache.put(request, response.clone()).catch(() => undefined);
    }
    return response;
  } catch (cause) {
    const cached = await cache.match(request);
    if (cached) return cached;
    throw cause;
  }
}

self.addEventListener('fetch', (event) => {
  const request = event.request;
  if (request.method !== 'GET') return;

  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;

  // API/hub: không respondWith — trình duyệt xử lý như không có service worker.
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/hubs/')) return;

  if (url.pathname.startsWith('/assets/')) {
    event.respondWith(cacheFirst(request));
    return;
  }

  event.respondWith(networkFirst(request));
});
