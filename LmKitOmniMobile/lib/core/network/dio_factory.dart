import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import '../config/app_config.dart';

/// Dựng `Dio` từ [AppConfig]. Mọi nơi cần HTTP client đều đi qua đây để
/// base URL, timeout và log chỉ tồn tại ở một chỗ.
Dio buildDio(AppConfig config, {List<Interceptor> interceptors = const []}) {
  final dio = Dio(
    BaseOptions(
      baseUrl: config.apiBaseUrl,
      connectTimeout: config.connectTimeout,
      receiveTimeout: config.receiveTimeout,
      sendTimeout: config.sendTimeout,
      headers: const {'Accept': 'application/json'},
    ),
  );
  if (config.logHttp) {
    dio.interceptors.add(
      LogInterceptor(requestBody: true, responseBody: false),
    );
  }
  dio.interceptors.addAll(interceptors);
  return dio;
}

/// Tên header đánh dấu client mobile, backend dùng để trả token thay vì cookie.
const String kClientPlatformHeader = 'X-Client-Platform';
const String kClientPlatformValue = 'mobile';

/// Log gọn cho interceptor, tránh in token ra console ở release.
void logHttp(String message) {
  if (kDebugMode) debugPrint('[api] $message');
}
