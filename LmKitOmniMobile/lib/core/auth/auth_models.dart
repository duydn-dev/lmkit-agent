class TenantBranding {
  const TenantBranding({required this.name, this.agentName, this.logoUrl});

  final String name;
  final String? agentName;
  final String? logoUrl;

  factory TenantBranding.fromJson(Map<String, dynamic> json) => TenantBranding(
    name: json['name'] as String? ?? '',
    agentName: json['agentName'] as String?,
    logoUrl: json['logoUrl'] as String?,
  );
}

class UserModel {
  const UserModel({
    required this.id,
    required this.email,
    required this.fullName,
    required this.role,
    required this.tenantId,
    this.tenant,
  });

  final String id;
  final String email;
  final String fullName;
  final String role;
  final String tenantId;
  final TenantBranding? tenant;

  bool get isAdmin => role.toLowerCase() == 'admin';
  String get agentName => tenant?.agentName?.trim().isNotEmpty == true
      ? tenant!.agentName!.trim()
      : 'CILA - AI Agent';

  factory UserModel.fromJson(Map<String, dynamic> json) => UserModel(
    id: json['id']?.toString() ?? '',
    email: json['email'] as String? ?? '',
    fullName: json['fullName'] as String? ?? '',
    role: json['role'] as String? ?? 'Member',
    tenantId: json['tenantId']?.toString() ?? '',
    tenant: json['tenant'] is Map<String, dynamic>
        ? TenantBranding.fromJson(json['tenant'] as Map<String, dynamic>)
        : null,
  );
}

class AuthSession {
  const AuthSession({
    required this.accessToken,
    required this.refreshToken,
    required this.accessTokenExpiresAt,
    required this.refreshTokenExpiresAt,
    required this.user,
  });

  final String accessToken;
  final String refreshToken;
  final DateTime? accessTokenExpiresAt;
  final DateTime? refreshTokenExpiresAt;
  final UserModel user;

  AuthSession copyWith({
    String? accessToken,
    String? refreshToken,
    DateTime? accessTokenExpiresAt,
    DateTime? refreshTokenExpiresAt,
    UserModel? user,
  }) => AuthSession(
    accessToken: accessToken ?? this.accessToken,
    refreshToken: refreshToken ?? this.refreshToken,
    accessTokenExpiresAt: accessTokenExpiresAt ?? this.accessTokenExpiresAt,
    refreshTokenExpiresAt: refreshTokenExpiresAt ?? this.refreshTokenExpiresAt,
    user: user ?? this.user,
  );

  factory AuthSession.fromLoginJson(Map<String, dynamic> json) {
    final userJson = json['user'] is Map<String, dynamic>
        ? json['user'] as Map<String, dynamic>
        : json;
    return AuthSession(
      accessToken: json['accessToken'] as String? ?? '',
      refreshToken: json['refreshToken'] as String? ?? '',
      accessTokenExpiresAt: _parseDate(json['accessTokenExpiresAtUtc']),
      refreshTokenExpiresAt: _parseDate(json['refreshTokenExpiresAtUtc']),
      user: UserModel.fromJson(userJson),
    );
  }

  static DateTime? _parseDate(Object? value) =>
      value is String ? DateTime.tryParse(value) : null;
}
