import 'package:flutter_test/flutter_test.dart';
import 'package:lmkit_omni_mobile/core/network/api_exception.dart';
import 'package:lmkit_omni_mobile/features/admin/admin_models.dart';
import 'package:lmkit_omni_mobile/features/admin/admin_repository.dart';

void main() {
  test('tenant đọc đúng cờ logo và số liệu', () {
    final tenant = TenantModel.fromJson({
      'id': 'T1',
      'name': 'Đơn vị A',
      'agentDisplayName': 'Trợ lý A',
      'hasLogo': true,
      'logoUpdatedAt': '2026-09-01T00:00:00Z',
      'userCount': 12,
      'databaseConnectionCount': 2,
    });
    expect(tenant.id, 'T1');
    expect(tenant.agentDisplayName, 'Trợ lý A');
    expect(tenant.hasLogo, isTrue);
    expect(tenant.userCount, 12);
    expect(tenant.databaseConnectionCount, 2);
  });

  test('MCP server đọc đúng key OAuth viết thường', () {
    final server = McpServerModel.fromJson({
      'id': 'M1',
      'name': 'context7',
      'url': 'https://mcp.context7.com/mcp',
      'isActive': true,
      'authMode': 'ClientCredentials',
      'oauthClientId': 'client-1',
      'oauthTokenUrl': 'https://auth.example.com/token',
    });
    expect(server.authMode, 'ClientCredentials');
    expect(server.oauthClientId, 'client-1');
    expect(server.oauthTokenUrl, 'https://auth.example.com/token');
    expect(server.hasHeaders, isFalse);
  });

  test('kết nối CSDL phân biệt toàn hệ thống và theo tenant', () {
    final global = DatabaseConnectionModel.fromJson({
      'id': 'D1',
      'name': 'Kho chung',
      'provider': 'Postgres',
      'isGlobal': true,
    });
    expect(global.isGlobal, isTrue);

    final scoped = DatabaseConnectionModel.fromJson({
      'id': 'D2',
      'name': 'Kho tenant',
      'provider': 'MySql',
      'tenantId': 'T1',
      'tenantName': 'Đơn vị A',
    });
    expect(scoped.isGlobal, isFalse);
    expect(scoped.tenantName, 'Đơn vị A');
  });

  test('LoRA adapter hiển thị dung lượng dễ đọc', () {
    final adapter = LoraAdapterModel.fromJson({
      'id': 'L1',
      'name': 'style-v1',
      'scale': 0.8,
      'fileSizeBytes': 4 * 1024 * 1024,
      'isActive': true,
    });
    expect(adapter.scale, 0.8);
    expect(adapter.displaySize, '4.0 MB');
    expect(adapter.isActive, isTrue);
  });

  test('widget settings mặc định vị trí góc phải dưới', () {
    final settings = WidgetSettingsModel.fromJson({
      'isActive': true,
      'allowedOrigins': ['https://example.com'],
    });
    expect(settings.position, 'bottom-right');
    expect(settings.allowedOrigins, ['https://example.com']);
  });

  group('PagedResult', () {
    test('đọc items và cờ còn dữ liệu', () {
      final page = PagedResult.fromJson({
        'items': [
          {'name': 'a'},
          {'name': 'b'},
        ],
        'page': 2,
        'pageSize': 2,
        'totalCount': 6,
        'totalPages': 3,
      }, (json) => json['name'] as String);

      expect(page.items, ['a', 'b']);
      expect(page.totalCount, 6);
      expect(page.hasMore, isTrue);
    });

    test('trang cuối không còn dữ liệu để tải thêm', () {
      final page = PagedResult.fromJson({
        'items': [
          {'name': 'z'},
        ],
        'page': 3,
        'totalPages': 3,
      }, (json) => json['name'] as String);

      expect(page.hasMore, isFalse);
    });

    test('append gộp dữ liệu và lấy tiến độ của trang sau', () {
      const first = PagedResult<String>(
        items: ['a', 'b'],
        page: 1,
        pageSize: 2,
        totalCount: 3,
        totalPages: 2,
      );
      const second = PagedResult<String>(
        items: ['c'],
        page: 2,
        pageSize: 2,
        totalCount: 3,
        totalPages: 2,
      );

      final merged = first.append(second);

      expect(merged.items, ['a', 'b', 'c']);
      expect(merged.page, 2);
      expect(merged.hasMore, isFalse);
    });

    test('thiếu field thì dùng mặc định an toàn', () {
      final page = PagedResult.fromJson(const {}, (json) => json.toString());

      expect(page.items, isEmpty);
      expect(page.page, 1);
      expect(page.totalCount, 0);
      expect(page.hasMore, isFalse);
    });
  });

  group('AdminUserModel', () {
    test('đọc đủ trường và nhận diện Admin', () {
      final user = AdminUserModel.fromJson({
        'id': 'U1',
        'email': 'a@example.com',
        'fullName': 'Nguyễn A',
        'role': 'Admin',
        'isActive': true,
        'failedLoginAttempts': 3,
        'createdAt': '2026-09-01T10:00:00Z',
      });

      expect(user.isAdmin, isTrue);
      expect(user.fullName, 'Nguyễn A');
      expect(user.failedLoginAttempts, 3);
      expect(user.createdAt, isNotNull);
    });

    test('khoá tạm chỉ đúng khi lockoutEnd còn ở tương lai', () {
      final locked = AdminUserModel.fromJson({
        'id': 'U2',
        'lockoutEnd': DateTime.now()
            .toUtc()
            .add(const Duration(minutes: 10))
            .toIso8601String(),
      });
      final expired = AdminUserModel.fromJson({
        'id': 'U3',
        'lockoutEnd': DateTime.now()
            .toUtc()
            .subtract(const Duration(minutes: 10))
            .toIso8601String(),
      });

      expect(locked.isLockedOut, isTrue);
      expect(expired.isLockedOut, isFalse);
      expect(AdminUserModel.fromJson(const {'id': 'U4'}).isLockedOut, isFalse);
    });

    test('role mặc định là Member', () {
      final user = AdminUserModel.fromJson(const {'id': 'U5'});

      expect(user.role, 'Member');
      expect(user.isAdmin, isFalse);
    });
  });

  group('AuditLogEntryModel', () {
    test('đọc bản ghi audit', () {
      final entry = AuditLogEntryModel.fromJson({
        'id': 'A1',
        'actorType': 'agent',
        'action': 'AI.Tool.Invoke',
        'entityType': 'web_search',
        'detailsJson': '{"query":"abc"}',
        'createdAtUtc': '2026-09-10T08:00:00Z',
      });

      expect(entry.actorType, 'agent');
      expect(entry.action, 'AI.Tool.Invoke');
      expect(entry.detailsJson, contains('abc'));
      expect(entry.createdAtUtc, isNotNull);
    });
  });

  group('AuditFacetsModel', () {
    test('đọc và bỏ giá trị rỗng', () {
      final facets = AuditFacetsModel.fromJson({
        'actorTypes': ['agent', '', 'user'],
        'actions': ['AI.Tool.Invoke'],
        'entityTypes': ['web_search'],
      });

      expect(facets.actorTypes, ['agent', 'user']);
      expect(facets.actions, ['AI.Tool.Invoke']);
    });

    test('thiếu field thì trả danh sách rỗng', () {
      final facets = AuditFacetsModel.fromJson(const {});

      expect(facets.actorTypes, isEmpty);
      expect(facets.actions, isEmpty);
      expect(facets.entityTypes, isEmpty);
    });
  });

  test('trạng thái OAuth MCP đọc cờ connected và hạn token', () {
    final status = McpOAuthStatusModel.fromJson({
      'connected': true,
      'expiresAtUtc': '2026-10-01T00:00:00Z',
    });

    expect(status.connected, isTrue);
    expect(status.expiresAtUtc, isNotNull);
    expect(McpOAuthStatusModel.fromJson(const {}).connected, isFalse);
  });

  test('isFeatureDisabled chỉ đúng với lỗi 501', () {
    expect(
      isFeatureDisabled(
        const ApiException(message: 'Tính năng đang tắt', statusCode: 501),
      ),
      isTrue,
    );
    expect(
      isFeatureDisabled(
        const ApiException(message: 'Không có quyền', statusCode: 403),
      ),
      isFalse,
    );
    expect(isFeatureDisabled(StateError('boom')), isFalse);
  });
}
