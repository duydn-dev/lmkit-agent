import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import 'auth_models.dart';

class SecureSessionStore {
  SecureSessionStore(this._storage);

  static const _sessionKey = 'lmkit.mobile.auth.session.v1';
  final FlutterSecureStorage _storage;

  Future<AuthSession?> read() async {
    final raw = await _storage.read(key: _sessionKey);
    if (raw == null || raw.isEmpty) return null;
    try {
      final json = jsonDecode(raw) as Map<String, dynamic>;
      return AuthSession(
        accessToken: json['accessToken'] as String? ?? '',
        refreshToken: json['refreshToken'] as String? ?? '',
        accessTokenExpiresAt: _date(json['accessTokenExpiresAt']),
        refreshTokenExpiresAt: _date(json['refreshTokenExpiresAt']),
        user: UserModel.fromJson(json['user'] as Map<String, dynamic>),
      );
    } catch (_) {
      await clear();
      return null;
    }
  }

  Future<void> write(AuthSession session) async {
    await _storage.write(
      key: _sessionKey,
      value: jsonEncode({
        'accessToken': session.accessToken,
        'refreshToken': session.refreshToken,
        'accessTokenExpiresAt': session.accessTokenExpiresAt?.toIso8601String(),
        'refreshTokenExpiresAt': session.refreshTokenExpiresAt
            ?.toIso8601String(),
        'user': {
          'id': session.user.id,
          'email': session.user.email,
          'fullName': session.user.fullName,
          'role': session.user.role,
          'tenantId': session.user.tenantId,
          'tenant': session.user.tenant == null
              ? null
              : {
                  'name': session.user.tenant!.name,
                  'agentName': session.user.tenant!.agentName,
                  'logoUrl': session.user.tenant!.logoUrl,
                },
        },
      }),
    );
  }

  Future<void> clear() => _storage.delete(key: _sessionKey);

  DateTime? _date(Object? value) =>
      value is String ? DateTime.tryParse(value) : null;
}
