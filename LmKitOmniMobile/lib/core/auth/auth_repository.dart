import 'package:dio/dio.dart';

import '../config/app_config.dart';
import '../network/api_exception.dart';
import '../network/dio_factory.dart';
import 'auth_models.dart';
import 'secure_session_store.dart';

/// Các endpoint xác thực (`/api/auth/*`).
///
/// Dùng Dio riêng, **không** gắn [AuthInterceptor]: login/refresh không nên
/// phụ thuộc vào interceptor và refresh không được phép đệ quy.
class AuthRepository {
  AuthRepository({required this.config, required this.store})
    : _dio = buildDio(config);

  final AppConfig config;
  final SecureSessionStore store;
  final Dio _dio;

  Future<AuthSession> login({
    required String email,
    required String password,
  }) async {
    final data = await _post('/api/auth/login', {
      'email': email,
      'password': password,
    });
    final session = AuthSession.fromLoginJson(data);
    await store.write(session);
    return session;
  }

  Future<bool> refresh() async {
    final current = await store.read();
    if (current == null || current.refreshToken.isEmpty) return false;
    try {
      final data = await _post(
        '/api/auth/refresh',
        {'refreshToken': current.refreshToken},
        headers: const {kClientPlatformHeader: kClientPlatformValue},
      );
      // Endpoint refresh chỉ trả token mới, thông tin user giữ nguyên.
      await store.write(
        AuthSession.fromLoginJson({...data, 'user': _userJson(current.user)}),
      );
      return true;
    } catch (_) {
      await store.clear();
      return false;
    }
  }

  Future<AuthSession?> restore() async {
    final current = await store.read();
    if (current == null) return null;

    final fetched = await _fetchMe(current.accessToken);
    if (fetched != null) {
      final restored = current.copyWith(user: fetched);
      await store.write(restored);
      return restored;
    }

    if (!await refresh()) return null;
    final refreshed = await store.read();
    if (refreshed == null) return null;

    final afterRefresh = await _fetchMe(refreshed.accessToken);
    if (afterRefresh == null) {
      await store.clear();
      return null;
    }
    final restored = refreshed.copyWith(user: afterRefresh);
    await store.write(restored);
    return restored;
  }

  Future<void> logout() async {
    // API thu hồi session theo claim `sid` trong access token, nên phải gửi kèm
    // bearer: thiếu token thì refresh token phía server vẫn còn hiệu lực.
    final session = await store.read();
    try {
      await _dio.post<void>(
        '/api/auth/logout',
        options: Options(
          headers: {
            kClientPlatformHeader: kClientPlatformValue,
            if (session != null && session.accessToken.isNotEmpty)
              'Authorization': 'Bearer ${session.accessToken}',
          },
        ),
      );
    } on DioException {
      // Logout phía server là best-effort; token local vẫn phải bị xoá.
    } finally {
      await store.clear();
    }
  }

  Future<UserModel?> _fetchMe(String accessToken) async {
    try {
      final response = await _dio.get<dynamic>(
        '/api/auth/me',
        options: Options(headers: {'Authorization': 'Bearer $accessToken'}),
      );
      final body = response.data;
      if (body is! Map) return null;
      return UserModel.fromJson(Map<String, dynamic>.from(body));
    } catch (_) {
      return null;
    }
  }

  Future<Map<String, dynamic>> _post(
    String path,
    Map<String, dynamic> body, {
    Map<String, String>? headers,
  }) async {
    try {
      final response = await _dio.post<dynamic>(
        path,
        data: body,
        options: Options(
          headers: {kClientPlatformHeader: kClientPlatformValue, ...?headers},
        ),
      );
      final data = response.data;
      if (data is! Map) {
        throw ApiException(
          message: 'Phản hồi không hợp lệ từ $path',
          statusCode: response.statusCode,
        );
      }
      return Map<String, dynamic>.from(data);
    } on DioException catch (error) {
      throw ApiException.fromDio(error);
    }
  }

  Map<String, dynamic> _userJson(UserModel user) => {
    'id': user.id,
    'email': user.email,
    'fullName': user.fullName,
    'role': user.role,
    'tenantId': user.tenantId,
    'tenant': user.tenant == null
        ? null
        : {
            'name': user.tenant!.name,
            'agentName': user.tenant!.agentName,
            'logoUrl': user.tenant!.logoUrl,
          },
  };
}
