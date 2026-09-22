// ignore_for_file: use_null_aware_elements

import 'package:dio/dio.dart';

import '../../core/network/api_client.dart';
import '../../core/network/api_exception.dart';
import 'admin_models.dart';

/// API quản trị: tenant, MCP server, knowledge base, kết nối CSDL, LoRA, widget.
///
/// Mọi màn admin đều dùng chung repository này; không màn nào tự gọi Dio.
class AdminRepository {
  AdminRepository(this._client);

  final ApiClient _client;

  // ---------------------------------------------------------------- tenants

  Future<List<TenantModel>> tenants({String? search}) async {
    final response = await _client.get(
      '/api/tenants',
      queryParameters: _page(search),
    );
    return _rows(response.data).map(TenantModel.fromJson).toList();
  }

  Future<List<TenantOptionModel>> tenantOptions() async {
    final response = await _client.get('/api/tenants/options');
    return _rows(response.data).map(TenantOptionModel.fromJson).toList();
  }

  Future<void> createTenant({required String name, String? agentDisplayName}) =>
      _client
          .post(
            '/api/tenants',
            data: {
              'name': name.trim(),
              'agentDisplayName': agentDisplayName?.trim(),
            },
          )
          .then((_) {});

  Future<void> updateTenant({
    required String id,
    required String name,
    String? agentDisplayName,
  }) => _client
      .put(
        '/api/tenants/$id',
        data: {
          'name': name.trim(),
          'agentDisplayName': agentDisplayName?.trim(),
        },
      )
      .then((_) {});

  Future<void> deleteTenant(String id) =>
      _client.delete('/api/tenants/$id').then((_) {});

  Future<void> uploadTenantLogo(
    String id, {
    required String path,
    required String fileName,
  }) async {
    final form = FormData.fromMap({
      'logo': await MultipartFile.fromFile(path, filename: fileName),
    });
    await _client.upload('/api/tenants/$id/logo', data: form);
  }

  Future<void> deleteTenantLogo(String id) =>
      _client.delete('/api/tenants/$id/logo').then((_) {});

  // ------------------------------------------------------------ MCP servers

  Future<List<McpServerModel>> mcpServers({String? search}) async {
    final response = await _client.get(
      '/api/mcp-servers',
      queryParameters: _page(search),
    );
    return _rows(response.data).map(McpServerModel.fromJson).toList();
  }

  Future<List<McpCatalogEntryModel>> mcpCatalog() async {
    final response = await _client.get('/api/mcp-servers/catalog');
    return _rows(response.data).map(McpCatalogEntryModel.fromJson).toList();
  }

  Future<void> saveMcpServer({
    String? id,
    required String name,
    required String url,
    bool isActive = true,
    bool trustReadOnlyAnnotations = false,
    Map<String, String>? headers,
    bool replaceHeaders = false,
    String? authMode,
    String? oauthClientId,
    String? oauthClientSecret,
    String? oauthTokenUrl,
    String? oauthAuthorizeUrl,
    String? oauthScopes,
  }) async {
    final body = <String, dynamic>{
      'name': name.trim(),
      'url': url.trim(),
      'isActive': isActive,
      'trustReadOnlyAnnotations': trustReadOnlyAnnotations,
      if (headers != null) 'headers': headers,
      if (replaceHeaders) 'replaceHeaders': true,
      if (authMode != null) 'authMode': authMode,
      if (oauthClientId?.trim().isNotEmpty == true)
        'oauthClientId': oauthClientId!.trim(),
      if (oauthClientSecret?.trim().isNotEmpty == true)
        'oauthClientSecret': oauthClientSecret!.trim(),
      if (oauthTokenUrl?.trim().isNotEmpty == true)
        'oauthTokenUrl': oauthTokenUrl!.trim(),
      if (oauthAuthorizeUrl?.trim().isNotEmpty == true)
        'oauthAuthorizeUrl': oauthAuthorizeUrl!.trim(),
      if (oauthScopes?.trim().isNotEmpty == true)
        'oauthScopes': oauthScopes!.trim(),
    };
    if (id == null) {
      await _client.post('/api/mcp-servers', data: body);
    } else {
      await _client.put('/api/mcp-servers/$id', data: body);
    }
  }

  Future<void> deleteMcpServer(String id) =>
      _client.delete('/api/mcp-servers/$id').then((_) {});

  // ---------------------------------------------------------- knowledge base

  Future<String> ingestKnowledge({
    required String fileName,
    required String content,
  }) async {
    final response = await _client.post(
      '/api/knowledgebase/ingest',
      data: {'fileName': fileName.trim(), 'content': content},
    );
    return _asText(response.data);
  }

  Future<String> queryKnowledge({required String query, int topK = 3}) async {
    final response = await _client.post(
      '/api/knowledgebase/query',
      data: {'query': query.trim(), 'topK': topK},
    );
    return _asText(response.data);
  }

  // ----------------------------------------------------- database connections

  Future<List<DatabaseConnectionModel>> databaseConnections({
    String? search,
  }) async {
    final response = await _client.get(
      '/api/database-connections',
      queryParameters: _page(search),
    );
    return _rows(response.data).map(DatabaseConnectionModel.fromJson).toList();
  }

  Future<void> saveDatabaseConnection({
    String? id,
    required String name,
    required String provider,
    String? connectionString,
    bool isActive = true,
    bool allowWrites = false,
    bool isGlobal = false,
    String? tenantId,
  }) async {
    final body = <String, dynamic>{
      'name': name.trim(),
      'provider': provider.trim(),
      'isActive': isActive,
      'allowWrites': allowWrites,
      'isGlobal': isGlobal,
      if (connectionString?.isNotEmpty == true) ...{
        'connectionString': connectionString,
        'replaceConnectionString': id != null,
      },
      if (!isGlobal && tenantId?.isNotEmpty == true) 'tenantId': tenantId,
    };
    if (id == null) {
      await _client.post('/api/database-connections', data: body);
    } else {
      await _client.put('/api/database-connections/$id', data: body);
    }
  }

  Future<void> deleteDatabaseConnection(String id) =>
      _client.delete('/api/database-connections/$id').then((_) {});

  /// Trả về thông báo kết quả; API trả `{ success, error }` cho lần test này.
  Future<String> testDatabaseConnection(String id) async {
    final response = await _client.post('/api/database-connections/$id/test');
    final data = response.data is Map
        ? Map<String, dynamic>.from(response.data as Map)
        : <String, dynamic>{};
    final success = data['success'] as bool? ?? false;
    return success
        ? 'Kết nối thành công.'
        : 'Kết nối thất bại: ${data['error'] ?? 'không rõ nguyên nhân'}';
  }

  Future<void> reindexDatabaseConnection(String id) =>
      _client.post('/api/database-connections/$id/reindex').then((_) {});

  /// Sơ đồ schema (bảng/cột/khoá + quan hệ khoá ngoại) của một kết nối.
  ///
  /// Backend đọc sống bằng đúng phép introspection dựng chỉ mục schema, nên xem
  /// được cả khi kết nối chưa đánh chỉ mục. Chuỗi kết nối không bao giờ trả về.
  Future<DatabaseSchemaModel> databaseSchema(String id) async {
    final response = await _client.get('/api/database-connections/$id/schema');
    final data = response.data;
    return DatabaseSchemaModel.fromJson(
      data is Map ? Map<String, dynamic>.from(data) : const {},
    );
  }

  // ------------------------------------------------------------ LoRA adapter

  /// Ném `ApiException` 501 khi tính năng LoRA bị tắt trên server — màn hình
  /// phân biệt bằng [isFeatureDisabled].
  Future<List<LoraAdapterModel>> loraAdapters() async {
    final response = await _client.get('/api/lora-adapters');
    return _rows(response.data).map(LoraAdapterModel.fromJson).toList();
  }

  Future<void> uploadLoraAdapter({
    required String name,
    required String path,
    required String fileName,
    String? description,
    double? scale,
    String? targetModelId,
  }) async {
    final form = FormData.fromMap({
      'name': name.trim(),
      if (description?.trim().isNotEmpty == true) 'description': description,
      if (scale != null) 'scale': scale,
      if (targetModelId?.trim().isNotEmpty == true)
        'targetModelId': targetModelId,
      'file': await MultipartFile.fromFile(path, filename: fileName),
    });
    await _client.upload('/api/lora-adapters', data: form);
  }

  Future<void> updateLoraAdapter({
    required String id,
    required String name,
    double scale = 1,
    bool isActive = false,
  }) => _client
      .put(
        '/api/lora-adapters/$id',
        data: {'name': name.trim(), 'scale': scale, 'isActive': isActive},
      )
      .then((_) {});

  Future<void> deleteLoraAdapter(String id) =>
      _client.delete('/api/lora-adapters/$id').then((_) {});

  Future<void> assignLoraAdapter({
    required String adapterId,
    required String agentId,
  }) => _client
      .post(
        '/api/lora-adapters/$adapterId/assign',
        queryParameters: {'agentId': agentId},
      )
      .then((_) {});

  Future<void> unassignLoraAdapter(String agentId) => _client
      .delete(
        '/api/lora-adapters/assign',
        queryParameters: {'agentId': agentId},
      )
      .then((_) {});

  // ------------------------------------------------------------ widget admin

  Future<WidgetSettingsModel> widgetSettings() async {
    final response = await _client.get('/api/admin/widget/settings');
    return WidgetSettingsModel.fromJson(_map(response.data));
  }

  Future<void> updateWidgetSettings({
    required bool isActive,
    required List<String> allowedOrigins,
    int? requestsPerMinute,
    int? requestsPerDay,
    String? widgetTitle,
    String? welcomeMessage,
    String? brandColor,
    String? logoUrl,
    String? position,
  }) => _client
      .put(
        '/api/admin/widget/settings',
        data: {
          'isActive': isActive,
          'allowedOrigins': allowedOrigins,
          'requestsPerMinute': ?requestsPerMinute,
          'requestsPerDay': ?requestsPerDay,
          'widgetTitle': widgetTitle,
          'welcomeMessage': welcomeMessage,
          'brandColor': brandColor,
          'logoUrl': logoUrl,
          'position': position,
        },
      )
      .then((_) {});

  /// Trả về raw key mới; chỉ hiển thị được đúng một lần.
  Future<String> rotateWidgetKey() async {
    final response = await _client.post('/api/admin/widget/credentials:rotate');
    return _map(response.data)['rawKey'] as String? ?? '';
  }

  // ------------------------------------------------------------------- MCP OAuth

  /// URL của nhà cung cấp để người dùng mở và cấp quyền (PKCE + state ở server).
  Future<String> mcpAuthorizeUrl(String serverId) async {
    final response = await _client.get('/api/mcp-oauth/$serverId/authorize');
    return _map(response.data)['url'] as String? ?? '';
  }

  /// Trạng thái kết nối của chính người dùng đang đăng nhập.
  Future<McpOAuthStatusModel> mcpOAuthStatus(String serverId) async {
    final response = await _client.get('/api/mcp-oauth/$serverId/status');
    return McpOAuthStatusModel.fromJson(_map(response.data));
  }

  /// Ngắt kết nối: xoá token OAuth đã lưu của người dùng hiện tại.
  Future<void> disconnectMcp(String serverId) =>
      _client.delete('/api/mcp-oauth/$serverId/token').then((_) {});

  // ------------------------------------------------------------------- users

  Future<PagedResult<AdminUserModel>> users({
    String? search,
    int page = 1,
    int pageSize = 20,
  }) async {
    final response = await _client.get(
      '/api/users',
      queryParameters: {
        'page': page,
        'pageSize': pageSize,
        if (search != null && search.trim().isNotEmpty) 'search': search.trim(),
      },
    );
    return PagedResult.fromJson(_map(response.data), AdminUserModel.fromJson);
  }

  Future<AdminUserModel> createUser({
    required String email,
    required String password,
    required String fullName,
    String role = 'Member',
    String? tenantId,
  }) async {
    final response = await _client.post(
      '/api/users',
      data: {
        'email': email.trim(),
        'password': password,
        'fullName': fullName.trim(),
        'role': role,
        // Admin đa tenant: gán user vào tenant bất kỳ; null → tenant của admin.
        if (tenantId != null && tenantId.isNotEmpty) 'tenantId': tenantId,
      },
    );
    return AdminUserModel.fromJson(_map(response.data));
  }

  Future<void> updateUserRole(String id, String role) =>
      _client.put('/api/users/$id/role', data: {'role': role}).then((_) {});

  /// Trả về trạng thái hoạt động sau khi đổi.
  Future<bool> toggleUserStatus(String id) async {
    final response = await _client.put('/api/users/$id/toggle-status');
    return _map(response.data)['isActive'] as bool? ?? false;
  }

  // ------------------------------------------------------------------- audit

  Future<PagedResult<AuditLogEntryModel>> auditLogs({
    String? actorType,
    String? action,
    String? entityType,
    DateTime? fromUtc,
    DateTime? toUtc,
    int page = 1,
    int pageSize = 25,
  }) async {
    final response = await _client.get(
      '/api/audit',
      queryParameters: {
        'page': page,
        'pageSize': pageSize,
        if (actorType != null && actorType.isNotEmpty) 'actorType': actorType,
        if (action != null && action.isNotEmpty) 'action': action,
        if (entityType != null && entityType.isNotEmpty)
          'entityType': entityType,
        if (fromUtc != null) 'fromUtc': fromUtc.toUtc().toIso8601String(),
        if (toUtc != null) 'toUtc': toUtc.toUtc().toIso8601String(),
      },
    );
    return PagedResult.fromJson(
      _map(response.data),
      AuditLogEntryModel.fromJson,
    );
  }

  /// Các giá trị đã từng xuất hiện trong audit log, dùng cho dropdown lọc.
  Future<AuditFacetsModel> auditFacets() async {
    final response = await _client.get('/api/audit/facets');
    return AuditFacetsModel.fromJson(_map(response.data));
  }

  // ----------------------------------------------------------------- helpers

  Map<String, dynamic> _page(String? search) => {
    'page': 1,
    'pageSize': 50,
    if (search != null && search.trim().isNotEmpty) 'search': search.trim(),
  };

  /// API trả về `PagedResult` (có `items`) hoặc list thẳng tuỳ endpoint.
  static List<Map<String, dynamic>> _rows(Object? value) {
    final raw = value is Map ? value['items'] : value;
    if (raw is! List) return const [];
    return raw
        .whereType<Map>()
        .map((row) => Map<String, dynamic>.from(row))
        .toList();
  }

  static Map<String, dynamic> _map(Object? value) =>
      value is Map ? Map<String, dynamic>.from(value) : <String, dynamic>{};

  static String _asText(Object? value) {
    if (value is String) return value;
    if (value is Map) {
      for (final key in ['result', 'message', 'value', 'content']) {
        final candidate = value[key];
        if (candidate is String) return candidate;
      }
      return value.toString();
    }
    return value?.toString() ?? '';
  }
}

/// Chuyển [error] thành thông báo hiển thị được cho người dùng.
String adminErrorMessage(Object error) =>
    error is ApiException ? error.message : error.toString();

/// `true` khi API trả 501 vì tính năng bị tắt ở cấu hình server.
bool isFeatureDisabled(Object error) =>
    error is ApiException && error.statusCode == 501;
