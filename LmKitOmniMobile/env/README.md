# Cấu hình môi trường (env)

Mọi giá trị phụ thuộc môi trường của app mobile nằm trong các file JSON ở đây và
được đọc qua `--dart-define-from-file`. Không hard-code URL ở bất kỳ file Dart nào.

## Các key

| Key | Kiểu | Ý nghĩa |
|---|---|---|
| `API_BASE_URL` | string | Base URL của `LmKitOmniApi`, ví dụ `http://10.0.2.2:5032`. Không có `/api` ở cuối. |
| `APP_WEB_BASE_URL` | string | Base URL của web client (`LmKitOmniClient`), dùng để dựng link share phiên chat. |
| `APP_FLAVOR` | string | `dev` / `staging` / `prod`, chỉ dùng để hiển thị trong màn cấu hình. |
| `API_CONNECT_TIMEOUT_SECONDS` | int | Timeout kết nối. |
| `API_RECEIVE_TIMEOUT_SECONDS` | int | Timeout nhận dữ liệu; chat/SSE cần giá trị lớn (300s). |
| `API_SEND_TIMEOUT_SECONDS` | int | Timeout gửi (upload file). |
| `API_LOG_HTTP` | bool | In log request/response ra console, chỉ bật ở dev. |
| `LIVEKIT_URL` | string | URL phòng thoại LiveKit (`ws://` hoặc `wss://`). Bỏ trống thì app tự suy ra từ host của `API_BASE_URL` với cổng 7880. |
| `USE_MOCK_DATA` | bool | `true` = phục vụ **dữ liệu mẫu** ngay trong app thay vì gọi mạng. Xem mục dưới. |

## File có sẵn

| File | Dùng cho |
|---|---|
| `dev.json` | Android emulator (`10.0.2.2` là alias của `localhost` máy dev). |
| `dev-ios.json` | iOS simulator, macOS/Windows/Linux desktop. |
| `dev-mock.json` | Duyệt giao diện bằng dữ liệu mẫu, không cần backend. |
| `prod.json` | Bản phát hành — **nhớ sửa `API_BASE_URL` thành domain thật.** |

## Chạy

```bash
flutter run --dart-define-from-file=env/dev.json          # Android emulator
flutter run --dart-define-from-file=env/dev-ios.json      # iOS simulator / desktop
flutter run --dart-define-from-file=env/dev-mock.json     # xem giao diện bằng dữ liệu mẫu
flutter build apk --release --dart-define-from-file=env/prod.json
```

VS Code: chọn sẵn cấu hình trong `.vscode/launch.json`, không cần nhớ tham số.

## Đổi API URL

Địa chỉ máy chủ nằm trong `API_BASE_URL` của file env đang dùng — **giá trị
build-time**. App cố ý **không** có màn cấu hình kết nối và không lưu override
trên thiết bị:

- Mọi bản cài trong một môi trường luôn trỏ về đúng máy chủ đã được duyệt.
- Không có đường nào để một lần bấm nhầm để lại URL sai trên máy người dùng.

Muốn đổi thì sửa `API_BASE_URL` trong `env/<flavor>.json` rồi chạy/build lại:

```bash
flutter run --dart-define-from-file=env/dev.json
```

Máy thật (không phải emulator) thì đặt `API_BASE_URL` thành IP LAN của máy chạy
backend — emulator Android dùng `10.0.2.2`, còn máy thật không truy cập được
`localhost` của máy dev.

Màn đăng nhập vẫn hiển thị **Máy chủ: …** (chỉ đọc) để khi báo lỗi cho hỗ trợ thì
biết bản cài đang nói chuyện với máy chủ nào.

URL LiveKit đi theo `API_BASE_URL`: nếu không đặt `LIVEKIT_URL` thì app dùng
`ws(s)://<host của API>:7880`.

## Xem giao diện khi chưa có backend (dữ liệu mẫu)

`USE_MOCK_DATA=true` (đặt trong `env/dev-mock.json`) đặt một adapter dữ liệu mẫu
vào Dio, nên **không request nào ra mạng**
nhưng mọi tầng khác vẫn chạy như thật: interceptor xác thực, chuyển đổi JSON,
parser SSE của chat/agent, provider và toàn bộ giao diện.

- Đăng nhập bằng **bất kỳ** tài khoản/mật khẩu nào (ở `dev-mock.json` form được
  điền sẵn).
- Dữ liệu mẫu nằm ở `lib/core/mock/mock_fixtures.dart`; thêm endpoint mới thì
  khai báo thêm ở đó. Endpoint bị app gọi mà chưa có dữ liệu sẽ được ghi log
  `[mock] chưa có dữ liệu mẫu cho: …` và `test/mock_adapter_test.dart` sẽ đỏ.
- Quay lại gọi máy chủ thật bằng cách chạy lại với env không bật
  `USE_MOCK_DATA` (`env/dev.json`).

## Thêm môi trường mới

Tạo `env/<tên>.json` theo cùng bộ key, thêm một entry vào `.vscode/launch.json`,
rồi chạy với `--dart-define-from-file=env/<tên>.json`. Không cần sửa code Dart.
