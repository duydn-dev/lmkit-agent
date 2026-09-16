# LmKitOmniMobile

Ứng dụng Flutter mobile của LM-Kit Omni, dùng chung backend `LmKitOmniApi` với
web client `LmKitOmniClient`. Mục tiêu là parity 1–1 về chức năng với bản desktop.

- **UI**: [Forui](https://forui.dev) là design system chính, Material chỉ là hạ tầng widget.
- **State**: Riverpod 3 (`Notifier` / `AsyncNotifier` / `FutureProvider.family`).
- **HTTP**: Dio, base URL lấy từ `AppConfig`, không hard-code ở bất kỳ đâu.

## Chạy app

```bash
flutter pub get
flutter run --dart-define-from-file=env/dev.json          # Android emulator
flutter run --dart-define-from-file=env/dev-ios.json      # iOS simulator / desktop
```

Cấu hình môi trường nằm trong `env/` — xem [`env/README.md`](env/README.md) để
biết cách đổi API URL, kể cả đổi ngay trên thiết bị mà không cần build lại.

### Máy ảo Android: bỏ thanh công cụ nổi của Gboard

Máy ảo khai với Android là **có bàn phím cứng** (`AT Translated Set 2 keyboard`),
nên Gboard không hiện bàn phím ảo thường mà hiện **thanh công cụ bàn phím cứng**:
một dải dọc nổi ở mép trái (micro, xoá, enter, emoji, ≡), và bấm micro trên dải đó
sẽ mở màn "Tap to speak" của Google. **Đây không phải giao diện của app** — cây
accessibility của app không có node nào ứng với nó, và đổi IME thì nó đổi theo.

Tắt nó trên máy ảo (đã áp dụng cho `Pixel_5_API_36`):

```bash
adb root
adb shell am force-stop com.google.android.inputmethod.latin
adb shell "sed -i 's/name=\"enable_physical_keyboard_widget\" value=\"true\"/name=\"enable_physical_keyboard_widget\" value=\"false\"/' \
  /data/data/com.google.android.inputmethod.latin/shared_prefs/flag_value.xml"
adb shell "chown u0_a154:u0_a154 /data/data/com.google.android.inputmethod.latin/shared_prefs/flag_value.xml"
adb reboot
```

Chủ sở hữu tệp phải trả lại như cũ, nếu không Gboard mất quyền đọc `shared_prefs`
và không khởi động được. Trên máy thật (không có bàn phím cứng) hiện tượng này
không xảy ra; nếu có bàn phím Bluetooth thì tắt trong Gboard → *Preferences*.

Muốn bàn phím ảo hiện khi chạm vào ô nhập (mặc định của máy ảo là ẩn vì tưởng có
bàn phím cứng):

```bash
adb shell settings put secure show_ime_with_hard_keyboard 1
```

## Cấu trúc

```text
lib/
  main.dart                     # load AppConfig trước runApp rồi override ProviderScope
  app/                          # MaterialApp, theme Forui ↔ Material
  core/
    config/                     # AppConfig + provider, override lưu trên thiết bị
    network/                    # ApiClient, AuthInterceptor, dio_factory, ApiException
    auth/                       # AuthRepository, session store, AuthController
    storage/                    # provider FlutterSecureStorage dùng chung
  features/
    auth/ home/ settings/       # đăng nhập, shell, cấu hình kết nối
    chat/                       # SSE, parser marker, composer, HITL, share,
                                # Canvas, thoại LiveKit, biểu đồ generative UI
    workspace/                  # Projects, Documents (RAG), Memory, Instructions
    studio/                     # Agents, Schedules, Runs, Research, Approvals,
                                # AI Tools (Text/Vision/OCR), Content Creation
    admin/                      # AdminHub + Người dùng, API Keys, Audit, Tenants,
                                # MCP, Knowledge Base, Database, LoRA, Widget
    notifications/              # chuông + badge chưa đọc
    canvas/ share/              # Canvas artifact, đọc chat được chia sẻ
```

Quy ước:

- Mỗi feature tự quản `*_models.dart`, `*_repository.dart`, `*_provider.dart`, `*_screen.dart`.
- Widget không gọi Dio trực tiếp; luôn đi qua repository của feature.
- Stream API (chat, research) dùng `ApiClient.stream` + `ChatSseParser`, không tự parse trong widget.
- Marker giao thức (`[THINKING]`, `[REASONING]`, `[WEB_SEARCH]`, `[FILE:...]`,
  `[HITL_APPROVAL_REQUIRED:...]`, `[DONE]`, `[ERROR]`) được xử lý ở parser/model,
  không lộ ra UI.

## Trạng thái parity

Mọi route của web client đều có màn tương ứng trên mobile:

| Nhóm | Nội dung |
|---|---|
| Chat | streaming SSE, attachment (8 file/20 MB), tìm kiếm/đổi tên/xoá phiên, chat tạm thời, regenerate, sửa tin nhắn cuối, share link, HITL approve/reject, tải file agent tạo, Canvas artifact (list/version/chèn vào composer), thoại LiveKit (kết nối/mute/thu hồi consent), biểu đồ `<chart>` trong câu trả lời, định dạng đậm/xuống dòng như desktop |
| Workspace | Projects (sửa, xem đoạn chat, chat mới trong dự án), Documents (tìm kiếm, grid/list, upload), Memory, Custom Instructions |
| AI Studio | Custom Agents (persona, công cụ, tài liệu ghim, LoRA adapter, chat với agent), Schedules, Agent Runs (chạy mới + theo dõi SSE + chi tiết bước), Deep Research, Approvals, Notifications, Text Analysis (5 chức năng), Vision/OCR (5 chức năng), Content Creation Pipeline |
| Quản trị | AdminHub, Người dùng (tạo/đổi quyền/khoá), API Keys (hạn dùng + hạn mức), Audit (facets + lọc + phân trang), Tenants (kèm logo), MCP Servers (catalog + OAuth 2.0 authorization-code), Knowledge Base, Database Connections, LoRA Adapters, Widget Settings |

Khác biệt có chủ đích so với web:

- `/widget/chat` là trang nhúng iframe cho site bên ngoài — không phải màn native.
- Thoại realtime dùng LiveKit như desktop nhưng UI là màn riêng (nút micro nổi của
  desktop không hợp với vùng chạm mobile).

## Giao diện

Giao diện bám đúng token của web (`LmKitOmniClient/src/style.css` + `AppLayout.vue`),
không tự đặt màu/kích thước riêng ở từng màn:

| Hạng mục | Giá trị | Nguồn bên web |
|---|---|---|
| Chrome (header, tab active) | `#1E3A8A` | `--color-gov-blue-dark` |
| Đỏ quốc kỳ (nguy hiểm, viền tiêu điểm) | `#B81F33` | `--color-gov-red` |
| Vàng sao (chỉ báo tab) | `#FFCD00` | `--color-gov-yellow` |
| Nền trang / chữ chính / chữ phụ | `#F8FAFC` · `#111827` · `#4B5563` | `--color-chatgpt-*` |
| Bo góc thẻ / ô nhập | 12px / 8px | `rounded-xl` / `rounded-lg` |
| Header | cao 56px, chữ trắng | `h-14` |
| Font | Be Vietnam Pro, chỉ theme sáng | font-family web |
| Bong bóng chat | người dùng: thẻ trắng bo 24px (`rounded-3xl rounded-tr-sm`), tối đa 80% bề ngang; trợ lý: nằm trực tiếp trên nền trang | `ChatView.vue` |

Forui là design system chính (nút, ô nhập, thẻ, alert qua `lib/app/ui/app_controls.dart`);
Material chỉ còn ở lớp hạ tầng (`Scaffold`, `AppBar`, danh sách).

## Kiểm thử

```bash
flutter analyze   # clean
flutter test      # 170 test
flutter build apk --release --dart-define-from-file=env/prod.json
```

Trong đó `test/government_style_test.dart` ghim bảng màu/kích thước chrome vào đúng
token web (đổi theme là test đỏ ngay), và `test/layout_test.dart` duyệt toàn bộ
màn ở khổ 320dp để bắt lỗi tràn dòng, căn lệch và chồng chữ.
