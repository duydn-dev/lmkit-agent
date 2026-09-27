# Tasklist xây dựng ứng dụng mobile LM-Kit Omni

Ứng dụng Flutter đã có nền tảng và nhiều màn hình chức năng. Tasklist này xác định phạm vi parity với web, thứ tự hoàn thiện và tiêu chí nghiệm thu; không xây lại các phần đã hoạt động.

## Nguyên tắc UI/UX

- Design system: Forui; state/data: Riverpod; API: Dio qua repository. Widget không gọi Dio trực tiếp.
- Nhận diện khối Government: xanh navy `#1E3A8A`, nền trắng/xám rất nhạt, đỏ quốc kỳ chỉ cho lỗi/xoá, vàng sao làm điểm nhấn nhỏ. Dùng Be Vietnam Pro.
- Mobile-first: bố cục cuộn, thao tác chạm tối thiểu 44dp, nhãn tiếng Việt rõ, trạng thái tải/rỗng/lỗi thành phần, không ép hàng dài ngang màn.
- Giữ các quy tắc an toàn backend (HITL, giới hạn, tenant/owner scope); không lưu token thường trong app preferences.

## Tasklist theo chức năng

| ID | Chức năng | Trạng thái / tiêu chí hoàn thành |
|---|---|---|
| M0 | Nền Flutter, cấu hình API, Forui/Riverpod, theme Government, menu và responsive shell | Đã có. Giữ đồng nhất màu sắc/kiểu nút và không tràn ở 320dp, gồm cỡ chữ hệ thống lớn. |
| M1 | Đăng nhập, lưu phiên an toàn, refresh/revoke/logout | Đã có. Xác nhận luồng bearer mobile, khoá bảo mật, thông báo hết phiên. |
| M2 | Chat SSE, phiên/lịch sử, đính kèm, voice, HITL, tệp và Canvas | Đã có. Giữ parser marker riêng, luồng mạng qua ApiClient, phê duyệt không bị bỏ qua. |
| M3 | Automation Agent: chạy mục tiêu, log bước, chi tiết run, huỷ và tải file | Đã có. Bổ sung deep-link từ lịch/thông báo tới chính xác run. |
| M4 | Task Scheduler: tạo/sửa/xoá/bật-tắt; interval/daily/weekly/once; completion/agent; persona; grant ghi tương lai; trạng thái/kết quả và mở run | Đã triển khai và kiểm tra: form Forui tạo/sửa đủ 4 chu kỳ; validation theo API (10–10080 phút, UTC/day-of-week, once 1 phút–366 ngày, webhook http/https); grant mặc định tắt và chỉ gửi cho agent; list có trạng thái, bật-tắt/xoá/sửa và mở Agent Run. Test repository/model/parser/UI cùng analyzer đều đạt. |
| M5 | HITL approvals, thông báo và deep link | Đã có. Duyệt/từ chối trong đúng tài khoản, lỗi hết hạn/xung đột dễ hiểu. |
| M6 | Workspace: Projects, RAG Documents, Memory, Custom Instructions | Đã có. |
| M7 | AI Studio: Agents, Research, Text/Vision tools, Content Studio | Đã có. |
| M8 | Quản trị: Dashboard, Users, Tenants, DB, Knowledge, MCP, LoRA, Widget, API Keys, Audit | Đã có; giới hạn truy cập Admin theo backend. |
| M9 | Hoàn thiện parity phụ trợ và vận hành mobile: upload, cache, accessibility, cấu hình release/store | Còn theo đợt tiếp theo; ưu tiên theo nhu cầu triển khai thực tế. |

## Đợt triển khai đã hoàn tất — M4 Scheduler

1. Đồng bộ model/repository Flutter với các trường và endpoint schedule của API.
2. Form Forui tạo/sửa lịch với đủ loại chu kỳ; kiểm tra đầu vào và 10 phút tối thiểu.
3. Cấu hình run mode, persona, grant `approveFutureRuns`; cảnh báo rõ quyền tự duyệt và mặc định an toàn.
4. Danh sách lịch có trạng thái, chạy kế tiếp/lần cuối, bật-tắt/xoá/sửa và link Agent Run gần nhất.
5. Đã xác minh: `flutter analyze --no-pub` và toàn bộ `flutter test --no-pub` đều đạt (212 test), gồm contract/API mock, form lịch, responsive 320/390dp và font scale 1.3×. Đã đồng bộ assertion đăng nhập với tên Bộ đa-tenant trên UI. Không chạy browser test.

## Tiêu chí nghiệm thu chung

- API contract đúng field camelCase; có trạng thái lỗi thân thiện, không hiển thị exception thô khi có thông điệp API.
- Forui controls trong modal/sheet; dùng `Wrap`/`Expanded` có kiểm soát để tránh overflow và giữ bố cục ổn định ở 320dp.
- Có unit/widget test cho logic hoặc luồng UI mới, kèm `flutter analyze`.
- Không chạy browser/E2E trên trình duyệt trừ khi được yêu cầu; chỉ chạy bộ test Flutter có liên quan.
