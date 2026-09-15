import 'package:dio/dio.dart';

import '../network/api_client.dart';
import '../network/api_exception.dart';
import 'auth_models.dart';
import 'secure_session_store.dart';

class AuthRepository {
  AuthRepository({required this.client, required this.store});

  final ApiClient client;
  final SecureSessionStore store;

  Future<AuthSession> login({
    required String email,
    required String password,
  }) async {
    try {
      final response = await client.post(
        '/api/auth/login',
        data: {'email': email, 'password': password},
      );
      final session = AuthSession.fromLoginJson(
        response.data as Map<String, dynamic>,
      );
      await store.write(session);
      return session;
    } on ApiException {
      rethrow;
    } on DioException catch (error) {
      throw ApiException.fromDio(error);
    }
  }

  Future<bool> refresh() async {
    final current = await store.read();
    if (current == null || current.refreshToken.isEmpty) return false;
    try {
      final raw = Dio(
        BaseOptions(
          baseUrl: client.dio.options.baseUrl,
          connectTimeout: client.dio.options.connectTimeout,
          receiveTimeout: client.dio.options.receiveTimeout,
          headers: const {'X-Client-Platform': 'mobile'},
        ),
      );
      final response = await raw.post(
        '/api/auth/refresh',
        data: {'refreshToken': current.refreshToken},
      );
      final refreshed = AuthSession.fromLoginJson({
        ...response.data as Map<String, dynamic>,
        'user': {
          'id': current.user.id,
          'email': current.user.email,
          'fullName': current.user.fullName,
          'role': current.user.role,
          'tenantId': current.user.tenantId,
          'tenant': current.user.tenant == null
              ? null
              : {
                  'name': current.user.tenant!.name,
                  'agentName': current.user.tenant!.agentName,
                  'logoUrl': current.user.tenant!.logoUrl,
                },
        },
      });
      await store.write(refreshed);
      return true;
    } catch (_) {
      await store.clear();
      return false;
    }
  }

  Future<AuthSession?> restore() async {
    final current = await store.read();
    if (current == null) return null;
    try {
      final response = await client.get('/api/auth/me');
      final user = UserModel.fromJson(response.data as Map<String, dynamic>);
      final restored = current.copyWith(user: user);
      await store.write(restored);
      return restored;
    } catch (_) {
      if (!await refresh()) return null;
      final refreshed = await store.read();
      if (refreshed == null) return null;
      try {
        final response = await client.get('/api/auth/me');
        final restored = refreshed.copyWith(
          user: UserModel.fromJson(response.data as Map<String, dynamic>),
        );
        await store.write(restored);
        return restored;
      } catch (_) {
        await store.clear();
        return null;
      }
    }
  }

  Future<void> logout() async {
    try {
      await client.post('/api/auth/logout');
    } finally {
      await store.clear();
    }
  }
}
