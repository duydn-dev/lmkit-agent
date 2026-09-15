import 'package:dio/dio.dart';

import '../auth/secure_session_store.dart';

class AuthInterceptor extends Interceptor {
  AuthInterceptor({
    required this.store,
    required this.refresh,
    required this.fetch,
  });

  final SecureSessionStore store;
  final Future<bool> Function() refresh;
  final Future<Response<dynamic>> Function(RequestOptions options) fetch;
  Future<bool>? _refreshInFlight;

  static const _platformHeader = 'X-Client-Platform';

  @override
  Future<void> onRequest(
    RequestOptions options,
    RequestInterceptorHandler handler,
  ) async {
    options.headers[_platformHeader] = 'mobile';
    final session = await store.read();
    if (session != null && session.accessToken.isNotEmpty) {
      options.headers['Authorization'] = 'Bearer ${session.accessToken}';
    }
    handler.next(options);
  }

  @override
  Future<void> onError(
    DioException err,
    ErrorInterceptorHandler handler,
  ) async {
    final request = err.requestOptions;
    final isAuthRequest =
        request.path.endsWith('/auth/login') ||
        request.path.endsWith('/auth/refresh') ||
        request.path.endsWith('/auth/logout');
    final alreadyRetried = request.extra['authRetry'] == true;

    if (err.response?.statusCode != 401 || isAuthRequest || alreadyRetried) {
      handler.next(err);
      return;
    }

    try {
      _refreshInFlight ??= refresh().whenComplete(
        () => _refreshInFlight = null,
      );
      final refreshed = await _refreshInFlight!;
      if (!refreshed) {
        handler.next(err);
        return;
      }
      final session = await store.read();
      if (session == null) {
        handler.next(err);
        return;
      }
      request.extra['authRetry'] = true;
      request.headers['Authorization'] = 'Bearer ${session.accessToken}';
      final response = await fetch(request);
      handler.resolve(response);
    } on DioException catch (retryError) {
      handler.next(retryError);
    } catch (_) {
      handler.next(err);
    }
  }
}
