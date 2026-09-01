# Accessibility review — LMKit Agent web client

**Ngày đánh giá:** 2026-08-20  
**Chuẩn tham chiếu:** WCAG 2.1 AA  
**Phạm vi:** login, app shell/navigation, chat, widget, document, memory, user management và voice controls

## Kết luận

Đợt remediation đã đóng các lỗi accessibility có thể xác minh ở source/build/browser: thiếu landmark, control không semantic, form thiếu label, icon button thiếu accessible name, focus indicator không nhất quán, target nhỏ hơn 44×44 px, animation không tôn trọng reduced-motion, trạng thái động không được thông báo và theme có contrast không nhất quán. Source guardrails và axe WCAG browser scan đều chạy trong CI.

Đây **không phải chứng nhận WCAG**. Axe đã đo DOM/computed styles trên Chromium production artifact, nhưng kiểm tra screen reader, keyboard exploratory, zoom/reflow 200–400% và các trạng thái dữ liệu chưa có fixture vẫn là release gate thủ công.

## Checklist phát hiện và remediation

| Mức độ | Khu vực | Phát hiện | WCAG 2.1 | Khắc phục | Trạng thái |
|---|---|---|---|---|---|
| Critical | Login/form | Email, password và một số MCP field không có liên kết label-control rõ ràng | 1.3.1, 3.3.2, 4.1.2 | Bổ sung `for`/`id`, `inputId`, input type và autocomplete token | Đã đóng |
| Critical | Chat/session | Session và nguồn web dùng `div` click, không có semantics/keyboard mặc định | 2.1.1, 4.1.2 | Chuyển thành native `button`, giữ focus và accessible name | Đã đóng |
| Major | Toàn client | Focus keyboard phụ thuộc component/browser, một số input loại bỏ focus ring | 2.4.7 | Thêm global `:focus-visible` 3 px và bỏ suppression tại composer | Đã đóng |
| Major | Navigation/actions | Nhiều icon action và CTA nhỏ hơn target 44×44 px | 2.5.5 | Chuẩn hóa `w-11 h-11`/`min-h-11` cho navigation, copy, delete, send, HITL và dialog actions | Đã đóng |
| Major | Dynamic content | Chat stream, login/MCP/voice/HITL error không công bố ổn định cho assistive technology | 4.1.3 | Thêm `role="log"`, `aria-live`, `role="alert"`, progress semantics và trạng thái mic `aria-pressed` | Đã đóng |
| Major | Motion | Transition/animation tiếp tục chạy khi hệ điều hành yêu cầu giảm chuyển động | 2.3.3 | Thêm `prefers-reduced-motion: reduce` để vô hiệu animation, transition và smooth scrolling | Đã đóng |
| Major | Icon controls | Một số nút chỉ có icon dựa vào tooltip/title | 1.1.1, 4.1.2 | Bổ sung `aria-label` theo ngữ cảnh; logout và remove actions có tên cụ thể | Đã đóng |
| Moderate | Mobile navigation | Menu không công bố expanded state và quan hệ với vùng điều hướng | 1.3.1, 4.1.2 | Bổ sung `aria-expanded`, `aria-controls`, `nav` label và target 44 px | Đã đóng |
| Moderate | Status/progress | Upload progress chỉ thể hiện trực quan | 1.3.1, 4.1.2 | Thêm `role="progressbar"` cùng `aria-valuemin/max/now` | Đã đóng |
| Moderate | Contrast | Error/action text đỏ nhạt có nguy cơ không đạt AA trên nền sáng | 1.4.3 | Đổi trạng thái quan trọng sang red-700/red-800 và xác minh computed styles bằng axe | Đã đóng trên fixtures browser |
| Moderate | Regression | CI chưa có accessibility guardrail | 4.1.2 (hỗ trợ) | Thêm 5 source tests: non-semantic click, placeholder link, focus suppression, native/PrimeVue icon-only accessible name | Đã đóng |
| Critical | Theme/contrast | Token nền tối được dùng cùng typography nền sáng; PrimeVue CTA và badge có contrast dưới AA | 1.4.3 | Đồng bộ light theme tokens, làm tối brand/status/muted text và quét lại computed styles bằng axe | Đã đóng trên fixtures browser |
| Critical | Password input | PrimeVue Password gắn `aria-expanded` không hợp lệ lên native password input | 4.1.2 | Dùng password `InputText` semantic; axe `aria-allowed-attr` sạch | Đã đóng |

## Bằng chứng tự động

- `npm run test:unit`: **16/16 pass**, trong đó 5 kiểm tra accessibility source và 4 kiểm tra API error contract.
- `npm run test:e2e`: **4/4 pass** trên Chromium production artifact; axe không báo WCAG 2.1 A/AA violation trong login, chat/SSE, documents, memory, user admin, MCP dialog và mobile navigation fixtures.
- Guardrails duyệt toàn bộ `.vue` dưới `src`; Playwright/axe chạy sau production build trong CI.

## Kiểm tra thủ công còn bắt buộc

1. Chạy toàn bộ luồng login → chat/HITL → documents → memory → MCP chỉ bằng keyboard; xác nhận thứ tự focus, focus trap và focus return của PrimeVue Dialog/Drawer.
2. Chạy NVDA + Chrome hoặc VoiceOver + Safari; xác nhận tên/role/value, live announcements và không lặp nội dung khi SSE stream.
3. Bổ sung fixture axe cho placeholder, disabled, error, upload/HITL và voice states chưa xuất hiện trong bốn browser flows hiện tại.
4. Kiểm tra zoom 200%, reflow 320 CSS px, orientation mobile và text spacing override.
5. Kiểm tra reduced-motion trong browser thật và các trạng thái loading/disabled/error.

## Tiêu chí đóng audit

- Không có lỗi Critical/Major từ axe/browser scan.
- Tất cả flow chính hoàn thành bằng keyboard, không keyboard trap.
- Screen reader công bố đúng chat updates, form errors, upload progress và mic state.
- Mọi tổ hợp màu thực tế đạt WCAG 2.1 AA; mọi ngoại lệ được ghi nhận và sửa trước release.
