import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_repository.dart';
import 'package:lmkit_omni_mobile/core/config/app_config.dart';
import 'package:lmkit_omni_mobile/core/mock/mock_fixtures.dart';
import 'package:lmkit_omni_mobile/core/mock/mock_http_adapter.dart';
import 'package:lmkit_omni_mobile/core/network/api_client.dart';
import 'package:lmkit_omni_mobile/core/network/dio_factory.dart';
import 'package:lmkit_omni_mobile/core/network/dio_image.dart';
import 'package:lmkit_omni_mobile/core/auth/secure_session_store.dart';
import 'package:lmkit_omni_mobile/features/admin/admin_repository.dart';
import 'package:lmkit_omni_mobile/features/canvas/canvas_repository.dart';
import 'package:lmkit_omni_mobile/features/chat/chat_repository.dart';
import 'package:lmkit_omni_mobile/features/chat/generative_ui.dart';
import 'package:lmkit_omni_mobile/features/share/share_repository.dart';
import 'package:lmkit_omni_mobile/features/studio/studio_repository.dart';
import 'package:lmkit_omni_mobile/features/workspace/workspace_repository.dart';

/// Bộ dữ liệu mẫu phải khớp **từng endpoint và từng tên field** mà app đang gọi.
///
/// Vì vậy các test dưới đây không đọc file JSON — chúng đi qua chính repository
/// thật (đúng model, đúng parser), chỉ thay dây mạng. Bộ dữ liệu lệch khỏi app
/// (đổi tên field, quên endpoint mới) làm test này đỏ ngay.
///
/// Test cuối cùng là lưới an toàn rộng nhất: nó gọi hết mọi endpoint app dùng và
/// khẳng định **không** endpoint nào bị rơi vào nhánh "chưa có dữ liệu mẫu".
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late AppConfig config;
  late SecureSessionStore store;
  late ApiClient client;
  late MockHttpAdapter adapter;

  setUp(() {
    // Storage trong bộ nhớ: session store và config store chạy được không cần
    // plugin thật.
    FlutterSecureStorage.setMockInitialValues({});
    config = AppConfig.fromEnvironment().copyWith(useMockData: true);
    store = SecureSessionStore(const FlutterSecureStorage());
    client = ApiClient(config: config, store: store, refresh: () async => false);
    adapter = client.dio.httpClientAdapter as MockHttpAdapter;
  });

  test('cờ dữ liệu mẫu là thứ duy nhất cài adapter giả', () {
    expect(
      buildDio(config).httpClientAdapter,
      isA<MockHttpAdapter>(),
      reason: 'USE_MOCK_DATA bật thì Dio phải phục vụ dữ liệu mẫu',
    );
    expect(
      buildDio(
        AppConfig.fromEnvironment().copyWith(useMockData: false),
      ).httpClientAdapter,
      isNot(isA<MockHttpAdapter>()),
      reason: 'không bật thì vẫn phải đi mạng thật',
    );
  });

  test('auth: mọi tài khoản đều đăng nhập được, trả về thương hiệu tenant', () async {
    final repository = AuthRepository(config: config, store: store);
    final session = await repository.login(
      email: 'chuyenvien@cila.gov.vn',
      password: 'bat-ky-mat-khau-nao',
    );

    expect(session.accessToken, isNotEmpty);
    expect(session.refreshToken, isNotEmpty);
    expect(session.user.role, 'Admin');
    expect(session.user.tenant?.agentName, MockFixtures.agentName);
    expect(
      session.user.tenant?.logoUrl,
      isNotNull,
      reason: 'thiếu logoUrl thì đầu mỗi câu trả lời mất avatar trợ lý',
    );
  });

  test('auth: khôi phục phiên bằng /api/auth/me khi đã có token', () async {
    final repository = AuthRepository(config: config, store: store);
    await repository.login(email: 'admin@cila.gov.vn', password: 'x');
    final restored = await repository.restore();
    expect(restored, isNotNull);
    expect(restored!.user.email, isNotEmpty);
  });

  test('chat: danh sách phiên và tin nhắn đọc được, có biểu đồ + tệp + nguồn', () async {
    final repository = ChatRepository(client);
    final sessions = await repository.listSessions();
    expect(sessions, isNotEmpty);
    expect(
      sessions.map((session) => session.id),
      contains(MockFixtures.sessionReportId),
    );

    final messages = await repository.getMessages(
      MockFixtures.sessionReportId,
    );
    expect(messages.length, greaterThanOrEqualTo(4));
    expect(messages.first.isUser, isTrue);
    expect(messages.any((message) => message.isAssistant), isTrue);

    final answer = messages.firstWhere((message) => message.isAssistant);
    // Marker của backend phải bị tách khỏi phần chữ hiển thị.
    expect(answer.content, isNot(contains('[REASONING]')));
    expect(answer.content, isNot(contains('[THINKING]')));
    expect(answer.webUrls, isNotEmpty, reason: 'mất chip "Nguồn" trên giao diện');
    expect(
      answer.producedFiles,
      isNotEmpty,
      reason: 'mất thẻ tệp do agent tạo',
    );
    expect(
      parseGenerativeContent(answer.content).charts,
      isNotEmpty,
      reason: 'biểu đồ <chart> phải dựng được trong bong bóng chat',
    );
  });

  test('chat: tìm kiếm phiên lọc đúng theo tiêu đề', () async {
    final repository = ChatRepository(client);
    final found = await repository.searchSessions('không khí');
    expect(found, isNotEmpty);
    final empty = await repository.searchSessions('chuỗi-không-tồn-tại');
    expect(empty, isEmpty);
  });

  test('chat: SSE trả đủ loại sự kiện mà màn chat xử lý', () async {
    final repository = ChatRepository(client);
    final types = <String>[];
    final content = StringBuffer();

    await repository.streamMessage(
      sessionId: MockFixtures.sessionReportId,
      message: 'Kiểm tra luồng SSE',
      onEvent: (event) {
        types.add(event.type);
        if (event.type == 'content') content.write(event.value);
      },
    );

    expect(types, containsAll(['thinking', 'reasoning', 'web-search']));
    expect(types, contains('file'));
    expect(types, contains('done'));
    expect(types.last, 'done', reason: 'phải kết thúc bằng [DONE]');
    expect(content.toString(), isNotEmpty);
  });

  test('studio: agents, lịch, runs, công cụ, phê duyệt, thông báo', () async {
    final repository = StudioRepository(client);

    final agents = await repository.customAgents();
    expect(agents, isNotEmpty);
    expect(agents.first.name, isNotEmpty);

    expect(await repository.toolCatalog(), isNotEmpty);
    expect(await repository.ownedDocuments(), isNotEmpty);
    expect(await repository.schedules(), isNotEmpty);
    expect(await repository.pendingApprovals(), isNotEmpty);
    expect(await repository.notifications(), isNotEmpty);
    expect(await repository.apiKeys(), isNotEmpty);

    final runs = await repository.agentRuns();
    expect(runs, isNotEmpty);
    final detail = await repository.agentRun('run-demo-0001');
    expect(detail.goal, isNotEmpty);
    expect(detail.steps, isNotEmpty, reason: 'màn chi tiết run cần log từng bước');
  });

  test('studio: agent tự hành và nghiên cứu trả về SSE đọc được', () async {
    final repository = StudioRepository(client);

    final runTypes = <String>[];
    await repository.startAgentRun(
      goal: 'Kiểm tra luồng agent',
      onEvent: (event) => runTypes.add(event.type),
    );
    expect(runTypes, contains('done'));

    final researchTypes = <String>[];
    await repository.runResearch(
      query: 'kiểm kê khí nhà kính',
      maxSources: 4,
      onEvent: (event) => researchTypes.add(event.type),
    );
    expect(researchTypes, containsAll(['web-search', 'saved', 'done']));
  });

  test('workspace: projects, tài liệu, ghi nhớ, chỉ dẫn tuỳ biến', () async {
    final repository = WorkspaceRepository(client);

    final projects = await repository.projects();
    expect(projects, isNotEmpty);
    expect(await repository.projectSessions(projects.first.id), isNotEmpty);

    final documents = await repository.documents();
    expect(documents, isNotEmpty);
    // Màn tài liệu có ba trạng thái màu khác nhau — dữ liệu mẫu phải phủ cả ba.
    expect(
      documents.map((document) => document.vectorizationStatus).toSet(),
      containsAll(<String>['Completed', 'Failed']),
    );

    final memories = await repository.memories();
    expect(memories, isNotEmpty);
    expect(
      memories.map((memory) => memory.isConfirmed).toSet().length,
      2,
      reason: 'cần cả ghi nhớ đã xác nhận và chờ xác nhận',
    );

    final instructions = await repository.instructions();
    expect(instructions.aboutUser, isNotEmpty);
  });

  test('admin: tenant, người dùng, api key, audit, MCP, CSDL, LoRA, widget', () async {
    final repository = AdminRepository(client);

    final tenants = await repository.tenants();
    expect(tenants, isNotEmpty);
    expect(tenants.first.hasLogo, isTrue);
    expect(await repository.tenantOptions(), isNotEmpty);
    expect(await repository.mcpServers(), isNotEmpty);
    expect(await repository.mcpCatalog(), isNotEmpty);
    expect(await repository.databaseConnections(), isNotEmpty);
    expect(await repository.loraAdapters(), isNotEmpty);

    final users = await repository.users();
    expect(users.items, isNotEmpty);
    expect(users.items.any((user) => user.isLockedOut), isTrue);
    expect(users.items.any((user) => !user.isActive), isTrue);

    final audit = await repository.auditLogs();
    expect(audit.items, isNotEmpty);
    expect(audit.items.first.action, isNotEmpty);
    final facets = await repository.auditFacets();
    expect(facets.actions, isNotEmpty);

    final widget = await repository.widgetSettings();
    expect(widget.allowedOrigins, isNotEmpty);
    expect(widget.brandColor, isNotNull);

    expect(await repository.testDatabaseConnection('db-demo-0001'), isNotEmpty);
    expect(await repository.rotateWidgetKey(), isNotEmpty);
    expect(
      await repository.mcpAuthorizeUrl('mcp-demo-0001'),
      startsWith('https://'),
    );
    expect(
      (await repository.mcpOAuthStatus('mcp-demo-0001')).connected,
      isTrue,
    );
  });

  test('admin: phân trang người dùng và audit lọc đúng theo trang', () async {
    final repository = AdminRepository(client);
    final first = await repository.users(page: 1, pageSize: 2);
    expect(first.items.length, 2);
    expect(first.totalCount, greaterThan(2));

    final second = await repository.users(page: 2, pageSize: 2);
    expect(second.items, isNotEmpty);
    expect(
      second.items.map((user) => user.id),
      isNot(contains(first.items.first.id)),
      reason: 'trang 2 phải là dữ liệu khác trang 1',
    );
  });

  test('canvas: danh sách, chi tiết và lịch sử phiên bản', () async {
    final repository = CanvasRepository(client);
    final artifacts = await repository.artifacts();
    expect(artifacts, isNotEmpty);
    final detail = await repository.artifact(rootId: artifacts.first.rootId);
    expect(detail.content, isNotEmpty);
    expect(detail.version, greaterThanOrEqualTo(1));
    expect(await repository.versions(artifacts.first.rootId), isNotEmpty);
  });

  test('chia sẻ + phòng thoại: link công khai và token LiveKit', () async {
    final chat = ChatRepository(client);
    final link = await chat.createShareLink(MockFixtures.sessionReportId);
    expect(link.token, isNotEmpty);
    expect(link.isExpired, isFalse);

    // Màn chia sẻ công khai dựng Dio riêng (không qua interceptor) nên test
    // cũng phải đi đúng đường đó.
    final shared = await ShareRepository(buildDio(config)).fetch(link.token);
    expect(shared.chat, isNotNull);
    expect(shared.chat!.messages, isNotEmpty);
  });

  test('logo tenant là ảnh PNG thật, đọc đúng như `DioImage` đọc', () async {
    // Đi đúng đường mà `DioImage` đi (byte thô), không phải `client.get` mặc
    // định — nếu không thì test "xanh" trong khi ảnh vẫn không giải mã được.
    final response = await client.dio.get<List<int>>(
      MockFixtures.logoPath,
      options: Options(responseType: ResponseType.bytes),
    );

    expect(response.statusCode, 200);
    final bytes = response.data!;

    // Bốn byte đầu của PNG. Asset thiếu thì adapter trả 404 rỗng và test đỏ
    // ngay tại đây — đúng lúc, vì khi đó `TenantLogo` lặng lẽ rơi về avatar
    // chữ cái đầu và không ai biết vì sao logo biến mất.
    expect(bytes.take(4), [0x89, 0x50, 0x4E, 0x47]);
    expect(bytes.length, greaterThan(1000));
  });

  test('`DioImage` giải mã được logo thành ảnh thật (không rơi về nhãn dự phòng)', () async {
    final provider = DioImage(
      client.dio,
      '${config.apiBaseUrl}${MockFixtures.logoPath}',
    );

    final completer = Completer<ImageInfo>();
    provider
        .resolve(ImageConfiguration.empty)
        .addListener(
          ImageStreamListener(
            (info, _) => completer.complete(info),
            onError: (error, stack) => completer.completeError(error, stack),
          ),
        );

    final info = await completer.future.timeout(const Duration(seconds: 10));
    expect(info.image.width, greaterThan(0));
    expect(info.image.height, greaterThan(0));
  });

  test('lưới an toàn: không endpoint nào app gọi bị thiếu dữ liệu mẫu', () async {
    final chat = ChatRepository(client);
    final studio = StudioRepository(client);
    final workspace = WorkspaceRepository(client);
    final admin = AdminRepository(client);
    final canvas = CanvasRepository(client);
    final share = ShareRepository(buildDio(config));
    final auth = AuthRepository(config: config, store: store);

    // Đúng những lời gọi mà các màn hình trong app thực hiện.
    await auth.login(email: 'admin@cila.gov.vn', password: 'x');
    await auth.restore();

    await chat.listSessions();
    await chat.searchSessions('quan trắc');
    await chat.getMessages(MockFixtures.sessionReportId);
    final created = await chat.createSession();
    await chat.renameSession(created.id, 'Đổi tên');
    await chat.createShareLink(created.id);
    await chat.revokeShareLink(created.id);
    await chat.deleteSession(created.id);

    await studio.customAgents();
    await studio.toolCatalog();
    await studio.ownedDocuments();
    await studio.createCustomAgent(name: 'a', personaPrompt: 'b');
    await studio.updateCustomAgent(id: 'a-demo-0001', name: 'a', personaPrompt: 'b');
    await studio.deleteCustomAgent('a-demo-new');
    await studio.schedules();
    await studio.createSchedule(name: 'n', prompt: 'p', scheduleKind: 'interval');
    await studio.toggleSchedule('sch-demo-0001');
    await studio.deleteSchedule('sch-demo-new');
    await studio.agentRuns();
    await studio.agentRun('run-demo-0001');
    await studio.cancelAgentRun('run-demo-0002');
    await studio.pendingApprovals();
    await studio.approve('ap-demo-0001');
    await studio.reject('ap-demo-0001');
    await studio.apiKeys();
    await studio.createApiKey(name: 'k', expiresInDays: 30);
    await studio.revokeApiKey('key-demo-0003');
    await studio.notifications(unreadOnly: true);
    await studio.markNotificationRead('n-demo-0001');
    await studio.markAllNotificationsRead();
    await studio.analyzeText('văn bản');
    await studio.classifyText('văn bản', const ['a', 'b']);
    await studio.detectLanguage('văn bản');
    await studio.extractKeywords('văn bản');
    await studio.embeddings('văn bản');
    await studio.analyzeVision('/api/files/x', 'mô tả');
    await studio.ocrVision('/api/files/x');
    await studio.classifyVision('/api/files/x', const ['a']);
    await studio.removeVisionBackground('/api/files/x');
    await studio.createContent('chủ đề');

    await workspace.projects();
    await workspace.projectSessions(MockFixtures.sessionReportId);
    await workspace.createProject(name: 'Dự án');
    await workspace.updateProject('p-demo-0001', name: 'Dự án');
    await workspace.deleteProject('p-demo-new');
    await workspace.documents();
    await workspace.deleteDocument('doc-demo-0004');
    await workspace.memories();
    await workspace.confirmMemory('mem-demo-0002');
    await workspace.forgetMemory('mem-demo-0003');
    await workspace.instructions();
    await workspace.saveInstructions(aboutUser: 'a', responseStyle: 'b');

    await admin.tenants();
    await admin.tenantOptions();
    await admin.createTenant(name: 'Tenant mới');
    await admin.updateTenant(id: 'tenant-demo-0002', name: 'Sửa tên');
    await admin.deleteTenant('tenant-demo-0002');
    await admin.deleteTenantLogo('tenant-demo-0002');
    await admin.mcpServers();
    await admin.mcpCatalog();
    await admin.saveMcpServer(name: 'n', url: 'https://x/sse');
    await admin.deleteMcpServer('mcp-demo-0002');
    await admin.ingestKnowledge(fileName: 'f.txt', content: 'nội dung');
    await admin.queryKnowledge(query: 'câu hỏi');
    await admin.databaseConnections();
    await admin.saveDatabaseConnection(name: 'n', provider: 'PostgreSQL');
    await admin.deleteDatabaseConnection('db-demo-0002');
    await admin.reindexDatabaseConnection('db-demo-0001');
    await admin.loraAdapters();
    await admin.updateLoraAdapter(id: 'lora-demo-0001', name: 'n');
    await admin.deleteLoraAdapter('lora-demo-0002');
    await admin.assignLoraAdapter(adapterId: 'lora-demo-0001', agentId: 'a-1');
    await admin.unassignLoraAdapter('a-1');
    await admin.widgetSettings();
    await admin.updateWidgetSettings(isActive: true, allowedOrigins: const []);
    await admin.mcpOAuthStatus('mcp-demo-0001');
    await admin.disconnectMcp('mcp-demo-0001');
    await admin.users();
    await admin.createUser(email: 'a@b.c', password: 'x', fullName: 'A');
    await admin.updateUserRole('u-0002', 'Admin');
    await admin.toggleUserStatus('u-0002');
    await admin.auditLogs();
    await admin.auditFacets();

    await canvas.artifacts();
    await canvas.artifact(rootId: 'cv-demo-0001');
    await canvas.versions('cv-demo-0001');
    await canvas.create(content: 'nội dung');
    await canvas.update(rootId: 'cv-demo-0001', content: 'nội dung');
    await canvas.delete('cv-demo-0003');

    await share.fetch(MockFixtures.shareToken);

    expect(
      adapter.unmatched,
      isEmpty,
      reason:
          'Thiếu dữ liệu mẫu (hoặc sai đường dẫn) cho: '
          '${adapter.unmatched.join(', ')}',
    );
  });
}
