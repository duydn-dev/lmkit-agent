import 'dart:async';
import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart' show rootBundle;

import '../branding_assets.dart';
import 'mock_fixtures.dart';

/// Thay `HttpClientAdapter` của Dio bằng dữ liệu mẫu: không request nào ra
/// mạng, nhưng **mọi tầng phía trên vẫn chạy như thật**.
///
/// Đặt ở tầng adapter (thay vì fake từng repository) nghĩa là: interceptor xác
/// thực, chuyển đổi JSON, parser SSE, xử lý lỗi và toàn bộ provider đều đi đúng
/// đường code của bản thật — chỉ có dây mạng là giả.
///
/// Cách dùng: bật `USE_MOCK_DATA` (xem `AppConfig.useMockData`).
class MockHttpAdapter implements HttpClientAdapter {
  MockHttpAdapter({this.latency = const Duration(milliseconds: 80)});

  /// Độ trễ giả để trạng thái "đang tải" của app hiện ra như thật.
  final Duration latency;

  /// Endpoint đã bị app gọi nhưng chưa có dữ liệu mẫu.
  ///
  /// Test dùng danh sách này để phát hiện bộ dữ liệu bị lệch khỏi app; ở chế độ
  /// demo thì chỉ ghi log, không làm hỏng màn hình.
  final List<String> unmatched = [];

  /// Mọi request đã đi qua adapter, theo đúng thứ tự phát ra.
  ///
  /// Test dùng để khẳng định client gửi **đúng đường dẫn** và **đúng header**
  /// (ví dụ ảnh logo phải mang `Authorization`) mà không cần máy chủ thật —
  /// điều mà việc chỉ giả lập dữ liệu trả về không kiểm được.
  final List<RequestOptions> requests = [];

  static const _jsonHeaders = {
    Headers.contentTypeHeader: [Headers.jsonContentType],
  };

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    final method = options.method.toUpperCase();
    final path = _normalize(options.uri.path);
    final body = _bodyOf(options.data);

    requests.add(options);

    if (latency > Duration.zero) await Future<void>.delayed(latency);

    // Huỷ (nút Dừng) phải cắt được stream giữa chừng, giống HTTP thật.
    if (cancelFuture != null) {
      var cancelled = false;
      unawaited(cancelFuture.then((_) => cancelled = true));
      if (cancelled) {
        throw DioException.requestCancelled(
          requestOptions: options,
          reason: 'Yêu cầu đã bị huỷ (dữ liệu mẫu).',
        );
      }
    }

    final response = await _route(method, path, options, body);
    return response;
  }

  @override
  void close({bool force = false}) {}

  // --------------------------------------------------------------- routing

  Future<ResponseBody> _route(
    String method,
    String path,
    RequestOptions options,
    Map<String, dynamic> body,
  ) async {
    // --- logo: phải trả về ảnh **thật** để kiểm tra đúng đường ảnh cần header
    // Authorization (`TenantLogo`) — trả 404 thì nhánh ảnh không bao giờ chạy và
    // lỗi "logo tải bằng HTTP client riêng, thiếu Bearer" sẽ lọt qua.
    //
    // Nội dung là **Quốc huy**, đúng mặc định của logo tenant: đơn vị nhà nước
    // không cần tải lên ảnh riêng, và khi họ có tải thì đó cũng là cùng một dấu
    // nhận diện. Dữ liệu mẫu vì thế phản ánh đúng cái người dùng sẽ thấy.
    if (method == 'GET' &&
        (path == '/api/tenant-branding/logo' ||
            RegExp(r'^/api/tenants/[^/]+/logo$').hasMatch(path))) {
      return _assetImage(BrandingAssets.emblemPng);
    }

    if (method == 'GET') {
      switch (path) {
        case '/health':
          return _json(MockFixtures.health);
        case '/api/auth/me':
          return _json(MockFixtures.user());
        case '/api/chat/sessions':
          return _json(MockFixtures.chatSessions());
        case '/api/chat/sessions/search':
          final query = (options.queryParameters['q'] ?? '').toString().trim();
          final rows = MockFixtures.chatSessions()
              .where(
                (session) =>
                    query.isEmpty ||
                    (session['title'] ?? '').toString().toLowerCase().contains(
                      query.toLowerCase(),
                    ),
              )
              .toList();
          return _json(rows);
        case '/api/agents/custom':
          return _json(MockFixtures.customAgentsPage());
        case '/api/agents/custom/tools':
          return _json(MockFixtures.agentTools);
        case '/api/document':
          return _json(
            options.queryParameters['ownedOnly'] == true ||
                    options.queryParameters['ownedOnly'] == 'true'
                ? MockFixtures.knowledgeDocs
                : MockFixtures.documents,
          );
        case '/api/schedules':
          return _json(MockFixtures.schedulesPage());
        case '/api/agent-runs':
          return _json(MockFixtures.agentRunsPage());
        case '/api/taskapproval/pending':
          return _json(MockFixtures.pendingApprovals);
        case '/api/api-keys':
          return _json(MockFixtures.apiKeysPage());
        case '/api/notifications':
          final unreadOnly =
              options.queryParameters['unreadOnly'] == true ||
              options.queryParameters['unreadOnly'] == 'true';
          return _json(
            unreadOnly
                ? MockFixtures.notifications
                      .where((item) => item['isRead'] != true)
                      .toList()
                : MockFixtures.notifications,
          );
        case '/api/projects':
          return _json(MockFixtures.projectsPage());
        case '/api/memory':
          return _json(MockFixtures.memories);
        case '/api/user/custom-instructions':
          return _json(MockFixtures.customInstructions);
        case '/api/canvas':
          return _json(MockFixtures.canvasArtifacts);
        case '/api/speech/token':
          return _json(MockFixtures.voiceToken);
        case '/api/tenants':
          return _json(MockFixtures.tenantsPage());
        case '/api/tenants/options':
          return _json(MockFixtures.tenantOptions);
        case '/api/mcp-servers':
          return _json(MockFixtures.mcpServersPage());
        case '/api/mcp-servers/catalog':
          return _json(MockFixtures.mcpCatalog);
        case '/api/database-connections':
          return _json(MockFixtures.databaseConnectionsPage());
        case '/api/lora-adapters':
          return _json(MockFixtures.loraAdaptersPage());
        case '/api/admin/widget/settings':
          return _json(MockFixtures.widgetSettings);
        case '/api/users':
          return _json(
            MockFixtures.usersPage(
              page: _int(options.queryParameters['page'], 1),
              pageSize: _int(options.queryParameters['pageSize'], 20),
              search: (options.queryParameters['search'] ?? '').toString(),
            ),
          );
        case '/api/audit':
          return _json(
            MockFixtures.auditPage(
              page: _int(options.queryParameters['page'], 1),
              pageSize: _int(options.queryParameters['pageSize'], 25),
            ),
          );
        case '/api/audit/facets':
          return _json(MockFixtures.auditFacets);
      }

      // Sơ đồ schema của một kết nối CSDL (màn `DatabaseDiagramScreen`).
      final schemaConnectionId = _group(
        r'^/api/database-connections/([^/]+)/schema$',
        path,
      );
      if (schemaConnectionId != null) {
        return _json(
          MockFixtures.databaseSchema(connectionId: schemaConnectionId),
        );
      }

      final messageSessionId = _group(
        r'^/api/chat/sessions/([^/]+)/messages$',
        path,
      );
      if (messageSessionId != null) {
        return _json(MockFixtures.chatMessages(messageSessionId));
      }
      final projectId = _group(r'^/api/projects/([^/]+)/sessions$', path);
      if (projectId != null) {
        return _json(
          MockFixtures.chatSessions()
              .where((session) => session['projectId'] == projectId)
              .toList(),
        );
      }
      final runId = _group(r'^/api/agent-runs/([^/]+)$', path);
      if (runId != null) {
        return _json(MockFixtures.agentRunDetail(runId));
      }
      if (_has(r'^/api/canvas/[^/]+/versions$', path)) {
        return _json(MockFixtures.canvasVersions);
      }
      if (_has(r'^/api/canvas/[^/]+$', path)) {
        return _json(MockFixtures.canvasArtifactDetail);
      }
      if (_has(r'^/api/mcp-oauth/[^/]+/authorize$', path)) {
        return _json({
          'url': 'https://oauth.monre.gov.vn/authorize?client_id=cila-mobile',
        });
      }
      if (_has(r'^/api/mcp-oauth/[^/]+/status$', path)) {
        return _json({'connected': true, 'expiresAtUtc': _daysFromNow(30)});
      }
      if (_has(r'^/api/share/chat/[^/]+$', path)) {
        return _json(MockFixtures.sharedChat());
      }
      if (_has(r'^/api/files/[^/]+$', path)) {
        return ResponseBody.fromString(
          MockFixtures.producedFilePreview,
          200,
          headers: const {
            Headers.contentTypeHeader: ['text/markdown; charset=utf-8'],
          },
        );
      }
    }

    if (method == 'POST') {
      if (path == '/api/auth/login') {
        return _json(
          MockFixtures.loginResponse(email: (body['email'] ?? '').toString()),
        );
      }
      if (path == '/api/auth/refresh') return _json(MockFixtures.tokenPair());
      if (path == '/api/chat/stream' || path == '/api/chat/stream-with-files') {
        return _sse(
          MockFixtures.chatStreamEvents((body['message'] ?? '').toString()),
        );
      }
      if (path == '/api/agent-runs') {
        return _sse(
          MockFixtures.agentRunEvents((body['goal'] ?? '').toString()),
        );
      }
      if (path == '/api/research') {
        return _sse(
          MockFixtures.researchEvents((body['query'] ?? '').toString()),
        );
      }
      if (path == '/api/chat/sessions') {
        return _json(MockFixtures.createdSession(body: body));
      }
      if (path == '/api/agents/custom') {
        return _json(
          MockFixtures.customAgent(
            id: 'a-demo-new',
            name: (body['name'] ?? '').toString(),
            personaPrompt: (body['personaPrompt'] ?? '').toString(),
          ),
        );
      }
      if (path == '/api/schedules') {
        return _json(MockFixtures.createdSchedule(body: body));
      }
      if (path == '/api/projects') {
        return _json(MockFixtures.createdProject(body: body));
      }
      if (path == '/api/api-keys') {
        return _json(
          MockFixtures.createdApiKey((body['name'] ?? '').toString()),
        );
      }
      if (path == '/api/users') {
        return _json(
          MockFixtures.createdUser(
            email: (body['email'] ?? '').toString(),
            fullName: (body['fullName'] ?? '').toString(),
            role: (body['role'] ?? 'Member').toString(),
          ),
        );
      }
      if (path == '/api/textanalysis/analyze') {
        return _json(MockFixtures.textAnalyzeResult);
      }
      if (path == '/api/textanalysis/classify') {
        return _json(MockFixtures.classifyResult);
      }
      if (path == '/api/textanalysis/detect-language') {
        return _json(MockFixtures.textLanguageResult);
      }
      if (path == '/api/textanalysis/extract-keywords') {
        return _json(MockFixtures.textKeywordsResult);
      }
      if (path == '/api/textanalysis/embeddings') {
        return _json(MockFixtures.textEmbeddingsResult);
      }
      if (path == '/api/vision/upload') {
        return _json(MockFixtures.visionUploadResult);
      }
      if (path == '/api/vision/analyze') {
        return _json(MockFixtures.visionAnalyzeResult);
      }
      if (path == '/api/vision/ocr') {
        return _json(MockFixtures.visionOcrResult);
      }
      if (path == '/api/vision/classify') {
        return _json(MockFixtures.visionClassifyResult);
      }
      if (path == '/api/vision/remove-background') {
        return _json(MockFixtures.visionBackgroundResult);
      }
      if (path == '/api/speech/transcribe-upload') {
        return _json({'text': 'Tổng hợp số liệu quan trắc tháng 8 giúp tôi.'});
      }
      if (path == '/api/agents/content-creation-pipeline') {
        return _json(MockFixtures.contentPipelineResult);
      }
      if (path == '/api/knowledgebase/ingest') {
        return _json({
          'result':
              'Đã nạp "${(body['fileName'] ?? 'tài liệu').toString()}" vào kho '
              'tri thức (dữ liệu mẫu).',
        });
      }
      if (path == '/api/knowledgebase/query') {
        return _json({
          'result':
              'Tìm thấy 3 đoạn liên quan tới '
              '"${(body['query'] ?? '').toString()}" (dữ liệu mẫu).',
        });
      }
      if (_has(r'^/api/taskapproval/[^/]+/approve$', path)) {
        return _json({
          'result': 'Đã phê duyệt tác vụ trong chế độ dữ liệu mẫu.',
        });
      }
      if (_has(r'^/api/database-connections/[^/]+/test$', path)) {
        return _json({'success': true, 'error': null});
      }
      if (_has(r'^/api/share/chat-sessions/[^/]+$', path)) {
        return _json(MockFixtures.shareLink());
      }
      if (path == '/api/admin/widget/credentials:rotate') {
        return _json(MockFixtures.rotatedWidgetKey);
      }
    }

    if (method == 'PUT') {
      if (path == '/api/user/custom-instructions') {
        return _json({
          'aboutUser': body['aboutUser'],
          'responseStyle': body['responseStyle'],
          'updatedAtUtc': _daysFromNow(0),
        });
      }
      if (_has(r'^/api/users/[^/]+/toggle-status$', path)) {
        return _json({'isActive': false});
      }
      final agentId = _group(r'^/api/agents/custom/([^/]+)$', path);
      if (agentId != null) {
        return _json(
          MockFixtures.customAgent(
            id: agentId,
            name: (body['name'] ?? '').toString(),
            personaPrompt: (body['personaPrompt'] ?? '').toString(),
          ),
        );
      }
    }

    // Mọi thao tác ghi khác trong `/api` coi như thành công.
    //
    // Chỉ những endpoint mà app **đọc** nội dung phản hồi mới cần dữ liệu mẫu
    // riêng (tạo phiên chat, tạo agent, tạo lịch, tạo API key…) — chúng đã được
    // xử lý ở trên. Các thao tác sửa/xoá/bật-tắt đều được các màn hình xử lý
    // bằng cách tải lại danh sách, nên phản hồi rỗng là đúng hành vi.
    if (method != 'GET' && path.startsWith('/api/')) {
      return _json(const <String, dynamic>{});
    }

    unmatched.add('$method $path');
    debugPrint('[mock] chưa có dữ liệu mẫu cho: $method $path');
    // Chỉ đường ĐỌC mới bị coi là thiếu dữ liệu: xem ghi chú ở nhánh ghi phía
    // trên (thao tác ghi trong `/api` đã được xử lý trước khi tới đây).
    // Trả về rỗng thay vì lỗi để màn hình hiển thị trạng thái rỗng thay vì màn
    // báo lỗi — người xem vẫn thấy đúng khung giao diện.
    return _json(
      method == 'GET' ? const <dynamic>[] : const <String, dynamic>{},
    );
  }

  // -------------------------------------------------------------- responses

  static ResponseBody _json(Object? body) =>
      ResponseBody.fromString(jsonEncode(body), 200, headers: _jsonHeaders);

  /// Stream SSE giống backend: `data: <json string>` từng sự kiện một.
  static ResponseBody _sse(
    List<String> events, {
    Duration gap = const Duration(milliseconds: 110),
  }) => ResponseBody(
    _sseChunks(events, gap),
    200,
    headers: const {
      Headers.contentTypeHeader: ['text/event-stream'],
      'cache-control': ['no-cache'],
    },
  );

  static Stream<Uint8List> _sseChunks(
    List<String> events,
    Duration gap,
  ) async* {
    for (final event in events) {
      // jsonEncode giữ payload trên một dòng, đúng như backend phát ra.
      yield Uint8List.fromList(utf8.encode('data: ${jsonEncode(event)}\n\n'));
      if (gap > Duration.zero) await Future<void>.delayed(gap);
    }
  }

  /// Ảnh logo lấy từ asset — trả 404 nếu asset không đọc được để `TenantLogo`
  /// rơi về **Quốc huy** đóng gói trong app (đúng hành vi khi tenant chưa cấu
  /// hình logo hoặc máy chủ không có ảnh).
  static Future<ResponseBody> _assetImage(String asset) async {
    try {
      final data = await rootBundle.load(asset);
      return ResponseBody.fromBytes(
        data.buffer.asUint8List(),
        200,
        headers: const {
          Headers.contentTypeHeader: ['image/png'],
        },
      );
    } catch (_) {
      return ResponseBody.fromString('', 404);
    }
  }

  // ----------------------------------------------------------------- helpers

  /// `true` nếu [path] khớp [pattern] (dùng cho đường dẫn có tham số).
  static bool _has(String pattern, String path) =>
      RegExp(pattern).hasMatch(path);

  /// Nhóm bắt đầu tiên của [pattern] trên [path], `null` nếu không khớp.
  static String? _group(String pattern, String path) =>
      RegExp(pattern).firstMatch(path)?.group(1);

  static String _normalize(String path) {
    if (path.isEmpty) return '/';
    var normalized = path;
    while (normalized.length > 1 && normalized.endsWith('/')) {
      normalized = normalized.substring(0, normalized.length - 1);
    }
    return normalized;
  }

  static Map<String, dynamic> _bodyOf(Object? data) {
    if (data is Map) return Map<String, dynamic>.from(data);
    if (data is FormData) {
      return {for (final field in data.fields) field.key: field.value};
    }
    return const <String, dynamic>{};
  }

  static int _int(Object? value, int fallback) {
    if (value is int) return value;
    if (value is String) return int.tryParse(value) ?? fallback;
    if (value is num) return value.toInt();
    return fallback;
  }

  static String _daysFromNow(int days) =>
      DateTime.now().toUtc().add(Duration(days: days)).toIso8601String();
}
