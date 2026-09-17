import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:lmkit_omni_mobile/app/app.dart';
import 'package:lmkit_omni_mobile/app/theme.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_models.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_provider.dart';
import 'package:lmkit_omni_mobile/features/admin/admin_hub_screen.dart';
import 'package:lmkit_omni_mobile/features/admin/admin_widgets.dart';
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
import 'package:lmkit_omni_mobile/features/chat/chat_message_view.dart';
import 'package:lmkit_omni_mobile/features/chat/chat_models.dart';
import 'package:lmkit_omni_mobile/features/chat/chat_screen.dart';
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

/// Hẹp hơn mọi điện thoại phổ thông (320dp) — nơi lỗi tràn dòng, chồng chữ và
/// căn lệch lộ ra rõ nhất.
const _narrow = Size(320, 640);
const _phone = Size(390, 844);

/// Mọi màn trong app, dựng TRỰC TIẾP thay vì bấm qua điều hướng.
///
/// Bấm mô phỏng ở khổ 320dp không đáng tin — mục cuộn sát đáy hay bị thanh điều
/// hướng dưới cùng chặn, khiến test "xanh" mà thực ra chưa mở màn nào. Dựng
/// thẳng màn cần kiểm vừa chắc chắn vừa đúng thứ cần đo: bố cục của chính màn đó.
/// Không có máy chủ trong test nên các màn hiện trạng thái lỗi/rỗng — bản thân
/// những trạng thái đó cũng phải gọn gàng ở màn hẹp.
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
  'Đoạn chat được chia sẻ': () => const SharedChatScreen(),
  'Chi tiết agent run': () =>
      const RunDetailScreen(runId: 'run-1', goal: 'Tổng hợp báo cáo tuần'),
  'Canvas artifact': () => const CanvasPanelScreen(sessionId: 'session-1'),
  'AI Chat': () => const ChatScreen(),
};

void _sizeTo(WidgetTester tester, Size size) {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
}

/// Dựng một màn lẻ trong đúng theme và phiên đăng nhập mà app dùng.
///
/// [screenKey] đổi khoá của màn: `pumpWidget` gặp cùng loại widget ở cùng vị trí
/// sẽ **giữ nguyên State cũ** (ví dụ đổi tab của cùng một `StudioScreen`), nên
/// lượt dựng sau cần khoá riêng mới thật sự là màn mới.
Future<void> _pumpScreen(
  WidgetTester tester,
  Widget screen, {
  Size size = _narrow,
  bool signedIn = true,
  String? screenKey,
}) async {
  _sizeTo(tester, size);

  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        authControllerProvider.overrideWith(
          () => _FakeAuth(signedIn ? _session(true) : null),
        ),
        // Chuông trên header nạp thông báo ngay khi màn dựng; test bố cục đo
        // pixel nên không được phụ thuộc mạng thật (một request đang bay cũng
        // đủ để `flutter_test` báo còn timer treo).
        notificationsProvider.overrideWith(
          () => _FakeNotifications(const []),
        ),
      ],
      child: MaterialApp(
        theme: AppTheme.material(AppTheme.forui()),
        builder: (context, inner) =>
            FTheme(data: AppTheme.forui(), child: inner ?? const SizedBox()),
        home: KeyedSubtree(
          key: ValueKey(screenKey ?? screen.runtimeType.toString()),
          child: screen,
        ),
      ),
    ),
  );
  await tester.pump(const Duration(milliseconds: 250));
}

/// Dựng toàn bộ app (đã đăng nhập) để kiểm tra shell và từng tab.
///
/// `ProviderScope` được gắn khoá theo tham số: gọi lần thứ hai với tham số khác
/// phải **dựng lại từ đầu**. Không có khoá thì `pumpWidget` chỉ cập nhật cây
/// hiện có, `Navigator` cũ được giữ nguyên — và sheet chức năng của lượt trước
/// vẫn nằm trên đó, khiến lượt sau đọc nhầm nội dung của lượt trước.
Future<void> _pumpApp(
  WidgetTester tester, {
  Size size = _narrow,
  bool admin = true,
}) async {
  _sizeTo(tester, size);

  await tester.pumpWidget(
    ProviderScope(
      key: ValueKey('app-$admin-${size.width}'),
      overrides: [
        authControllerProvider.overrideWith(() => _FakeAuth(_session(admin))),
        // Như trên: shell có chuông nên phải chặn sẵn nguồn thông báo.
        notificationsProvider.overrideWith(
          () => _FakeNotifications(const []),
        ),
      ],
      child: const LmKitOmniApp(),
    ),
  );
  await tester.pump(const Duration(milliseconds: 250));
}

AuthSession _session(
  bool admin, {
  String tenantName = 'Trung tâm Thông tin và Lưu trữ Quốc gia',
  String agentName = 'CILA - AI Agent',
}) => AuthSession(
  accessToken: 'access',
  refreshToken: 'refresh',
  accessTokenExpiresAt: DateTime(2030),
  refreshTokenExpiresAt: DateTime(2030),
  user: UserModel(
    id: 'u1',
    email: 'admin@example.com',
    fullName: 'Nguyễn Văn A',
    role: admin ? 'Admin' : 'Member',
    tenantId: 't1',
    tenant: TenantBranding(name: tenantName, agentName: agentName),
  ),
);

class _FakeAuth extends AuthController {
  _FakeAuth(this.session);

  final AuthSession? session;

  @override
  Future<AuthSession?> build() async => session;
}

/// Mở danh sách chức năng bằng nút ba chấm trên header — cửa duy nhất vào các
/// mục cấp một kể từ khi bỏ thanh điều hướng dưới.
Future<void> _openFunctionMenu(WidgetTester tester) async {
  final button = find.byTooltip('Danh sách chức năng');
  expect(button, findsOneWidget, reason: 'header không có nút ba chấm');
  await tester.tap(button);
  await tester.pump(const Duration(milliseconds: 300));
}

/// Danh sách của sheet chức năng. Phải chỉ đích danh nó thay vì để
/// `scrollUntilVisible` tự đoán Scrollable nào (khung chat phía sau cũng cuộn
/// được).
Finder get _menuList => find.descendant(
  of: find.byType(FunctionMenuSheet),
  matching: find.byType(Scrollable),
);

/// Mục trong sheet chức năng theo nhãn.
///
/// Phải chỉ đích danh trong sheet: vài nhãn trùng với tiêu đề trang (ví dụ "AI
/// Chat" vừa là mục trong danh sách vừa là tiêu đề header đang mở) nên
/// `find.text` trần sẽ trả về hai widget và `scrollUntilVisible` báo
/// "Too many elements".
Finder _menuItem(String label) => find.descendant(
  of: find.byType(FunctionMenuSheet),
  matching: find.text(label),
);

Future<void> _scrollTo(WidgetTester tester, String label, Finder list) =>
    tester.scrollUntilVisible(
      _menuItem(label),
      240,
      scrollable: list,
      maxScrolls: 40,
    );

/// Mở hộp thoại tạo mới bằng cách gọi thẳng `onPressed` của nút nổi.
void _pressFab(WidgetTester tester) {
  final fab = find.byType(FloatingActionButton);
  expect(fab, findsOneWidget, reason: 'màn không có nút tạo mới');
  final button = tester.widget<FloatingActionButton>(fab);
  (button.onPressed ?? () {})();
}

void main() {
  group('shell', () {
    testWidgets('không còn thanh điều hướng dưới', (tester) async {
      // Quyết định bố cục: màn chat là màn gốc duy nhất, điều hướng nằm ở
      // header. Thanh dưới quay trở lại là mất 64dp chiều cao của transcript,
      // và kéo theo cảnh thanh dưới tô sáng nhầm mục ở các màn đẩy sang.
      await _pumpApp(tester);

      expect(find.byType(NavigationBar), findsNothing);
      expect(find.byType(BottomNavigationBar), findsNothing);
      expect(find.byType(ChatScreen), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('nút ba chấm mở đủ danh sách chức năng ở 320dp', (tester) async {
      await _pumpApp(tester);
      await _openFunctionMenu(tester);

      expect(find.byType(FunctionMenuSheet), findsOneWidget);

      // Danh sách dài hơn một màn 640dp nên mọi mục đều phải **tới được** bằng
      // cách cuộn — trong đó có nhóm "Không gian làm việc", tức nhóm đã thay
      // các tab cũ (AI Chat / Projects / RAG Documents).
      for (final label in const [
        'AI Chat',
        'Projects',
        'RAG Documents',
        'Agent Studio',
        'HITL Approvals',
        'Đoạn chat được chia sẻ',
        'Đăng xuất',
      ]) {
        await _scrollTo(tester, label, _menuList);
        expect(_menuItem(label), findsOneWidget, reason: 'thiếu mục "$label"');
        expect(tester.takeException(), isNull, reason: 'mục "$label"');
      }
    });

    testWidgets('menu chức năng vẫn đúng ở khổ máy thật (390dp)', (
      tester,
    ) async {
      await _pumpApp(tester, size: _phone);
      await _openFunctionMenu(tester);
      await _scrollTo(tester, 'Đăng xuất', _menuList);

      expect(_menuItem('Đăng xuất'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('menu chịu được cỡ chữ hệ thống lớn (1.3×) ở 320dp', (
      tester,
    ) async {
      // Người dùng lớn tuổi là nhóm chính của app hành chính: cỡ chữ hệ thống
      // lớn hơn vẫn phải dùng được, không cắt nhãn hay đè lên nhau.
      tester.platformDispatcher.textScaleFactorTestValue = 1.3;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);

      await _pumpApp(tester);
      await _openFunctionMenu(tester);

      for (final label in const ['AI Chat', 'HITL Approvals', 'Đăng xuất']) {
        await _scrollTo(tester, label, _menuList);
        expect(_menuItem(label), findsOneWidget, reason: 'thiếu mục "$label"');
        expect(tester.takeException(), isNull, reason: 'mục "$label" ở 1.3×');
      }
    });

    testWidgets('admin thấy nhóm quản trị, thành viên thì không', (
      tester,
    ) async {
      await _pumpApp(tester);
      await _openFunctionMenu(tester);
      await _scrollTo(tester, 'Dashboard quản trị', _menuList);
      expect(_menuItem('Dashboard quản trị'), findsOneWidget);

      await _pumpApp(tester, admin: false);
      await _openFunctionMenu(tester);
      // Cuộn hết danh sách rồi mới kết luận: mục quản trị nằm ngay trước nhóm
      // "Cài đặt", nên nếu tồn tại thì phải thấy ở đoạn này.
      await _scrollTo(tester, 'Đăng xuất', _menuList);
      expect(_menuItem('Đăng xuất'), findsOneWidget);
      expect(_menuItem('Dashboard quản trị'), findsNothing);
    });
  });

  group('từng màn', () {
    testWidgets('mọi màn vừa ở 320dp', (tester) async {
      for (final entry in _screens.entries) {
        await _pumpScreen(tester, entry.value());
        expect(tester.takeException(), isNull, reason: 'màn "${entry.key}"');
      }
    });

    testWidgets('mọi màn chịu được cỡ chữ hệ thống lớn (1.3×)', (tester) async {
      tester.platformDispatcher.textScaleFactorTestValue = 1.3;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);

      for (final entry in _screens.entries) {
        await _pumpScreen(tester, entry.value());
        expect(
          tester.takeException(),
          isNull,
          reason: 'màn "${entry.key}" ở 1.3×',
        );
      }
    });

    testWidgets('tiêu đề màn con là **tên trang**, không phải tên nhóm', (
      tester,
    ) async {
      // Người dùng bấm "Deep Research" trong menu chức năng thì thanh trên cùng
      // phải đọc đúng "Deep Research"; nếu chỉ ghi tên nhóm ("AI Studio") thì
      // header nói một đằng còn màn đang mở nói một nẻo.
      for (final entry in const {
        'Deep Research': StudioScreen(initialTab: 3),
        'HITL Approvals': StudioScreen(initialTab: 4),
        'Vision & OCR': ToolsScreen(initialTab: 1),
        'Text Analytics': ToolsScreen(),
      }.entries) {
        // `screenKey` riêng cho từng màn: cùng loại widget ở cùng vị trí thì
        // `pumpWidget` giữ nguyên State cũ — không có khoá thì tab của lượt
        // trước còn nguyên và test sẽ đọc nhầm tiêu đề của lượt trước.
        await _pumpScreen(tester, entry.value, screenKey: entry.key);
        expect(
          find.widgetWithText(AppBar, entry.key),
          findsOneWidget,
          reason: 'header không đọc tên trang "${entry.key}"',
        );
      }
    });

    testWidgets('màn đăng nhập vừa ở 320dp', (tester) async {
      await _pumpScreen(tester, const LoginScreen(), signedIn: false);

      expect(tester.takeException(), isNull);
      expect(find.text('Đăng Nhập'), findsOneWidget);
    });

    testWidgets('hộp thoại tạo mới của màn quản trị không tràn (1.3×)', (
      tester,
    ) async {
      // Form tạo mới là chỗ dày nhãn nhất: nhãn + gợi ý + dropdown + nút.
      tester.platformDispatcher.textScaleFactorTestValue = 1.3;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);

      for (final entry in const {
        'Tenant Management': TenantsScreen(),
        'MCP Servers': McpServersScreen(),
        'Database Connections': DatabaseConnectionsScreen(),
        'LoRA Adapters': LoraAdaptersScreen(),
        'Người dùng': UsersScreen(),
        'API Keys': ApiKeysScreen(),
      }.entries) {
        await _pumpScreen(tester, entry.value);
        expect(tester.takeException(), isNull, reason: 'màn "${entry.key}"');

        _pressFab(tester);
        await tester.pump(const Duration(milliseconds: 350));
        expect(
          tester.takeException(),
          isNull,
          reason: 'hộp thoại tạo mới của "${entry.key}"',
        );
      }
    });
  });

  group('nội dung', () {
    testWidgets('bong bóng chat chịu được nội dung dài ở 320dp', (
      tester,
    ) async {
      // Chuỗi dài không có dấu cách là ca khó nhất: web xử lý bằng
      // `break-words`, Flutter phải soft-wrap hoặc cắt bớt.
      final longToken = 'https://example.gov.vn/${'quyhoach-' * 12}';
      final messages = [
        ChatMessageModel(
          role: 'user',
          content:
              'Tóm tắt tài liệu này giúp tôi:\n$longToken\n'
              'và so sánh với “Quy hoạch tổng thể hệ thống quan trắc môi trường '
              'quốc gia đến năm 2035”.',
        ),
        ChatMessageModel(
          role: 'assistant',
          content:
              'Dạ, tôi đã đọc tài liệu.\n\n**Ba điểm chính**\n1. Phạm vi\n'
              '2. Tiến độ\n3. Nguồn lực\n\n<chart>{"type":"bar","title":"Tiến độ",'
              '"labels":["2024","2025","2026"],"datasets":[{"label":"Hoàn thành",'
              '"values":[12,45,78]}]}</chart>',
          reasoning: 'Người dùng cần bản tóm tắt ngắn gọn, có dẫn nguồn.',
          thinkingSteps: const ['Đọc tài liệu', 'Trích xuất số liệu'],
          webUrls: const ['https://example.gov.vn/van-ban/quy-hoach-2035'],
        ),
      ];

      await _pumpScreen(
        tester,
        Scaffold(
          body: ListView(
            padding: AppTheme.pagePadding,
            children: [
              for (final message in messages) ChatMessageView(message: message),
            ],
          ),
        ),
      );

      expect(tester.takeException(), isNull);
      expect(find.textContaining('Tóm tắt tài liệu'), findsOneWidget);

      // Cuộn xuống để bong bóng trả lời (kèm biểu đồ, nguồn, khối suy luận) được
      // dựng thật — đây là chỗ dễ tràn nhất khi cửa sổ hẹp.
      await tester.scrollUntilVisible(
        find.textContaining('Ba điểm chính'),
        200,
        scrollable: find.byType(Scrollable).first,
      );
      await tester.pump(const Duration(milliseconds: 200));

      expect(tester.takeException(), isNull);
      expect(find.textContaining('Ba điểm chính'), findsOneWidget);
    });

    testWidgets('câu trả lời của trợ lý có avatar và tên trợ lý', (
      tester,
    ) async {
      // Web mở đầu mỗi câu trả lời bằng avatar tròn của tenant + tên trợ lý;
      // thiếu hai thứ này thì người dùng không biết đang chat với trợ lý nào.
      await _pumpScreen(
        tester,
        ProviderScope(
          overrides: [
            authControllerProvider.overrideWith(
              () => _FakeAuth(
                _session(
                  true,
                  tenantName: 'Trung tâm Thông tin và Lưu trữ Quốc gia',
                  agentName: 'Trợ lý CILA',
                ),
              ),
            ),
          ],
          child: Scaffold(
            body: ListView(
              padding: AppTheme.pagePadding,
              children: [
                ChatMessageView(
                  message: ChatMessageModel(role: 'user', content: 'Xin chào'),
                ),
                ChatMessageView(
                  message: ChatMessageModel(
                    role: 'assistant',
                    content: 'Dạ, tôi có thể giúp gì ạ?',
                  ),
                ),
              ],
            ),
          ),
        ),
      );

      expect(tester.takeException(), isNull);
      expect(find.text('Trợ lý CILA'), findsOneWidget);
      // Đơn vị chưa cấu hình logo (và trong test cũng không tải được ảnh nào) →
      // avatar phải là **Quốc huy** đóng gói trong app, không phải avatar chữ cái.
      expect(
        find.byWidgetPredicate(
          (w) =>
              w is SvgPicture &&
              w.bytesLoader.toString().contains('quochuy.svg'),
        ),
        findsOneWidget,
        reason: 'avatar mặc định phải là Quốc huy',
      );
      // Và không còn avatar chữ cái nào lọt lại.
      expect(find.text('T'), findsNothing);
    });

    testWidgets('khung danh sách admin gọn ở 320dp với dữ liệu thật', (
      tester,
    ) async {
      // Mọi màn quản trị đều dựng trên AdminListView, nên kiểm tra khung này một
      // lần là phủ được cả nhóm khi không có máy chủ để thấy dữ liệu thật.
      await _pumpScreen(
        tester,
        AdminListView<String>(
          title: 'Tenant Management',
          description:
              'Tạo, sửa, xoá tenant và logo thương hiệu dùng cho toàn hệ thống.',
          searchHint: 'Tìm theo tên tenant',
          fetch: (_) async => [
            'Trung tâm Thông tin và Lưu trữ Quốc gia — Chi nhánh Thành phố Hồ '
                'Chí Minh (khu vực phía Nam)',
          ],
          emptyText: 'Chưa có tenant nào.',
          itemBuilder: (context, item, reload) => Card(
            child: ListTile(
              leading: const Icon(Icons.apartment_outlined),
              title: Text(item, maxLines: 2),
              subtitle: const Text('Trợ lý: CILA - AI Agent'),
              trailing: PopupMenuButton<String>(
                onSelected: (_) {},
                itemBuilder: (_) => const [
                  PopupMenuItem(value: 'edit', child: Text('Sửa tenant')),
                  PopupMenuItem(value: 'delete', child: Text('Xoá tenant')),
                ],
              ),
            ),
          ),
        ),
      );

      expect(tester.takeException(), isNull);
      expect(find.textContaining('Trung tâm Thông tin'), findsOneWidget);
    });

    testWidgets('danh sách thông báo chịu được tiêu đề dài', (tester) async {
      await _pumpScreen(
        tester,
        ProviderScope(
          overrides: [
            notificationsProvider.overrideWith(
              () => _FakeNotifications(_items),
            ),
          ],
          child: const NotificationsScreen(),
        ),
      );

      expect(tester.takeException(), isNull);
      expect(find.textContaining('Quy hoạch tổng thể'), findsOneWidget);
    });
  });
}

final _items = [
  NotificationModel(
    id: '1',
    title:
        'Tài liệu “Quy hoạch tổng thể hệ thống quan trắc môi trường quốc gia '
        'đến năm 2035” đã được xử lý và vector hóa xong',
    body:
        'Bạn có thể dùng tài liệu này trong RAG Documents hoặc ghim vào custom '
        'agent để làm ngữ cảnh riêng cho từng trợ lý.',
    createdAt: DateTime.now(),
  ),
  NotificationModel(
    id: '2',
    title: 'Tác vụ tự động “Tổng hợp báo cáo tuần” cần bạn phê duyệt',
    isRead: true,
    createdAt: DateTime.now().subtract(const Duration(days: 3)),
  ),
];

class _FakeNotifications extends NotificationsController {
  _FakeNotifications(this.items);

  final List<NotificationModel> items;

  @override
  Future<List<NotificationModel>> build() async => items;
}
