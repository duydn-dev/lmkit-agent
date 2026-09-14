# Kế hoạch test chat — Regression 14/09/2026

> **Phạm vi:** regression các tính năng **vừa thay đổi** sau kế hoạch acceptance gốc (`chat-acceptance-test-plan.md`) + smoke test nhân chat. Case gốc (C01–C06, T01–T07…) vẫn giữ hiệu lực, chỉ chạy lại nhóm chọn lọc ở Phần 4.
>
> **Nguyên tắc giữ nguyên:** PASS chỉ khi có kết quả đúng + bằng chứng runtime (SSE marker / DOM / log / DB). Không đoán PASS từ câu trả lời tự nhận của model.

## 1. Điều kiện chạy (pre-flight)

| # | Kiểm tra | Cách kiểm | Tiêu chí |
|---|---|---|---|
| P1 | API đang chạy | `GET http://localhost:5032/health` | 200 |
| P2 | Model chat mặc định | appsettings `DefaultChat` = `gemma4:e4b`; request đầu tiên có `[THINKING]: ⏳ Đang nạp mô hình...` nếu lạnh | model ready ≤ 60s |
| P3 | SearXNG | `GET http://localhost:8888/search?q=test&format=json` | 200, JSON có `results` |
| P4 | FE | preview `http://127.0.0.1:5198` (vite proxy `/api` → 5032) | trang login render |
| P5 | Đăng nhập | bootstrap user từ `.env` (không ghi mật khẩu vào báo cáo) | vào được `/chat` |

Lưu ý môi trường: API phải chạy từ thư mục `LmKitOmniApi/` (content root) với `JwtSettings__SecretKey`, nếu không appsettings không nạp → login 400/500.

## 2. Case test — tính năng MỚI/ĐỔI sau acceptance gốc

### 2.1. Web search qua SearXNG (trước đây là DuckDuckGo)

| ID | Bước | Expected / bằng chứng |
|---|---|---|
| W01 | Tạo **session mới**, hỏi: `Hôm nay giá vàng miếng SJC bao nhiêu?` (bật web search) | SSE có event `web-search` với ≥ 3 URL (baomoi/24h/webgia/vietnamnet…); **log API** có đúng 1 dòng `Web search provider searx served the query with N hits.` và **0** dòng fallback DuckDuckGo |
| W02 | Cùng turn W01 — chất lượng đáp án | Đáp án chứa **con số thật** từ snippet (vd: "143,6 – 146,6 triệu đồng/lượng") + nêu nguồn; KHÔNG được trả "tôi không truy cập được dữ liệu thời gian thực" |
| W03 | UI citations | Chip "Read N web pages" hiển thị favicon thật của các nguồn (Google s2), drawer "Nguồn tham khảo" liệt kê cùng URLs; ảnh lỗi thì ẩn, không vỡ layout |
| W04 | Hỏi tiếp trong **cùng session**: `Còn hôm qua thì sao?` | Dữ liệu search mới được truyền vào lượt synthesis (không tái dùng cache cũ sai ngày); trả lời theo ngày tương đối hợp lý |

### 2.2. Neo ngày hiện tại trong system prompt

| ID | Bước | Expected |
|---|---|---|
| D01 | Session mới, hỏi: `Hôm nay ngày mấy, thứ mấy?` | Trả đúng ngày/thứ hiện tại (13/09/2026, chủ nhật) — khớp đồng hồ server |
| D02 | `Tin tức mới nhất về [chủ đề] ngày 13/9/2026 là gì?` | KHÔNG từ chối kiểu "đó là tương lai"; nếu có search thì dẫn được bài viết dated đúng ngày |

### 2.3. Reasoning / thinking pipeline

| ID | Bước | Expected |
|---|---|---|
| R01 | Hỏi cần tool (vd W01) và **quan sát lúc đang stream** | Panel "Quá trình suy luận" hiện **mở sẵn**, label "Đang suy luận" + spinner, chữ CoT chảy dần; mốc pipeline (🛡️ → 🧠 → 📋 → ✅) hiện lần lượt |
| R02 | Sau khi turn xong | Mốc pipeline **biến mất**, panel chỉ còn chuỗi suy luận của model (có scroll, max-height); nếu turn không sinh reasoning → panel không render |
| R03 | Click header panel | Collapse/mở được; turn đang chạy **không cho** collapse; mỗi message nhớ trạng thái riêng khi reload |
| R04 | Session cũ chứa nhiều lượt | Panel lịch sử load lại dạng disclosure, nội dung reasoning không trùng đáp án |
| R05 | Turn问答 trực tiếp không cần tool: `1+1 bằng mấy?` | Có mốc "✅ Đã có câu trả lời trực tiếp"; trả lời `2`; không treo heartbeat thừa |

### 2.4. Danh tính & ngôn ngữ milestone

| ID | Bước | Expected |
|---|---|---|
| I01 | `Bạn là ai?` (session mới) | Tự giới thiệu **CILA Agent** — "Trung tâm thông tin lưu trữ và thư viện tài nguyên môi trường quốc gia"; KHÔNG xưng Hermes |
| I02 | Xem chuỗi `[THINKING]` | KHÔNG còn thuật ngữ "LM-Kit ReAct"/"inference"; chỉ tiếng Việt chung chung ("Hoàn tất suy luận sau N bước xử lý") |
| I03 | `Trung tâm bạn thuộc cơ quan nào?` | Trả lời khớp mô tả vai trò trong prompt, không bịa tên cơ quan khác |

### 2.5. UI app shell (đổi gần đây)

| ID | Bước | Expected |
|---|---|---|
| U01 | Đăng nhập xong nhìn sidebar + header | Brand bar và top header **cùng chiều cao 56px**, cùng nền navy `#1e3a8a`; không còn viền vàng dưới logo |
| U02 | Logo sidebar | **Quốc huy** (không phải ngôi sao), nền trắng tròn, không vỡ ảnh |
| U03 | User block cuối sidebar | PrimeVue Avatar: bootstrap user "Admin User" → chữ **AU**, nền blue-50; name `slate-800`, role `slate-500`, **căn trái** |
| U04 | Tên cơ quan trên header | "Trung tâm Thông tin lưu trữ và Thư viện tài nguyên môi trường quốc gia" hiện **căn trái** top header, chữ sky-100, truncate có `title` đầy đủ |
| U05 | Click menu "AI Chat" rồi sang trang khác (vd RAG Documents) | Active item: nền blue-50, chữ + border trái navy; chuyển trang vẫn đúng item active |
| U06 | Đồng bộ màu | Toàn màn hình chat không còn tông sky/cyan cũ: nút gửi (khi có chữ), vòng sparkles, chip attachments, links — cùng họ blue/navy |
| U07 | Mobile 439px | Header không wrap; nút hamburger + "+" hiển thị; drawer menu mở được |

## 3. Case biên / resilience

| ID | Bước | Expected |
|---|---|---|
| E01 | Gửi 2 chat song song (2 tab) | Turn sau có `[THINKING]: ⏳ Máy đang bận — đang xếp hàng...`; không 500, không mất đáp án |
| E02 | Đính kèm file > 20MB (mock/fake) | `[THINKING]: ⚠️ Bỏ qua file quá 20 MB` rồi turn vẫn chạy tiếp |
| E03 | Đính kèm loại file lạ `.xyz` | `[THINKING]: ⚠️ Loại file không được hỗ trợ` |
| E04 | Ngắt mạng SearXNG (stop container) rồi hỏi cần search | Fallback provider kế tiếp hoặc lỗi *trONG* đáp án ở dạng lịch sự (không crash SSE, vẫn `[DONE]`); bật lại container |
| E05 | Turn quá dài (>100k chars trả lời) | Stream không đứt, không vượt quá budget token của model |
| E06 | `regenerate:true` khi chưa có turn nào | Thông báo lỗi rõ ràng, không 500 |
| E07 | Gửi cả `regenerate` + `editLast` | HTTP 400 với message rõ |

## 4. Smoke nhân chat (chạy lại từ kế hoạch gốc)

Chạy nhanh lại nhóm case cốt lõi để chắc chắn các thay đổi pipeline không phá nền:

| ID | Từ kế hoạch gốc | Điểm kiểm chính |
|---|---|---|
| S01 | C01 | `2+2=4`, `[DONE]`, session lưu |
| S02 | C03.1 + C03.2 | Nhớ OMNI-2026/Lan qua history |
| S03 | C06.1 + C06.2 | Temporary chat không lưu DB |
| S04 | C04 | Regenerate thay thế đáp án |
| S05 | T01 + T02 | Tool tính toán + ngày giờ có evidence |
| S06 | C02 | Stream tăng dần, không lặp, đáp án ~500 từ |

## 5. Quy tắc thực thi

1. **Không đọc log giữa case.** Chỉ đọc log API **một lần** sau toàn bộ case, grep theo marker cần thiết (`searx served`, `ERR`, `FTL`).
2. Mỗi case timeout: thường 90s; case model lạnh (lần đầu) 120s. Quá thời gian → ghi `BLOCKED`, chuyển case kế.
3. SSE đọc bằng `curl -N` + script parse; DOM kiểm bằng `preview_evaluate`/snapshot; **không screenshot dư thừa** — chỉ chụp khi case UI (U01–U07, W03, R03).
4. Session test dùng riêng, đặt tiêu đề `REG-<ID>-...` để dọn dẹp sau khi xong (delete qua API).
5. Kết quả tổng hợp vào bảng: `ID | Kết quả (PASS/FAIL/BLOCKED) | Bằng chứng (1 dòng)`.

## 6. Thứ tự chạy đề xuất (≈ 20 phút)

1. Pre-flight P1–P5 (2').
2. Nhóm 2.3 reasoning (R01–R05) — ảnh hưởng FE nhiều nhất (5').
3. Nhóm 2.1 + 2.2 search & date (W01–W04, D01–D02) (6').
4. Nhóm 2.4 identity (I01–I03) (2').
5. Smoke S01–S06 (5').
6. Biên E01–E07 — chỉ chạy E01, E02, E06, E07; E03–E05 tùy thời gian (2').
7. UI shell U01–U07 — chụp ảnh nhanh từng cụm (3').
8. Đọc log 1 lần, dọn session REG-*, viết báo cáo.
