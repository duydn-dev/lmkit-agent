import 'package:dio/dio.dart';

import '../auth/secure_session_store.dart';
import '../config/app_config.dart';
import 'api_exception.dart';
import 'auth_interceptor.dart';
import 'dio_factory.dart';

/// HTTP client cho mọi API cần đăng nhập.
///
/// Base URL đến từ [AppConfig]; đổi API URL trong app sẽ dựng lại client này.
class ApiClient {
  ApiClient({
    required this.config,
    required this.store,
    required this.refresh,
    this.onSessionExpired,
  }) {
    dio = buildDio(
      config,
      interceptors: [
        AuthInterceptor(
          store: store,
          refresh: refresh,
          fetch: (options) => dio.fetch(options),
          onSessionExpired: onSessionExpired,
        ),
      ],
    );
  }

  final AppConfig config;
  final SecureSessionStore store;
  final Future<bool> Function() refresh;
  final void Function()? onSessionExpired;
  late final Dio dio;

  String get baseUrl => dio.options.baseUrl;

  Future<Response<dynamic>> get(
    String path, {
    Map<String, dynamic>? queryParameters,
  }) => _request(() => dio.get(path, queryParameters: queryParameters));

  Future<Response<dynamic>> post(
    String path, {
    Object? data,
    Map<String, dynamic>? queryParameters,
    CancelToken? cancelToken,
  }) => _request(
    () => dio.post(
      path,
      data: data,
      queryParameters: queryParameters,
      cancelToken: cancelToken,
    ),
  );

  Future<Response<dynamic>> put(
    String path, {
    Object? data,
    Map<String, dynamic>? queryParameters,
  }) => _request(
    () => dio.put(path, data: data, queryParameters: queryParameters),
  );

  Future<Response<dynamic>> patch(String path, {Object? data}) =>
      _request(() => dio.patch(path, data: data));

  Future<Response<dynamic>> delete(
    String path, {
    Object? data,
    Map<String, dynamic>? queryParameters,
  }) => _request(
    () => dio.delete(path, data: data, queryParameters: queryParameters),
  );

  Future<Response<dynamic>> upload(
    String path, {
    required FormData data,
    CancelToken? cancelToken,
  }) => _request(() => dio.post(path, data: data, cancelToken: cancelToken));

  Future<Response<dynamic>> download(
    String path,
    String savePath, {
    CancelToken? cancelToken,
  }) => _request(() => dio.download(path, savePath, cancelToken: cancelToken));

  Future<Response<dynamic>> stream(
    String path, {
    Object? data,
    CancelToken? cancelToken,
  }) => _request(
    () => dio.post(
      path,
      data: data,
      cancelToken: cancelToken,
      options: Options(responseType: ResponseType.stream),
    ),
  );

  Future<Response<dynamic>> _request(
    Future<Response<dynamic>> Function() call,
  ) async {
    try {
      return await call();
    } on DioException catch (error) {
      throw ApiException.fromDio(error);
    }
  }
}
