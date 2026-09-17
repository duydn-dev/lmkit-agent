import 'dart:async';
import 'dart:math' show max;
import 'dart:ui' show ImageByteFormat;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show rootBundle;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:lmkit_omni_mobile/app/app.dart';
import 'package:lmkit_omni_mobile/app/theme.dart';
import 'package:lmkit_omni_mobile/app/ui/app_controls.dart';
import 'package:lmkit_omni_mobile/app/ui/tenant_logo.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_models.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_provider.dart';
import 'package:lmkit_omni_mobile/core/branding_assets.dart';
import 'package:lmkit_omni_mobile/core/config/app_config.dart';
import 'package:lmkit_omni_mobile/core/config/app_config_provider.dart';
import 'package:lmkit_omni_mobile/features/admin/admin_hub_screen.dart';
import 'package:lmkit_omni_mobile/features/admin/api_keys_screen.dart';
import 'package:lmkit_omni_mobile/features/admin/audit_log_screen.dart';
import 'package:lmkit_omni_mobile/features/admin/database_connections_screen.dart';
import 'package:lmkit_omni_mobile/features/admin/knowledge_base_screen.dart';
import 'package:lmkit_omni_mobile/features/admin/lora_adapters_screen.dart';
import 'package:lmkit_omni_mobile/features/admin/mcp_servers_screen.dart';
import 'package:lmkit_omni_mobile/features/admin/tenants_screen.dart';
import 'package:lmkit_omni_mobile/features/admin/users_screen.dart';
import 'package:lmkit_omni_mobile/features/admin/widget_settings_screen.dart';
import 'package:lmkit_omni_mobile/features/auth/login_screen.dart';
import 'package:lmkit_omni_mobile/features/canvas/canvas_panel_screen.dart';
import 'package:lmkit_omni_mobile/core/mock/mock_fixtures.dart';
import 'package:lmkit_omni_mobile/core/mock/mock_http_adapter.dart';
import 'package:lmkit_omni_mobile/features/chat/chat_message_view.dart';
import 'package:lmkit_omni_mobile/features/chat/chat_navigation.dart';
import 'package:lmkit_omni_mobile/features/chat/chat_screen.dart';
import 'package:lmkit_omni_mobile/features/chat/voice_input.dart';
import 'package:lmkit_omni_mobile/features/home/home_screen.dart';
import 'package:lmkit_omni_mobile/features/more/function_menu.dart';
import 'package:lmkit_omni_mobile/features/notifications/notifications_screen.dart';
import 'package:lmkit_omni_mobile/features/share/shared_chat_screen.dart';
import 'package:lmkit_omni_mobile/features/studio/run_detail_screen.dart';
import 'package:lmkit_omni_mobile/features/studio/studio_screen.dart';
import 'package:lmkit_omni_mobile/features/studio/tools_screen.dart';
import 'package:lmkit_omni_mobile/features/workspace/custom_instructions_screen.dart';
import 'package:lmkit_omni_mobile/features/workspace/documents_screen.dart';
import 'package:lmkit_omni_mobile/features/workspace/memory_screen.dart';
import 'package:lmkit_omni_mobile/features/workspace/projects_screen.dart';

/// Bố cục ở **trạng thái đã tải xong** — vùng mù trước đây.
///
/// `layout_test.dart` dựng các màn khi không có máy chủ nên chỉ kiểm được trạng
/// thái lỗi/rỗng. Test này bật dữ liệu mẫu (`bootstrapAppConfigProvider` với
/// `useMockData: true`) nên mọi màn chạy đúng đường code đọc dữ liệu thật: danh
/// sách dài, tên dài, badge trạng thái, biểu đồ, thẻ tệp… — chính là những chỗ
/// sinh ra tràn dòng mà đọc mã nguồn không thấy.
const _narrow = Size(320, 640);
const _phone = Size(390, 844);

const _mockConfig = AppConfig(
  apiBaseUrl: 'http://localhost:5032',
  webBaseUrl: 'http://localhost:5173',
  flavor: 'test-mock',
  connectTimeout: Duration(seconds: 20),
  receiveTimeout: Duration(seconds: 300),
  sendTimeout: Duration(seconds: 120),
  logHttp: false,
  useMockData: true,
);

final _screens = <String, Widget Function()>{
  'AI Studio — Agents': () => const StudioScreen(),
  'AI Studio — Lịch': () => const StudioScreen(initialTab: 1),
  'AI Studio — Runs': () => const StudioScreen(initialTab: 2),
  'AI Studio — Research': () => const StudioScreen(initialTab: 3),
  'AI Studio — Phê duyệt': () => const StudioScreen(initialTab: 4),
  'AI Studio — Tạo nội dung': () => const StudioScreen(initialTab: 5),
  'Text Analytics': () => const ToolsScreen(),
  'Vision & OCR': () => const ToolsScreen(initialTab: 1),
  'Thông báo': () => const NotificationsScreen(),
  'Projects': () => const ProjectsScreen(),
  'Documents': () => const DocumentsScreen(),
  'Agent Memory': () => const MemoryScreen(),
  'Custom Instructions': () => const CustomInstructionsScreen(),
  'Quản trị': () => const AdminHubScreen(),
  'Người dùng': () => const UsersScreen(),
  'API Keys': () => const ApiKeysScreen(),
  'Tenant Management': () => const TenantsScreen(),
  'MCP Servers': () => const McpServersScreen(),
  'Cơ sở kiến thức': () => const KnowledgeBaseScreen(),
  'Database Connections': () => const DatabaseConnectionsScreen(),
  'LoRA Adapters': () => const LoraAdaptersScreen(),
  'Widget Settings': () => const WidgetSettingsScreen(),
  'Nhật ký kiểm toán': () => const AuditLogScreen(),
  'Đoạn chat được chia sẻ': () =>
      const SharedChatScreen(initialToken: 'demo-share-token'),
  'Chi tiết agent run': () =>
      const RunDetailScreen(runId: 'run-demo-0001', goal: 'Rà soát hợp đồng'),
  'Canvas artifact': () =>
      const CanvasPanelScreen(sessionId: 's-demo-0001'),
  'AI Chat': () => const ChatScreen(),
};

void _sizeTo(WidgetTester tester, Size size) {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
}

/// Chờ mọi request mẫu (độ trễ 80ms/request) trả về rồi mới đo bố cục.
Future<void> _drain(WidgetTester tester, {int rounds = 12}) async {
  for (var i = 0; i < rounds; i++) {
    await tester.pump(const Duration(milliseconds: 120));
  }
}

Future<void> _pumpScreen(
  WidgetTester tester,
  Widget screen, {
  Size size = _narrow,
  bool openDemoSession = false,
  String? screenKey,
  /// Phiên đăng nhập khác — dùng khi test cần một tenant **chưa có logo**
  /// riêng, để kiểm đúng nhánh mặc định (Quốc huy).
  AuthSession? session,
  /// Bộ ghi âm giả: plugin `record` cần micro thật nên không chạy trong test.
  VoiceTranscriber? recorder,
}) async {
  _sizeTo(tester, size);

  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        bootstrapAppConfigProvider.overrideWithValue(_mockConfig),
        authControllerProvider.overrideWith(() => _FakeAuth(session ?? _session)),
        if (recorder != null) voiceRecorderProvider.overrideWithValue(recorder),
        if (openDemoSession)
          pendingChatSessionProvider.overrideWith(_OpenSession.new),
      ],
      child: MaterialApp(
        theme: AppTheme.material(AppTheme.forui()),
        builder: (context, inner) =>
            FTheme(data: AppTheme.forui(), child: inner ?? const SizedBox()),
        // Đổi key theo màn: nếu không, `pumpWidget` gặp cùng loại widget ở cùng vị
        // trí sẽ **giữ nguyên State cũ** (ví dụ chuyển từ tab Agents sang tab Lịch
        // của cùng `StudioScreen` sẽ không đổi tab), và test sẽ kiểm nhầm màn.
        home: KeyedSubtree(
          key: ValueKey(screenKey ?? screen.runtimeType.toString()),
          child: screen,
        ),
      ),
    ),
  );

  await _drain(tester);
}

AuthSession _session = AuthSession(
  accessToken: 'access',
  refreshToken: 'refresh',
  accessTokenExpiresAt: DateTime(2030),
  refreshTokenExpiresAt: DateTime(2030),
  user: UserModel(
    id: 'u1',
    email: 'admin@cila.gov.vn',
    fullName: 'Nguyễn Văn Duy',
    role: 'Admin',
    tenantId: 't1',
    tenant: TenantBranding(
      name:
          'Trung tâm Thông tin lưu trữ và Thư viện tài nguyên môi trường '
          'quốc gia',
      agentName: 'Trợ lý CILA',
      logoUrl: '/api/tenant-branding/logo?v=demo',
    ),
  ),
);

class _FakeAuth extends AuthController {
  _FakeAuth(this.session);

  final AuthSession? session;

  @override
  Future<AuthSession?> build() async => session;
}

/// Bản sao phiên đăng nhập với **tenant chưa cấu hình logo**, để kiểm nhánh mặc
/// định. Không sửa [_session] dùng chung — test khác đang dựa vào logo của nó.
AuthSession _sessionWithoutLogo() => AuthSession(
  accessToken: _session.accessToken,
  refreshToken: _session.refreshToken,
  accessTokenExpiresAt: _session.accessTokenExpiresAt,
  refreshTokenExpiresAt: _session.refreshTokenExpiresAt,
  user: UserModel(
    id: _session.user.id,
    email: _session.user.email,
    fullName: _session.user.fullName,
    role: _session.user.role,
    tenantId: _session.user.tenantId,
    tenant: TenantBranding(
      name: _session.user.tenant!.name,
      agentName: _session.user.tenant!.agentName,
    ),
  ),
);

/// Quốc huy đã đóng gói — dấu hiệu nhánh logo mặc định đang chạy.
final _emblem = find.byWidgetPredicate(
  (w) => w is SvgPicture && w.bytesLoader.toString().contains('quochuy.svg'),
);

/// Nút gửi trong composer có đang bấm được không.
///
/// Đọc thẳng `onTap` của `InkWell` bên trong: nút này không dùng `IconButton`
/// (nó là nút tròn 44px như web) nên không có `onPressed` để hỏi.
bool _sendEnabled(WidgetTester tester) {
  final ink = find.descendant(
    of: find.byTooltip('Gửi tin nhắn'),
    matching: find.byType(InkWell),
  );
  expect(ink, findsOneWidget, reason: 'không thấy nút gửi');
  return tester.widget<InkWell>(ink).onTap != null;
}

/// Chiều cao các cột sóng đang vẽ trong khối ghi âm.
///
/// Nhận diện theo đúng bề ngang đã khai (3px): không widget nào khác trên màn
/// mảnh đến vậy, nên phép đo không phụ thuộc thứ tự cây.
List<double> _barHeights(WidgetTester tester) => find
    .byType(Container)
    .evaluate()
    .map((element) => tester.getSize(find.byWidget(element.widget)))
    .where((size) => size.width == 3)
    .map((size) => size.height)
    .toList();

/// Bộ ghi âm giả: plugin `record` cần micro thật nên không chạy trong test.
class _FakeRecorder implements VoiceTranscriber {
  /// Độ trễ khi phiên âm, để kịp nhìn thấy trạng thái "đang phiên âm".
  static const transcribeDelay = Duration(milliseconds: 500);

  final _levels = StreamController<double>.broadcast();

  int cancels = 0;
  int stops = 0;

  /// Bơm một mức âm lượng như micro vừa nghe được (0..1).
  void emitLevel(double level) => _levels.add(level);

  void dispose() => _levels.close();

  @override
  Future<bool> hasPermission() async => true;

  @override
  Future<void> start() async {}

  @override
  Future<String> stopAndTranscribe() async {
    stops++;
    await Future<void>.delayed(transcribeDelay);
    return 'nội dung phiên âm mẫu';
  }

  @override
  Future<void> cancel() async => cancels++;

  @override
  Stream<double> levels() => _levels.stream;
}

/// Mở sẵn một phiên chat như khi bấm từ màn Projects.
class _OpenSession extends PendingChatSession {
  @override
  String? build() => MockFixtures.sessionReportId;
}

void main() {
  // Bắt buộc: không có nó, `FlutterSecureStorage` treo ở lời gọi plugin nên
  // `AuthInterceptor` không bao giờ trả về và **mọi màn ở lại vòng xoay** —
  // test sẽ "xanh" mà thực chất chưa hề kiểm gì.
  setUp(() => FlutterSecureStorage.setMockInitialValues({}));

  group('trạng thái đã tải xong (dữ liệu mẫu)', () {
    testWidgets('mọi màn hiển thị dữ liệu thật mà không tràn ở 320dp', (
      tester,
    ) async {
      for (final entry in _screens.entries) {
        await _pumpScreen(tester, entry.value(), screenKey: entry.key);
        expect(
          tester.takeException(),
          isNull,
          reason: 'màn "${entry.key}" tràn khi đã có dữ liệu (320dp)',
        );
      }
    });

    testWidgets('mọi màn giữ được bố cục ở khổ máy thật (390dp)', (
      tester,
    ) async {
      for (final entry in _screens.entries) {
        await _pumpScreen(
          tester,
          entry.value(),
          size: _phone,
          screenKey: entry.key,
        );
        expect(
          tester.takeException(),
          isNull,
          reason: 'màn "${entry.key}" tràn khi đã có dữ liệu (390dp)',
        );
      }
    });

    testWidgets('mọi màn chịu được cỡ chữ hệ thống lớn (1.3×)', (tester) async {
      tester.platformDispatcher.textScaleFactorTestValue = 1.3;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);

      for (final entry in _screens.entries) {
        await _pumpScreen(tester, entry.value(), screenKey: entry.key);
        expect(
          tester.takeException(),
          isNull,
          reason: 'màn "${entry.key}" tràn ở cỡ chữ 1.3×',
        );
      }
    });
  });

  // Ảnh nghiệp vụ từng lọt ra ngoài lớp mạng: `Image.network` dựng HTTP client
  // riềng nên không đi qua `Dio`, và ở chế độ dữ liệu mẫu nó vẫn gọi ra
  // `http://localhost:5032/api/tenant-branding/logo` rồi thất bại
  // (`ERR_CONNECTION_REFUSED` — bắt được trong console của bản web). Nhóm test
  // này khoá lại đúng chỗ đó: request **phải** đi qua adapter dữ liệu mẫu.
  group('ảnh nghiệp vụ đi qua lớp mạng của app', () {
    testWidgets('logo đơn vị được adapter dữ liệu mẫu phục vụ', (tester) async {
      _sizeTo(tester, _phone);

      final container = ProviderContainer(
        overrides: [
          bootstrapAppConfigProvider.overrideWithValue(_mockConfig),
          authControllerProvider.overrideWith(() => _FakeAuth(_session)),
        ],
      );
      addTearDown(container.dispose);

      final adapter =
          container.read(apiClientProvider).dio.httpClientAdapter
              as MockHttpAdapter;

      // Ghi phiên vào đúng nơi `AuthInterceptor` đọc (`secureStorage`), không
      // chỉ giả `authControllerProvider`: nếu không, interceptor thấy trống và
      // test sẽ không kiểm được chuyện Bearer token có được gắn hay không.
      await container.read(sessionStoreProvider).write(_session);

      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: container,
          child: MaterialApp(
            theme: AppTheme.material(AppTheme.forui()),
            builder: (context, inner) =>
                FTheme(data: AppTheme.forui(), child: inner ?? const SizedBox()),
            home: const Center(
              child: TenantLogo(
                path: MockFixtures.logoPath,
                size: 40,
              ),
            ),
          ),
        ),
      );
      await _drain(tester);

      final logoRequests = adapter.requests.where(
        (request) => request.uri.path == '/api/tenant-branding/logo',
      );
      expect(
        logoRequests,
        isNotEmpty,
        reason:
            'Logo không đi qua Dio (nhiều khả năng ai đó đổi lại thành '
            'Image.network): ở chế độ dữ liệu mẫu nó sẽ gọi thẳng ra máy chủ thật.',
      );
      expect(
        logoRequests.first.headers['Authorization'],
        startsWith('Bearer '),
        reason: 'Endpoint logo nằm sau [Authorize] nên thiếu token là 404 ngay',
      );
      expect(tester.takeException(), isNull);
    });
  });

  group('app chạy bằng dữ liệu mẫu', () {
    testWidgets('mở tới tab chat ngay sau khi đăng nhập', (tester) async {
      _sizeTo(tester, _narrow);

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            bootstrapAppConfigProvider.overrideWithValue(_mockConfig),
            authControllerProvider.overrideWith(
              () => _FakeAuth(_session),
            ),
          ],
          child: const LmKitOmniApp(),
        ),
      );
      await _drain(tester);

      expect(find.byType(HomeScreen), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('menu chức năng mở từ header và đẩy được sang màn con', (
      tester,
    ) async {
      _sizeTo(tester, _narrow);

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            bootstrapAppConfigProvider.overrideWithValue(_mockConfig),
            authControllerProvider.overrideWith(() => _FakeAuth(_session)),
          ],
          child: const LmKitOmniApp(),
        ),
      );
      await _drain(tester);

      // Không còn thanh điều hướng dưới: mọi mục cấp một đi qua nút ba chấm.
      expect(find.byType(NavigationBar), findsNothing);

      await tester.tap(find.byTooltip('Danh sách chức năng'));
      await _drain(tester, rounds: 6);
      expect(find.byType(FunctionMenuSheet), findsOneWidget);

      // Mở một màn con từ menu: sheet phải đóng lại, nếu không lúc quay về
      // người dùng gặp lại đúng danh sách vừa chọn.
      final item = find.text('HITL Approvals');
      await tester.scrollUntilVisible(
        item,
        220,
        scrollable: find.descendant(
          of: find.byType(FunctionMenuSheet),
          matching: find.byType(Scrollable),
        ),
      );
      await tester.tap(item);
      await _drain(tester, rounds: 10);

      expect(find.byType(StudioScreen), findsOneWidget);
      expect(find.byType(FunctionMenuSheet), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets('màn đăng nhập ở chế độ dữ liệu mẫu nói rõ đang là dữ liệu giả', (
      tester,
    ) async {
      await _pumpScreen(tester, const LoginScreen());

      expect(find.textContaining('dữ liệu mẫu'), findsWidgets);
      expect(tester.takeException(), isNull);
    });
  });

  /// Test trên chỉ có giá trị nếu các màn **thật sự** hiển thị dữ liệu mẫu —
  /// nếu không nó lại đang kiểm trạng thái rỗng như bộ test cũ.
  group('dữ liệu mẫu thực sự lên màn hình', () {
    const probes = <String, (Widget, String)>{
      'Thông báo': (
        NotificationsScreen(),
        'Tài liệu đã xử lý xong',
      ),
      'Người dùng': (UsersScreen(), 'lan.pham@cila.gov.vn'),
      'API Keys': (ApiKeysScreen(), 'Tích hợp cổng dịch vụ công'),
      'Tenant Management': (
        TenantsScreen(),
        'Trung tâm Thông tin lưu trữ và Thư viện tài nguyên môi trường quốc gia',
      ),
      'Projects': (ProjectsScreen(), 'Báo cáo tổng kết quan trắc 2026'),
      'Documents': (DocumentsScreen(), 'Luật Bảo vệ môi trường 2020.pdf'),
      'Agent Memory': (MemoryScreen(), 'Định dạng báo cáo ưa thích'),
      'Nhật ký kiểm toán': (AuditLogScreen(), 'User • Session'),
      'Cơ sở kiến thức': (KnowledgeBaseScreen(), 'Nạp tài liệu'),
      'MCP Servers': (McpServersScreen(), 'Kho dữ liệu quốc gia'),
      'Database Connections': (
        DatabaseConnectionsScreen(),
        'CSDL nghiệp vụ quan trắc',
      ),
      'AI Studio — Agents': (StudioScreen(), 'Chuyên gia pháp chế'),
      'AI Studio — Lịch': (
        StudioScreen(initialTab: 1),
        'Báo cáo không khí hằng ngày',
      ),
      'AI Studio — Runs': (
        StudioScreen(initialTab: 2),
        'Rà soát toàn bộ hợp đồng quan trắc quý III và nêu rủi ro.',
      ),
    };

    testWidgets('mọi danh sách then chốt hiện đúng nội dung mẫu', (tester) async {
      for (final entry in probes.entries) {
        await _pumpScreen(
          tester,
          entry.value.$1,
          screenKey: entry.key,
          openDemoSession: entry.key == 'AI Chat',
        );
        expect(
          find.textContaining(entry.value.$2),
          findsWidgets,
          reason: 'màn "${entry.key}" không hiển thị dữ liệu mẫu',
        );
      }
    });

    /// Hộp thoại Forui (`FDialog`) **không có tổ tiên `Material`**, nên widget nào
    /// cần `Material` (`TextField`, `DropdownButtonFormField`…) đặt trong đó sẽ
    /// ném "No Material widget found" và cả hộp thoại trắng xoá — đúng lỗi đã gặp
    /// ở hộp thoại "Tạo lịch tự động" của AI Studio.
    ///
    /// Test này mở hộp thoại đó và kiểm hai điều: dựng lên không ném ngoại lệ, và
    /// các ô nhập là của hệ thiết kế (Forui) chứ không phải Material.
    testWidgets('hộp thoại tạo lịch tự động: dựng được trong hộp thoại Forui', (
      tester,
    ) async {
      await _pumpScreen(
        tester,
        const StudioScreen(initialTab: 1),
        screenKey: 'studio-schedule-dialog',
      );

      // Nút "thêm mới" ở tiêu đề tab Lịch — nút icon của hệ thiết kế đặt
      // `semanticsLabel` chứ không phải `Tooltip` của Material, nên tìm theo icon.
      await tester.tap(find.byIcon(Icons.add).first);
      await _drain(tester, rounds: 6);

      expect(
        tester.takeException(),
        isNull,
        reason: 'hộp thoại "Tạo lịch tự động" ném lỗi khi dựng',
      );
      expect(find.text('Tạo lịch tự động'), findsOneWidget);
      expect(
        find.byType(FTextField),
        findsNWidgets(3),
        reason: 'ba ô nhập phải là ô của hệ thiết kế (Forui), không phải Material',
      );
      expect(find.text('Loại lịch'), findsOneWidget);

      // Ô chọn loại lịch: mở được và giá trị vừa chọn hiện lại trong hộp thoại
      // (trước đây ô này bị bỏ qua, luôn gửi `interval` khi tạo lịch).
      await tester.tap(find.text('Loại lịch'));
      await _drain(tester, rounds: 6);
      await tester.tap(find.text('Chạy một lần'));
      await _drain(tester, rounds: 6);
      expect(tester.takeException(), isNull);
      expect(
        find.text('Chạy một lần'),
        findsWidgets,
        reason: 'chọn "Chạy một lần" xong hộp thoại không cập nhật',
      );
    });

    /// Cùng một lớp lỗi với hộp thoại trên, nhưng quét **mọi hộp thoại form**:
    /// hộp thoại Forui không có tổ tiên `Material`, nên chỉ cần sót lại một
    /// `DropdownButtonFormField` hoặc `TextField` của Material là cả hộp thoại
    /// trắng xoá khi mở. Mỗi màn được mở thật trong chế độ dữ liệu mẫu.
    ///
    /// Mỗi hộp thoại một `testWidgets` riêng: pump nhiều màn trong cùng một test
    /// sẽ sinh lỗi harness (GlobalKey trùng, build sai scope) chứ không phải lỗi
    /// của app.
    final formDialogs = <String, (Widget Function(), IconData)>{
      'AI Studio — Lịch': (() => const StudioScreen(initialTab: 1), Icons.add),
      'Users': (() => const UsersScreen(), Icons.person_add_alt),
      'MCP Servers': (() => const McpServersScreen(), Icons.add),
      'Database Connections': (
        () => const DatabaseConnectionsScreen(),
        Icons.add,
      ),
      'Projects': (() => const ProjectsScreen(), Icons.add),
    };

    for (final entry in formDialogs.entries) {
      testWidgets('hộp thoại form của "${entry.key}" mở được', (tester) async {
        await _pumpScreen(
          tester,
          entry.value.$1(),
          screenKey: 'form-dialog-${entry.key}',
        );

        expect(tester.takeException(), isNull, reason: 'màn nền đã có lỗi');

        await tester.tap(find.byIcon(entry.value.$2).first);
        await _drain(tester, rounds: 6);

        expect(
          tester.takeException(),
          isNull,
          reason: 'hộp thoại form của "${entry.key}" ném lỗi khi dựng',
        );
        expect(find.byType(AppDialog), findsOneWidget);
        // Hộp thoại form nào ở đây cũng có ô nhập, và phải là ô của hệ thiết kế
        // (Forui) — quay lại dùng `TextField`/`DropdownButtonFormField` của
        // Material là hộp thoại trắng xoá như đã gặp, và dòng `takeException` ở
        // trên sẽ bắt được.
        expect(
          find.descendant(
            of: find.byType(AppDialog),
            matching: find.byType(FTextField),
          ),
          findsWidgets,
          reason: 'hộp thoại "${entry.key}" không có ô nhập của hệ thiết kế',
        );
      });
    }

    /// Form agent của AI Studio nằm trong **sheet Forui** (`showAppSheet`) chứ
    /// không phải hộp thoại — cũng cùng lớp lỗi "không tra được `Material`" nên
    /// phải kiểm riêng.
    testWidgets('form agent (sheet) mở được, không còn ô nhập Material', (
      tester,
    ) async {
      await _pumpScreen(
        tester,
        const StudioScreen(),
        screenKey: 'agent-form-sheet',
      );

      await tester.tap(find.byIcon(Icons.add).first);
      await _drain(tester, rounds: 6);

      expect(
        tester.takeException(),
        isNull,
        reason: 'form agent ném lỗi khi dựng',
      );
      expect(find.text('Tên'), findsOneWidget);
      expect(find.byType(FTextField), findsWidgets);
    });

    testWidgets('composer: nút gửi bật khi có chữ, gửi là có câu trả lời mẫu', (
      tester,
    ) async {
      await _pumpScreen(
        tester,
        const ChatScreen(),
        size: _phone,
        screenKey: 'chat-composer',
      );

      // Chưa gõ gì và chưa đính kèm file thì nút gửi **khoá** (đúng `:disabled`
      // của web). Nếu ai đó bỏ điều kiện này, người dùng sẽ bấm một nút trông
      // như bấm được mà không có gì xảy ra.
      expect(_sendEnabled(tester), isFalse, reason: 'nút gửi phải khoá khi trống');

      await tester.enterText(
        find.byType(TextField),
        'Tóm tắt nhanh tài liệu mẫu',
      );
      await tester.pump();
      expect(_sendEnabled(tester), isTrue, reason: 'có chữ mà nút gửi vẫn khoá');

      await tester.tap(find.byTooltip('Gửi tin nhắn'));
      await _drain(tester, rounds: 10);

      // Trong lúc chờ câu trả lời, đúng một nút phải là "dừng".
      expect(find.byTooltip('Dừng tạo trả lời'), findsOneWidget);

      // Tin nhắn của người dùng lên màn và luồng SSE mẫu trả về bong bóng trả lời.
      expect(find.textContaining('Tóm tắt nhanh tài liệu mẫu'), findsWidgets);
      expect(find.byType(ChatMessageView), findsWidgets);
      expect(tester.takeException(), isNull);

      // Kết thúc lượt bằng nút dừng rồi chờ nốt các timer của luồng mẫu.
      //
      // Không chờ `[DONE]` ở đây: việc huỷ subscription của `ResponseBody` chỉ
      // hoàn tất khi có event loop thật, mà `testWidgets` chạy bằng đồng hồ giả
      // — `await` luồng SSE mẫu sẽ treo vô hạn và test đỏ vì lý do của harness,
      // không phải của app. Đường đi thật (Android, web) đã được kiểm ở
      // `mock_adapter_test` bằng async thật.
      await tester.tap(find.byTooltip('Dừng tạo trả lời'));
      await _drain(tester, rounds: 20);
      expect(
        find.byTooltip('Gửi tin nhắn'),
        findsOneWidget,
        reason: 'dừng tạo trả lời xong mà nút gửi không trở lại',
      );
      // Ô soạn tin đã được xoá sau khi gửi nên nút gửi lại khoá — đúng như web.
      expect(_sendEnabled(tester), isFalse);
    });

    /// Logo là **cấu hình theo từng tenant**: đơn vị tự tải logo riêng lên, và
    /// mặc định — khi chưa cấu hình — là **Quốc huy**.
    ///
    /// Hai nhánh này nằm ở hai `testWidgets` riêng chứ không phải hai lượt
    /// `pumpWidget` trong cùng một test: `ProviderScope` ở cùng vị trí trong cây
    /// **giữ nguyên State** giữa hai lượt pump, nên phiên đăng nhập của lượt sau
    /// không được áp và lượt "chưa có logo" thực ra vẫn đọc logo của lượt trước
    /// — test đỏ vì harness, không vì app.
    testWidgets('logo đơn vị: tenant có logo riêng thì dùng logo đó', (
      tester,
    ) async {
      await _pumpScreen(
        tester,
        const ChatScreen(),
        size: _phone,
        screenKey: 'logo-tenant',
      );
      expect(find.text('Hôm nay tôi có thể giúp gì cho bạn?'), findsOneWidget);
      expect(
        find.descendant(
          of: find.byType(TenantLogo),
          matching: find.byType(Image),
        ),
        findsWidgets,
        reason: 'tenant có logo riêng mà không dùng logo đó',
      );
      expect(_emblem, findsNothing, reason: 'có logo riêng thì không dùng Quốc huy');
      expect(tester.takeException(), isNull);
    });

    testWidgets('logo đơn vị: chưa cấu hình logo thì mặc định là Quốc huy', (
      tester,
    ) async {
      await _pumpScreen(
        tester,
        const ChatScreen(),
        size: _narrow,
        screenKey: 'logo-emblem',
        session: _sessionWithoutLogo(),
      );
      expect(_emblem, findsWidgets, reason: 'thiếu logo thì phải rơi về Quốc huy');
      // Không được rơi về avatar chữ cái hay ô trống.
      expect(
        find.descendant(
          of: find.byType(TenantLogo),
          matching: find.byType(Image),
        ),
        findsNothing,
        reason: 'tenant chưa có logo mà vẫn dựng đường ảnh',
      );
      expect(tester.takeException(), isNull);
    });

    /// Danh sách đơn vị là chỗ duy nhất thấy **cả hai nhánh logo cùng lúc**:
    /// đơn vị đã tải logo riêng (`hasLogo`) đi đường ảnh có Bearer, đơn vị chưa
    /// cấu hình rơi về Quốc huy. Cấu hình logo là **theo từng tenant**, nên một
    /// bên đổi không được kéo bên kia theo.
    testWidgets('Tenant Management: logo theo từng tenant, chưa có thì Quốc huy', (
      tester,
    ) async {
      await _pumpScreen(
        tester,
        const TenantsScreen(),
        size: _phone,
        screenKey: 'tenants-logo',
      );

      expect(
        find.text('Sở Tài nguyên và Môi trường tỉnh Bắc Ninh'),
        findsOneWidget,
      );
      expect(
        find.descendant(
          of: find.byType(TenantLogo),
          matching: find.byType(Image),
        ),
        findsOneWidget,
        reason: 'đơn vị đã có logo mà không dựng ảnh',
      );
      expect(
        _emblem,
        findsOneWidget,
        reason: 'đơn vị chưa cấu hình logo mà không rơi về Quốc huy',
      );
      expect(tester.takeException(), isNull);
    });

    /// Quản trị viên cấu hình logo **ngay trong hộp thoại sửa tenant**, và nhãn
    /// phải nói rõ để trống là dùng Quốc huy — nếu không, người dùng tưởng đơn vị
    /// đang thiếu cấu hình.
    testWidgets('hộp thoại sửa tenant có khối Logo đơn vị và nêu mặc định', (
      tester,
    ) async {
      await _pumpScreen(
        tester,
        const TenantsScreen(),
        size: _phone,
        screenKey: 'tenants-logo-dialog',
      );

      // Menu dòng của màn quản trị là `AppMenuButton` của hệ thiết kế (Forui),
      // không còn `PopupMenuButton<String>` của Material nữa. Nút này gắn nhãn
      // qua `semanticsLabel` chứ không phải `Tooltip`, nên tìm theo icon.
      await tester.tap(find.byIcon(Icons.more_vert).first);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Sửa (tên, logo)'));
      await tester.pumpAndSettle();

      expect(find.text('Logo đơn vị'), findsOneWidget);
      // Đơn vị đầu tiên trong dữ liệu mẫu đã có logo riêng.
      expect(
        find.text('Logo riêng của đơn vị đang được dùng.'),
        findsOneWidget,
      );
      expect(find.text('Tải logo'), findsOneWidget);
      expect(find.text('Gỡ'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    /// Ảnh logo mà chế độ dữ liệu mẫu phục vụ phải là **Quốc huy thật**, không
    /// phải một ô màu trang trí: đây là dấu nhận diện của đơn vị nhà nước, và cả
    /// hai bản (vector để vẽ, raster để phục vụ qua HTTP) đều phải đọc được.
    testWidgets('asset Quốc huy: bản vector và bản raster đều dùng được', (
      tester,
    ) async {
      final svg = await rootBundle.load(BrandingAssets.emblemSvg);
      expect(svg.lengthInBytes, greaterThan(10000));

      // Giải mã ảnh cần **event loop thật**: ở đồng hồ giả của `testWidgets`,
      // callback của codec không bao giờ chạy và test treo tới hết giờ (cùng lý
      // do đã gặp với luồng SSE mẫu), nên phải bọc trong `runAsync`.
      final raster = await tester.runAsync(() async {
        final png = await rootBundle.load(BrandingAssets.emblemPng);
        final image = await decodeImageFromList(png.buffer.asUint8List());
        final pixels = await image.toByteData(format: ImageByteFormat.rawRgba);
        return (image, pixels!);
      });
      final (image, pixels) = raster!;
      expect(
        image.width,
        greaterThanOrEqualTo(128),
        reason: 'Quốc huy raster quá nhỏ để làm logo',
      );
      expect(image.height, image.width, reason: 'Quốc huy phải là ảnh vuông');

      // Trắng một màu, hay một ô xanh trang trí, cũng giải mã được và cũng đúng
      // kích thước — nên phải soi **nội dung**: Quốc huy là nền đỏ với ngôi sao
      // vàng, tức phần lớn điểm ảnh đục phải là tông đỏ.
      final bytes = pixels.buffer.asUint8List();
      var opaque = 0;
      var red = 0;
      for (var i = 0; i < bytes.length; i += 4) {
        if (bytes[i + 3] < 40) continue;
        opaque++;
        if (bytes[i] > 120 &&
            bytes[i] > bytes[i + 1] + 40 &&
            bytes[i] > bytes[i + 2] + 40) {
          red++;
        }
      }
      expect(opaque, greaterThan(0), reason: 'Quốc huy raster trong suốt hoàn toàn');
      expect(
        red / opaque,
        greaterThan(0.3),
        reason: 'ảnh raster không phải Quốc huy (thiếu mảng đỏ đặc trưng)',
      );
    });

    /// Tên đơn vị nhà nước dài 60–80 ký tự và khác nhau ở **cuối** câu, nên cắt
    /// bằng `…` là cắt đúng phần phân biệt đơn vị này với đơn vị khác.
    testWidgets('ngăn kéo: tên đơn vị hiện đầy đủ, không cắt bằng "…"', (
      tester,
    ) async {
      await _pumpScreen(
        tester,
        const ChatScreen(),
        size: _narrow,
        screenKey: 'drawer-full-name',
      );
      await tester.tap(find.byTooltip('Lịch sử chat'));
      await tester.pumpAndSettle();

      final longName = _session.user.tenant!.name;
      final nameText = find.descendant(
        of: find.byType(Drawer),
        matching: find.text(longName),
      );
      expect(nameText, findsOneWidget, reason: 'ngăn kéo phải có tên đơn vị');

      // `Text.data` giữ nguyên chuỗi kể cả khi bị cắt, nên phải kiểm đúng hai
      // thuộc tính gây ra cắt: giới hạn dòng và kiểu tràn.
      final text = tester.widget<Text>(nameText);
      expect(text.maxLines, isNull, reason: 'tên bị giới hạn dòng');
      expect(
        text.overflow,
        isNot(TextOverflow.ellipsis),
        reason: 'tên đơn vị vẫn bị cắt bằng "…"',
      );
      // Và phải **thật sự** xuống dòng: ở 320dp, tên dài 66 ký tự không thể nằm
      // gọn trong một dòng — nếu ai đó thêm lại giới hạn dòng, chiều cao tụt về
      // một dòng và phép đo này đỏ.
      expect(
        tester.getSize(nameText).height,
        greaterThan(40),
        reason: 'tên đơn vị chỉ chiếm một dòng — tức đã bị cắt',
      );

      // Tên trợ lý in **nguyên văn** tên đơn vị đặt, không ghép thêm tiền tố:
      // dữ liệu mẫu đặt "Trợ lý CILA", nên bản cũ cho ra "Trợ lý: Trợ lý CILA".
      expect(
        find.descendant(
          of: find.byType(Drawer),
          matching: find.text(_session.user.agentName),
        ),
        findsOneWidget,
        reason: 'dòng trợ lý không khớp tên trợ lý của đơn vị',
      );
      expect(
        find.textContaining('Trợ lý: '),
        findsNothing,
        reason: 'tên trợ lý bị ghép thêm tiền tố "Trợ lý: "',
      );
    });

    /// Lúc ghi âm, khối soạn tin phải **thu lại còn một hàng**: chấm đỏ · thời
    /// lượng · đồng hồ sóng · Huỷ · Phiên âm — thay cho cả ô nhập lẫn hàng công
    /// cụ.
    ///
    /// Đây là điểm từng làm người dùng bấm nhầm: trước đây ghi âm chỉ là một
    /// dòng chữ nhỏ dưới ô nhập, nên họ tưởng nút không ăn rồi bấm sang nút micro
    /// của **bàn phím hệ thống** (Google) và nhận một màn "Tap to speak" xa lạ.
    testWidgets('ghi âm: khối soạn tin thu còn một hàng, có đồng hồ và sóng', (
      tester,
    ) async {
      final recorder = _FakeRecorder();
      addTearDown(recorder.dispose);

      await _pumpScreen(
        tester,
        const ChatScreen(),
        size: _phone,
        screenKey: 'voice-strip',
        recorder: recorder,
      );

      await tester.tap(find.byTooltip('Nhập bằng giọng nói'));
      await _drain(tester, rounds: 6);

      // Ô nhập và hàng công cụ biến mất — không còn chỗ để gõ tay trong lúc
      // micro đang mở, và khối không cao thêm.
      expect(find.byType(TextField), findsNothing);
      expect(find.byTooltip('Đính kèm tài liệu'), findsNothing);
      expect(find.byTooltip('Huỷ bản ghi'), findsOneWidget);
      expect(find.byTooltip('Phiên âm bản ghi'), findsOneWidget);

      // Đồng hồ chạy theo thời gian thực.
      expect(find.text('0:00'), findsOneWidget);
      await tester.pump(const Duration(seconds: 1));
      expect(find.text('0:01'), findsOneWidget);

      // Đồng hồ sóng phản ứng với âm lượng: bơm một mức to rồi đo cột cao nhất.
      final quiet = _barHeights(tester);
      expect(quiet, isNotEmpty, reason: 'không vẽ được cột sóng nào');
      expect(quiet.reduce(max), lessThanOrEqualTo(6));

      recorder.emitLevel(1);
      await _drain(tester, rounds: 2);
      expect(
        _barHeights(tester).reduce(max),
        greaterThan(20),
        reason: 'cột sóng không nhúc nhích theo tiếng nói',
      );

      // Huỷ thì quay lại đúng khối soạn tin bình thường.
      await tester.tap(find.byTooltip('Huỷ bản ghi'));
      await _drain(tester, rounds: 4);
      expect(find.byType(TextField), findsOneWidget);
      expect(find.byTooltip('Nhập bằng giọng nói'), findsOneWidget);
      expect(recorder.cancels, 1);
    });

    testWidgets('ghi âm: Xong thì hiện trạng thái đang phiên âm', (
      tester,
    ) async {
      final recorder = _FakeRecorder();
      addTearDown(recorder.dispose);

      await _pumpScreen(
        tester,
        const ChatScreen(),
        size: _phone,
        screenKey: 'voice-transcribe',
        recorder: recorder,
      );

      await tester.tap(find.byTooltip('Nhập bằng giọng nói'));
      await _drain(tester, rounds: 6);
      await tester.tap(find.byTooltip('Phiên âm bản ghi'));
      await _drain(tester, rounds: 2);

      expect(find.text('Đang phiên âm...'), findsOneWidget);
      expect(find.byTooltip('Huỷ bản ghi'), findsOneWidget);
      expect(recorder.stops, 1);

      // Phiên âm xong thì trả lại khối soạn tin, và văn bản nằm sẵn trong ô.
      await _drain(tester, rounds: 10);
      expect(find.byType(TextField), findsOneWidget);
      expect(find.text('Đang phiên âm...'), findsNothing);
      expect(
        tester.widget<TextField>(find.byType(TextField)).controller?.text,
        isNotEmpty,
        reason: 'văn bản phiên âm không được đưa vào ô soạn tin',
      );
    });

    /// Nút micro là **cách nhập liệu**, không phải một chế độ của cuộc trò
    /// chuyện: nó phải nằm trong hàng ô nhập, không lẫn xuống hàng công cụ.
    /// Test này đo vị trí thật của hai nút, không đọc mã — chuyển nút về hàng
    /// dưới là đỏ ngay.
    testWidgets('composer: micro nằm trong hàng ô nhập, không ở hàng công cụ', (
      tester,
    ) async {
      await _pumpScreen(
        tester,
        const ChatScreen(),
        size: _phone,
        screenKey: 'chat-mic-in-field',
      );

      final field = tester.getRect(find.byType(TextField));
      final mic = tester.getRect(find.byTooltip('Nhập bằng giọng nói'));
      final send = tester.getRect(find.byTooltip('Gửi tin nhắn'));

      // Cùng hàng ô nhập: hai hình chữ nhật phải giao nhau theo chiều dọc.
      expect(
        mic.bottom > field.top && mic.top < field.bottom,
        isTrue,
        reason: 'micro và ô nhập không còn nằm cùng hàng (mic=$mic, ô=$field)',
      );
      // Và phải nằm **trên** hàng công cụ — nút gửi là mốc của hàng đó.
      expect(
        mic.bottom,
        lessThanOrEqualTo(send.top),
        reason: 'micro vẫn nằm ở hàng công cụ bên dưới (mic=$mic, gửi=$send)',
      );
      // Hai nút tròn nằm cùng cột: lệch tâm nhìn ra ngay trên màn hẹp.
      expect(
        (mic.center.dx - send.center.dx).abs(),
        lessThanOrEqualTo(2),
        reason: 'tâm micro và tâm nút gửi lệch nhau (mic=$mic, gửi=$send)',
      );
      // Nút micro không đẩy nội dung tràn: mép phải của nó vẫn trong màn.
      expect(mic.right, lessThan(_phone.width));
      expect(tester.takeException(), isNull);
    });

    testWidgets('mở phiên chat: tin nhắn hiện ra ngay, không bị cuộn quá đáy', (
      tester,
    ) async {
      await _pumpScreen(
        tester,
        const ChatScreen(),
        size: _phone,
        openDemoSession: true,
        screenKey: 'chat-with-session',
      );

      // Hồi quy: đích cuộn cũ được tính ở một frame mà nội dung còn dài hơn, nên
      // `animateTo` dừng NGOÀI đáy và cả khung chat trống trơn dù đã tải xong.
      final position = tester.state<ScrollableState>(
        find.byType(Scrollable).first,
      ).position;
      expect(
        position.pixels,
        lessThanOrEqualTo(position.maxScrollExtent),
        reason: 'vị trí cuộn vượt quá đáy → khung chat trống',
      );
      expect(
        find.byType(ChatMessageView),
        findsWidgets,
        reason: 'mở phiên xong mà không thấy bong bóng tin nhắn nào',
      );

      // Cuộn lên đầu cuộc trò chuyện để kiểm cả câu trả lời dài: biểu đồ, nguồn
      // và thẻ tệp do agent tạo.
      await tester.drag(find.byType(ListView), const Offset(0, 3000));
      await _drain(tester, rounds: 4);

      expect(
        find.textContaining('PM2.5', findRichText: true),
        findsWidgets,
        reason: 'câu trả lời dài (kèm biểu đồ) không dựng được',
      );
      expect(
        find.textContaining('bao-cao-khong-khi-08-2026.md'),
        findsWidgets,
        reason: 'mất thẻ tệp do agent tạo trong bong bóng trả lời',
      );
      expect(tester.takeException(), isNull);
    });
  });
}
