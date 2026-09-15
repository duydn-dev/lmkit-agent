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

## File có sẵn

| File | Dùng cho |
|---|---|
| `dev.json` | Android emulator (`10.0.2.2` là alias của `localhost` máy dev). |
| `dev-ios.json` | iOS simulator, macOS/Windows/Linux desktop. |
| `prod.json` | Bản phát hành — **nhớ sửa `API_BASE_URL` thành domain thật.** |

## Chạy

```bash
flutter run --dart-define-from-file=env/dev.json          # Android emulator
flutter run --dart-define-from-file=env/dev-ios.json      # iOS simulator / desktop
flutter build apk --release --dart-define-from-file=env/prod.json
```

VS Code: chọn sẵn cấu hình trong `.vscode/launch.json`, không cần nhớ tham số.

## Đổi API URL mà không cần build lại

Vì env là cấu hình lúc build, app có thêm lớp override chạy trên thiết bị:

- Mở màn **Cấu hình kết nối** (nút trên AppBar, drawer, hoặc link dưới màn đăng nhập).
- Nhập URL, bấm **Kiểm tra kết nối** (app gọi `GET /health`), rồi **Lưu**.
- Đổi URL sẽ xoá phiên hiện tại và yêu cầu đăng nhập lại, vì token cấp cho server cũ.

Thứ tự ưu tiên:

```text
override trên thiết bị  →  env build (--dart-define-from-file)  →  mặc định trong AppConfig
```

Xoá app hoặc bấm **Khôi phục URL từ env** để quay lại giá trị build.

URL LiveKit đi theo `API_BASE_URL`: nếu không đặt `LIVEKIT_URL` thì app dùng
`ws(s)://<host của API>:7880`, nên đổi server trong app là phòng thoại đổi theo.

## Thêm môi trường mới

Tạo `env/<tên>.json` theo cùng bộ key, thêm một entry vào `.vscode/launch.json`,
rồi chạy với `--dart-define-from-file=env/<tên>.json`. Không cần sửa code Dart.
