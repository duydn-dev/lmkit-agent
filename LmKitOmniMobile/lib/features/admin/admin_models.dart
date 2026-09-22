/// Model cho nhóm màn quản trị (admin). Tên field khớp JSON camelCase của API.
class TenantModel {
  const TenantModel({
    required this.id,
    required this.name,
    this.agentDisplayName,
    this.hasLogo = false,
    this.logoUpdatedAt,
    this.createdAt,
    this.userCount = 0,
    this.databaseConnectionCount = 0,
  });

  final String id;
  final String name;
  final String? agentDisplayName;
  final bool hasLogo;
  final DateTime? logoUpdatedAt;
  final DateTime? createdAt;
  final int userCount;
  final int databaseConnectionCount;

  factory TenantModel.fromJson(Map<String, dynamic> json) => TenantModel(
    id: json['id']?.toString() ?? '',
    name: json['name'] as String? ?? '',
    agentDisplayName: json['agentDisplayName'] as String?,
    hasLogo: json['hasLogo'] as bool? ?? false,
    logoUpdatedAt: _date(json['logoUpdatedAt']),
    createdAt: _date(json['createdAt']),
    userCount: (json['userCount'] as num?)?.toInt() ?? 0,
    databaseConnectionCount:
        (json['databaseConnectionCount'] as num?)?.toInt() ?? 0,
  );
}

class TenantOptionModel {
  const TenantOptionModel({required this.id, required this.name});

  final String id;
  final String name;

  factory TenantOptionModel.fromJson(Map<String, dynamic> json) =>
      TenantOptionModel(
        id: json['id']?.toString() ?? '',
        name: json['name'] as String? ?? '',
      );
}

class McpServerModel {
  const McpServerModel({
    required this.id,
    required this.name,
    required this.url,
    this.isActive = true,
    this.trustReadOnlyAnnotations = false,
    this.hasHeaders = false,
    this.authMode = 'Static',
    this.oauthClientId,
    this.oauthTokenUrl,
    this.oauthAuthorizeUrl,
    this.oauthScopes,
  });

  final String id;
  final String name;
  final String url;
  final bool isActive;
  final bool trustReadOnlyAnnotations;
  final bool hasHeaders;
  final String authMode;
  final String? oauthClientId;
  final String? oauthTokenUrl;
  final String? oauthAuthorizeUrl;
  final String? oauthScopes;

  factory McpServerModel.fromJson(Map<String, dynamic> json) => McpServerModel(
    id: json['id']?.toString() ?? '',
    name: json['name'] as String? ?? '',
    url: json['url'] as String? ?? '',
    isActive: json['isActive'] as bool? ?? true,
    trustReadOnlyAnnotations:
        json['trustReadOnlyAnnotations'] as bool? ?? false,
    hasHeaders: json['hasHeaders'] as bool? ?? false,
    authMode: json['authMode'] as String? ?? 'Static',
    oauthClientId: json['oauthClientId'] as String?,
    oauthTokenUrl: json['oauthTokenUrl'] as String?,
    oauthAuthorizeUrl: json['oauthAuthorizeUrl'] as String?,
    oauthScopes: json['oauthScopes'] as String?,
  );
}

class McpCatalogEntryModel {
  const McpCatalogEntryModel({
    required this.id,
    required this.url,
    required this.description,
  });

  final String id;
  final String url;
  final String description;

  factory McpCatalogEntryModel.fromJson(Map<String, dynamic> json) =>
      McpCatalogEntryModel(
        id: json['id']?.toString() ?? '',
        url: json['url'] as String? ?? '',
        description: json['description'] as String? ?? '',
      );
}

class DatabaseConnectionModel {
  const DatabaseConnectionModel({
    required this.id,
    required this.name,
    required this.provider,
    this.tenantId,
    this.tenantName,
    this.isActive = false,
    this.allowWrites = false,
    this.isIndexed = false,
    this.indexStatus = '',
    this.lastIndexError,
    this.lastIndexedAtUtc,
  });

  final String id;
  final String? tenantId;
  final String? tenantName;
  final String name;
  final String provider;
  final bool isActive;
  final bool allowWrites;
  final bool isIndexed;
  final String indexStatus;
  final String? lastIndexError;
  final DateTime? lastIndexedAtUtc;

  bool get isGlobal => tenantId == null || tenantId!.isEmpty;

  factory DatabaseConnectionModel.fromJson(Map<String, dynamic> json) =>
      DatabaseConnectionModel(
        id: json['id']?.toString() ?? '',
        tenantId: json['tenantId']?.toString(),
        tenantName: json['tenantName'] as String?,
        name: json['name'] as String? ?? '',
        provider: json['provider'] as String? ?? '',
        isActive: json['isActive'] as bool? ?? false,
        allowWrites: json['allowWrites'] as bool? ?? false,
        isIndexed: json['isIndexed'] as bool? ?? false,
        indexStatus: json['indexStatus'] as String? ?? '',
        lastIndexError: json['lastIndexError'] as String?,
        lastIndexedAtUtc: _date(json['lastIndexedAtUtc']),
      );
}

class LoraAdapterModel {
  const LoraAdapterModel({
    required this.id,
    required this.name,
    this.description,
    this.scale = 1,
    this.targetModelId,
    this.fileSizeBytes = 0,
    this.isActive = false,
  });

  final String id;
  final String name;
  final String? description;
  final double scale;
  final String? targetModelId;
  final int fileSizeBytes;
  final bool isActive;

  String get displaySize => fileSizeBytes >= 1024 * 1024
      ? '${(fileSizeBytes / (1024 * 1024)).toStringAsFixed(1)} MB'
      : '${(fileSizeBytes / 1024).round()} KB';

  factory LoraAdapterModel.fromJson(Map<String, dynamic> json) =>
      LoraAdapterModel(
        id: json['id']?.toString() ?? '',
        name: json['name'] as String? ?? '',
        description: json['description'] as String?,
        scale: (json['scale'] as num?)?.toDouble() ?? 1,
        targetModelId: json['targetModelId'] as String?,
        fileSizeBytes: (json['fileSizeBytes'] as num?)?.toInt() ?? 0,
        isActive: json['isActive'] as bool? ?? false,
      );
}

class WidgetSettingsModel {
  const WidgetSettingsModel({
    this.isActive = false,
    this.allowedOrigins = const [],
    this.requestsPerMinute = 0,
    this.requestsPerDay = 0,
    this.widgetTitle,
    this.welcomeMessage,
    this.brandColor,
    this.logoUrl,
    this.position = 'bottom-right',
    this.rotatedAtUtc,
  });

  final bool isActive;
  final List<String> allowedOrigins;
  final int requestsPerMinute;
  final int requestsPerDay;
  final String? widgetTitle;
  final String? welcomeMessage;
  final String? brandColor;
  final String? logoUrl;
  final String position;
  final DateTime? rotatedAtUtc;

  factory WidgetSettingsModel.fromJson(Map<String, dynamic> json) =>
      WidgetSettingsModel(
        isActive: json['isActive'] as bool? ?? false,
        allowedOrigins:
            (json['allowedOrigins'] as List<dynamic>?)
                ?.map((item) => item.toString())
                .toList() ??
            const [],
        requestsPerMinute: (json['requestsPerMinute'] as num?)?.toInt() ?? 0,
        requestsPerDay: (json['requestsPerDay'] as num?)?.toInt() ?? 0,
        widgetTitle: json['widgetTitle'] as String?,
        welcomeMessage: json['welcomeMessage'] as String?,
        brandColor: json['brandColor'] as String?,
        logoUrl: json['logoUrl'] as String?,
        position: json['position'] as String? ?? 'bottom-right',
        rotatedAtUtc: _date(json['rotatedAtUtc']),
      );
}

DateTime? _date(Object? value) =>
    value is String ? DateTime.tryParse(value) : null;

/// Một trang dữ liệu theo chuẩn `PagedResult<T>` của backend.
class PagedResult<T> {
  const PagedResult({
    this.items = const [],
    this.page = 1,
    this.pageSize = 20,
    this.totalCount = 0,
    this.totalPages = 0,
  });

  final List<T> items;
  final int page;
  final int pageSize;
  final int totalCount;
  final int totalPages;

  bool get hasMore => page < totalPages;

  factory PagedResult.fromJson(
    Map<String, dynamic> json,
    T Function(Map<String, dynamic>) parse,
  ) => PagedResult(
    items: (json['items'] as List<dynamic>? ?? const [])
        .whereType<Map>()
        .map((row) => parse(Map<String, dynamic>.from(row)))
        .toList(),
    page: (json['page'] as num?)?.toInt() ?? 1,
    pageSize: (json['pageSize'] as num?)?.toInt() ?? 20,
    totalCount: (json['totalCount'] as num?)?.toInt() ?? 0,
    totalPages: (json['totalPages'] as num?)?.toInt() ?? 0,
  );

  /// Trang kế tiếp gộp vào trang hiện tại (dùng cho nút "Tải thêm").
  PagedResult<T> append(PagedResult<T> next) => PagedResult(
    items: [...items, ...next.items],
    page: next.page,
    pageSize: next.pageSize,
    totalCount: next.totalCount,
    totalPages: next.totalPages,
  );
}

/// Người dùng trong tenant (khớp `UserSummaryDto`).
class AdminUserModel {
  const AdminUserModel({
    required this.id,
    required this.email,
    this.fullName = '',
    this.role = 'Member',
    this.isActive = true,
    this.createdAt,
    this.updatedAt,
    this.failedLoginAttempts = 0,
    this.lockoutEnd,
    this.tenantId,
    this.tenantName,
  });

  final String id;
  final String email;
  final String fullName;
  final String role;
  final bool isActive;
  final DateTime? createdAt;
  final DateTime? updatedAt;
  final int failedLoginAttempts;
  final DateTime? lockoutEnd;
  final String? tenantId;
  /// Tên tenant (admin đa tenant thấy được) — hiển thị cạnh email.
  final String? tenantName;

  bool get isAdmin => role.toLowerCase() == 'admin';

  /// Khóa tạm do đăng nhập sai quá nhiều lần (tự mở khi hết hạn).
  bool get isLockedOut =>
      lockoutEnd != null && lockoutEnd!.isAfter(DateTime.now().toUtc());

  factory AdminUserModel.fromJson(Map<String, dynamic> json) => AdminUserModel(
    id: json['id']?.toString() ?? '',
    email: json['email'] as String? ?? '',
    fullName: json['fullName'] as String? ?? '',
    role: json['role'] as String? ?? 'Member',
    isActive: json['isActive'] as bool? ?? false,
    createdAt: _date(json['createdAt']),
    updatedAt: _date(json['updatedAt']),
    failedLoginAttempts: (json['failedLoginAttempts'] as num?)?.toInt() ?? 0,
    lockoutEnd: _date(json['lockoutEnd']),
    tenantId: json['tenantId']?.toString(),
    tenantName: json['tenantName'] as String?,
  );
}

/// Một dòng audit log.
class AuditLogEntryModel {
  const AuditLogEntryModel({
    required this.id,
    this.actorUserId,
    this.actorType = '',
    this.action = '',
    this.entityType = '',
    this.entityId,
    this.correlationId,
    this.detailsJson,
    this.createdAtUtc,
  });

  final String id;
  final String? actorUserId;
  final String actorType;
  final String action;
  final String entityType;
  final String? entityId;
  final String? correlationId;
  final String? detailsJson;
  final DateTime? createdAtUtc;

  factory AuditLogEntryModel.fromJson(Map<String, dynamic> json) =>
      AuditLogEntryModel(
        id: json['id']?.toString() ?? '',
        actorUserId: json['actorUserId']?.toString(),
        actorType: json['actorType'] as String? ?? '',
        action: json['action'] as String? ?? '',
        entityType: json['entityType'] as String? ?? '',
        entityId: json['entityId']?.toString(),
        correlationId: json['correlationId']?.toString(),
        detailsJson: json['detailsJson'] as String?,
        createdAtUtc: _date(json['createdAtUtc']),
      );
}

/// Giá trị gợi ý cho bộ lọc audit (khớp `AuditFacetsDto`).
class AuditFacetsModel {
  const AuditFacetsModel({
    this.actorTypes = const [],
    this.actions = const [],
    this.entityTypes = const [],
  });

  final List<String> actorTypes;
  final List<String> actions;
  final List<String> entityTypes;

  static List<String> _strings(Object? value) =>
      (value as List<dynamic>?)
          ?.map((item) => item.toString())
          .where((item) => item.isNotEmpty)
          .toList() ??
      const [];

  factory AuditFacetsModel.fromJson(Map<String, dynamic> json) =>
      AuditFacetsModel(
        actorTypes: _strings(json['actorTypes']),
        actions: _strings(json['actions']),
        entityTypes: _strings(json['entityTypes']),
      );
}

/// Trạng thái kết nối OAuth theo từng người dùng cho một MCP server.
class McpOAuthStatusModel {
  const McpOAuthStatusModel({this.connected = false, this.expiresAtUtc});

  final bool connected;
  final DateTime? expiresAtUtc;

  factory McpOAuthStatusModel.fromJson(Map<String, dynamic> json) =>
      McpOAuthStatusModel(
        connected: json['connected'] as bool? ?? false,
        expiresAtUtc: _date(json['expiresAtUtc']),
      );
}

/// Một cột của bảng trong sơ đồ schema.
///
/// `isPrimaryKey`/`isForeignKey` do **backend** xác định (nó đối chiếu danh sách
/// khoá ngoại của bảng), client không tự đoán từ tên cột.
class SchemaColumnModel {
  const SchemaColumnModel({
    required this.name,
    this.dataType = '',
    this.isNullable = true,
    this.isPrimaryKey = false,
    this.isForeignKey = false,
  });

  final String name;
  final String dataType;
  final bool isNullable;
  final bool isPrimaryKey;
  final bool isForeignKey;

  /// Nhãn khoá hiển thị trên thẻ bảng — cột vừa là khoá chính vừa là khoá ngoại
  /// là chuyện thường (bảng nối), nên phải hiện được cả hai.
  String get keyLabel =>
      [if (isPrimaryKey) 'PK', if (isForeignKey) 'FK'].join(', ');

  factory SchemaColumnModel.fromJson(Map<String, dynamic> json) =>
      SchemaColumnModel(
        name: json['name'] as String? ?? '',
        dataType: json['dataType'] as String? ?? '',
        isNullable: json['isNullable'] as bool? ?? true,
        isPrimaryKey: json['isPrimaryKey'] as bool? ?? false,
        isForeignKey: json['isForeignKey'] as bool? ?? false,
      );
}

/// Một khoá ngoại đã được backend tách thành (cột → bảng.cột).
///
/// `isResolved == false` nghĩa là chuỗi introspection không tách được (khoá tổ
/// hợp, thiếu thông tin) — vẫn giữ [raw] để hiển thị thay vì bỏ im lặng.
class SchemaForeignKeyModel {
  const SchemaForeignKeyModel({
    this.column = '',
    this.referencedTable = '',
    this.referencedColumn = '',
    this.raw = '',
    this.isResolved = false,
  });

  final String column;
  final String referencedTable;
  final String referencedColumn;
  final String raw;
  final bool isResolved;

  factory SchemaForeignKeyModel.fromJson(Map<String, dynamic> json) =>
      SchemaForeignKeyModel(
        column: json['column'] as String? ?? '',
        referencedTable: json['referencedTable'] as String? ?? '',
        referencedColumn: json['referencedColumn'] as String? ?? '',
        raw: json['raw'] as String? ?? '',
        isResolved: json['isResolved'] as bool? ?? false,
      );
}

/// Một bảng (entity) của sơ đồ.
class SchemaTableModel {
  const SchemaTableModel({
    required this.name,
    required this.qualifiedName,
    this.schema = '',
    this.columns = const [],
    this.foreignKeys = const [],
  });

  final String schema;
  final String name;

  /// Tên đầy đủ đúng như backend hiển thị (`public.users`, hoặc `users` khi
  /// engine không có schema có nghĩa — SQLite `main` đã được lược bỏ).
  final String qualifiedName;

  final List<SchemaColumnModel> columns;
  final List<SchemaForeignKeyModel> foreignKeys;

  factory SchemaTableModel.fromJson(Map<String, dynamic> json) =>
      SchemaTableModel(
        schema: json['schema'] as String? ?? '',
        name: json['name'] as String? ?? '',
        qualifiedName:
            json['qualifiedName'] as String? ?? json['name']?.toString() ?? '',
        columns: _list(json['columns'], SchemaColumnModel.fromJson),
        foreignKeys: _list(json['foreignKeys'], SchemaForeignKeyModel.fromJson),
      );
}

/// Một cạnh của sơ đồ: bảng con (đang giữ khoá ngoại) trỏ tới bảng cha.
class SchemaRelationModel {
  const SchemaRelationModel({
    required this.fromTable,
    required this.fromColumn,
    required this.toTable,
    this.toColumn = '',
    this.targetIncluded = false,
  });

  final String fromTable;
  final String fromColumn;
  final String toTable;
  final String toColumn;

  /// Bảng cha có nằm trong sơ đồ hay không. `false` = **không được vẽ** (trỏ ra
  /// ngoài phần đã cắt) nhưng vẫn phải liệt kê cho người dùng thấy.
  final bool targetIncluded;

  /// Nhãn cạnh, ví dụ `customer_id → id`.
  String get label => toColumn.isEmpty ? fromColumn : '$fromColumn → $toColumn';

  factory SchemaRelationModel.fromJson(Map<String, dynamic> json) =>
      SchemaRelationModel(
        fromTable: json['fromTable'] as String? ?? '',
        fromColumn: json['fromColumn'] as String? ?? '',
        toTable: json['toTable'] as String? ?? '',
        toColumn: json['toColumn'] as String? ?? '',
        targetIncluded: json['targetIncluded'] as bool? ?? false,
      );
}

/// Sơ đồ schema của một kết nối CSDL ngoài — nguồn dựng hình cho màn
/// `DatabaseDiagramScreen`.
class DatabaseSchemaModel {
  const DatabaseSchemaModel({
    this.connectionId = '',
    this.name = '',
    this.provider = '',
    this.isActive = true,
    this.isIndexed = false,
    this.indexStatus = '',
    this.lastIndexedAtUtc,
    this.tableCount = 0,
    this.totalTableCount = 0,
    this.truncated = false,
    this.tables = const [],
    this.relations = const [],
  });

  final String connectionId;
  final String name;
  final String provider;
  final bool isActive;
  final bool isIndexed;
  final String indexStatus;
  final DateTime? lastIndexedAtUtc;

  /// Số bảng thực sự có trong sơ đồ (đã áp trần hiển thị của backend).
  final int tableCount;

  /// Số bảng introspection trả về, trước khi áp trần.
  final int totalTableCount;

  /// `true` khi sơ đồ bị cắt bớt — màn hình phải nói rõ, không im lặng.
  final bool truncated;

  final List<SchemaTableModel> tables;
  final List<SchemaRelationModel> relations;

  /// Các cạnh không vẽ được vì bảng cha nằm ngoài sơ đồ.
  List<SchemaRelationModel> get externalReferences =>
      relations.where((relation) => !relation.targetIncluded).toList();

  /// Khoá ngoại backend không tách được thành cạnh.
  List<SchemaForeignKeyModel> get unresolvedForeignKeys => [
    for (final table in tables)
      ...table.foreignKeys.where((fk) => !fk.isResolved),
  ];

  /// Cạnh vẽ được: cả hai đầu đều có thẻ trong sơ đồ.
  List<SchemaRelationModel> get drawableRelations {
    final ids = tables.map((table) => table.qualifiedName).toSet();
    return relations
        .where(
          (relation) =>
              relation.targetIncluded &&
              ids.contains(relation.fromTable) &&
              ids.contains(relation.toTable),
        )
        .toList();
  }

  factory DatabaseSchemaModel.fromJson(Map<String, dynamic> json) =>
      DatabaseSchemaModel(
        connectionId: json['connectionId']?.toString() ?? '',
        name: json['name'] as String? ?? '',
        provider: json['provider'] as String? ?? '',
        isActive: json['isActive'] as bool? ?? true,
        isIndexed: json['isIndexed'] as bool? ?? false,
        indexStatus: json['indexStatus'] as String? ?? '',
        lastIndexedAtUtc: _date(json['lastIndexedAtUtc']),
        tableCount: (json['tableCount'] as num?)?.toInt() ?? 0,
        totalTableCount: (json['totalTableCount'] as num?)?.toInt() ?? 0,
        truncated: json['truncated'] as bool? ?? false,
        tables: _list(json['tables'], SchemaTableModel.fromJson),
        relations: _list(json['relations'], SchemaRelationModel.fromJson),
      );
}

/// Danh sách model từ một mảng JSON (bỏ qua phần tử không phải object).
List<T> _list<T>(Object? value, T Function(Map<String, dynamic>) build) {
  if (value is! List) return const [];
  return [
    for (final item in value)
      if (item is Map) build(Map<String, dynamic>.from(item)),
  ];
}
