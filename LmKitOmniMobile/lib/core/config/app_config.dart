import 'package:flutter/foundation.dart';

/// Cấu hình runtime của app: mọi giá trị phụ thuộc môi trường nằm ở đây.
///
/// Nguồn duy nhất, theo thứ tự ưu tiên (cao → thấp):
/// 1. `--dart-define-from-file=env/<flavor>.json` — môi trường build/CI quyết định.
/// 2. Hằng số mặc định trong file này.
///
/// **Không có lớp override lúc chạy.** Trước đây app cho đổi API URL ngay trên
/// thiết bị; bỏ đi vì địa chỉ máy chủ là thứ phải cố định theo môi trường đã
/// duyệt, và một URL sai lưu lại trên máy người dùng thì không ai kiểm soát được.
///
/// Không nơi nào khác trong app được hard-code URL hay timeout.
class AppConfig {
  const AppConfig({
    required this.apiBaseUrl,
    required this.webBaseUrl,
    required this.flavor,
    required this.connectTimeout,
    required this.receiveTimeout,
    required this.sendTimeout,
    required this.logHttp,
    this.useMockData = false,
    this.livekitUrlOverride,
  });

  /// Base URL của LmKitOmniApi, luôn ở dạng đã chuẩn hoá (không có dấu `/` cuối).
  final String apiBaseUrl;

  /// Base URL của web client (LmKitOmniClient), dùng để dựng link share.
  final String webBaseUrl;

  /// Tên môi trường build: `dev`, `staging`, `prod`... Chỉ dùng để hiển thị.
  final String flavor;

  final Duration connectTimeout;
  final Duration receiveTimeout;
  final Duration sendTimeout;

  /// Ghi log request/response ra console (chỉ nên bật ở dev).
  final bool logHttp;

  /// `true` = phục vụ **dữ liệu mẫu** ngay tại tầng Dio thay vì gọi mạng.
  ///
  /// Dùng để xem/duyệt giao diện khi chưa có backend. Bật bằng env lúc build
  /// (`--dart-define-from-file=env/dev-mock.json`); không có công tắc lúc chạy.
  final bool useMockData;

  /// URL LiveKit do env chỉ định (`LIVEKIT_URL`); `null` để suy ra từ API host.
  final String? livekitUrlOverride;

  /// URL phòng thoại LiveKit đang dùng.
  ///
  /// Ưu tiên `LIVEKIT_URL` trong env; nếu không có thì suy ra từ host của
  /// [apiBaseUrl] với cổng 7880 — phòng thoại luôn đi cùng máy chủ mà bản build
  /// này trỏ tới.
  String get livekitUrl => normalizeLiveKitUrl(
    livekitUrlOverride ?? defaultLiveKitUrlFor(apiBaseUrl),
  );

  /// URL LiveKit mặc định cho một API base URL.
  static String defaultLiveKitUrlFor(String apiBaseUrl) {
    final uri = Uri.tryParse(apiBaseUrl);
    if (uri == null || uri.host.isEmpty) return 'ws://localhost:7880';
    final scheme = uri.scheme == 'https' ? 'wss' : 'ws';
    return '$scheme://${uri.host}:7880';
  }

  /// Chuẩn hoá URL LiveKit về dạng `ws(s)://host[:port]` không có `/` cuối.
  static String normalizeLiveKitUrl(String raw) {
    final trimmed = raw.trim();
    if (trimmed.isEmpty) return 'ws://localhost:7880';
    var normalized = trimmed.replaceAll(RegExp(r'/+$'), '');
    if (normalized.startsWith('https://')) {
      normalized = 'wss://${normalized.substring(8)}';
    } else if (normalized.startsWith('http://')) {
      normalized = 'ws://${normalized.substring(7)}';
    }
    return normalized;
  }

  /// Đọc cấu hình từ `--dart-define` / `--dart-define-from-file`.
  factory AppConfig.fromEnvironment() {
    // `_fallbackBaseUrl` phụ thuộc nền tảng nên không thể là giá trị const của
    // `String.fromEnvironment`; để trống rồi tự chọn mặc định.
    const raw = String.fromEnvironment('API_BASE_URL');
    return AppConfig(
      apiBaseUrl: raw.trim().isEmpty ? _fallbackBaseUrl : normalizeBaseUrl(raw),
      webBaseUrl: normalizeBaseUrl(
        const String.fromEnvironment(
          'APP_WEB_BASE_URL',
          defaultValue: _fallbackWebBaseUrl,
        ),
      ),
      flavor: const String.fromEnvironment('APP_FLAVOR', defaultValue: 'dev'),
      connectTimeout: Duration(
        seconds: const int.fromEnvironment(
          'API_CONNECT_TIMEOUT_SECONDS',
          defaultValue: 20,
        ),
      ),
      receiveTimeout: Duration(
        seconds: const int.fromEnvironment(
          'API_RECEIVE_TIMEOUT_SECONDS',
          defaultValue: 300,
        ),
      ),
      sendTimeout: Duration(
        seconds: const int.fromEnvironment(
          'API_SEND_TIMEOUT_SECONDS',
          defaultValue: 120,
        ),
      ),
      logHttp: const bool.fromEnvironment('API_LOG_HTTP', defaultValue: false),
      useMockData: const bool.fromEnvironment('USE_MOCK_DATA'),
      livekitUrlOverride:
          const String.fromEnvironment('LIVEKIT_URL').trim().isEmpty
          ? null
          : const String.fromEnvironment('LIVEKIT_URL'),
    );
  }

  /// URL dùng khi không có `API_BASE_URL` nào được truyền vào.
  ///
  /// Android emulator không truy cập được `localhost` của máy dev nên phải dùng
  /// `10.0.2.2`; các nền tảng còn lại đi thẳng `localhost`.
  ///
  /// Dùng `defaultTargetPlatform` thay cho `Platform.isAndroid` để file này
  /// không phải import `dart:io` — chỉ cần một import như vậy là bản build web
  /// không biên dịch được, dù đã bọc trong `kIsWeb`.
  static String get _fallbackBaseUrl {
    if (kIsWeb) return 'http://localhost:5032';
    if (defaultTargetPlatform == TargetPlatform.android) {
      return 'http://10.0.2.2:5032';
    }
    return 'http://localhost:5032';
  }

  /// Web client chạy ở cổng Vite mặc định khi dev.
  static const _fallbackWebBaseUrl = 'http://localhost:5173';

  static String get fallbackBaseUrl => _fallbackBaseUrl;

  /// Bỏ khoảng trắng và `/` cuối để ghép path không bị `//`.
  static String normalizeBaseUrl(String raw) {
    final trimmed = raw.trim();
    if (trimmed.isEmpty) return _fallbackBaseUrl;
    return trimmed.replaceAll(RegExp(r'/+$'), '');
  }

  /// Link công khai của một phiên chat đã share.
  String shareUrlFor(String token) => '$webBaseUrl/share/$token';

  AppConfig copyWith({
    String? apiBaseUrl,
    String? webBaseUrl,
    String? flavor,
    Duration? connectTimeout,
    Duration? receiveTimeout,
    Duration? sendTimeout,
    bool? logHttp,
    bool? useMockData,
    String? livekitUrlOverride,
  }) => AppConfig(
    apiBaseUrl: apiBaseUrl ?? this.apiBaseUrl,
    webBaseUrl: webBaseUrl ?? this.webBaseUrl,
    flavor: flavor ?? this.flavor,
    connectTimeout: connectTimeout ?? this.connectTimeout,
    receiveTimeout: receiveTimeout ?? this.receiveTimeout,
    sendTimeout: sendTimeout ?? this.sendTimeout,
    logHttp: logHttp ?? this.logHttp,
    useMockData: useMockData ?? this.useMockData,
    livekitUrlOverride: livekitUrlOverride ?? this.livekitUrlOverride,
  );
}
