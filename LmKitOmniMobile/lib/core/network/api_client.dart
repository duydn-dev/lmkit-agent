import 'package:dio/dio.dart';

import '../auth/secure_session_store.dart';
import 'api_exception.dart';
import 'auth_interceptor.dart';

class ApiClient {
  ApiClient({required this.store, required this.refresh}) {
    dio = Dio(
      BaseOptions(
        baseUrl: const String.fromEnvironment(
          'API_BASE_URL',
          defaultValue: 'http://10.0.2.2:5032',
        ),
        connectTimeout: const Duration(seconds: 20),
        receiveTimeout: const Duration(minutes: 5),
        sendTimeout: const Duration(minutes: 2),
        headers: const {'Accept': 'application/json'},
      ),
    );
    dio.interceptors.add(_interceptor);
  }

  final SecureSessionStore store;
  final Future<bool> Function() refresh;
  late final Dio dio;
  late final AuthInterceptor _interceptor = AuthInterceptor(
    store: store,
    refresh: refresh,
    fetch: (options) => dio.fetch(options),
  );

  Future<Response<dynamic>> get(
    String path, {
    Map<String, dynamic>? queryParameters,
  }) => _request(() => dio.get(path, queryParameters: queryParameters));

  Future<Response<dynamic>> post(
    String path, {
    Object? data,
    CancelToken? cancelToken,
  }) => _request(() => dio.post(path, data: data, cancelToken: cancelToken));

  Future<Response<dynamic>> put(String path, {Object? data}) =>
      _request(() => dio.put(path, data: data));

  Future<Response<dynamic>> patch(String path, {Object? data}) =>
      _request(() => dio.patch(path, data: data));

  Future<Response<dynamic>> delete(String path, {Object? data}) =>
      _request(() => dio.delete(path, data: data));

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
      final response = await call();
      return response;
    } on DioException catch (error) {
      throw ApiException.fromDio(error);
    }
  }
}
