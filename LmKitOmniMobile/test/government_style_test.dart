import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:lmkit_omni_mobile/app/app.dart';
import 'package:lmkit_omni_mobile/app/theme.dart';
import 'package:lmkit_omni_mobile/app/ui/app_controls.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_models.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_provider.dart';

/// Bộ test này ghim giao diện vào đúng token của `LmKitOmniClient/src/style.css`
/// và `AppLayout.vue`. Mục đích: nếu ai đó đổi màu/kích thước chrome, test đỏ
/// ngay thay vì để app lệch khỏi bản web một cách âm thầm.
void main() {
  group('theme Material', () {
    final theme = AppTheme.material(AppTheme.forui());

    test('nhận diện Chính phủ đúng mã màu web', () {
      expect(AppTheme.govRed, const Color(0xFFB81F33)); // --color-gov-red
      expect(AppTheme.govRedDark, const Color(0xFF7F1826));
      expect(AppTheme.govYellow, const Color(0xFFFFCD00));
      expect(AppTheme.govBlueDark, const Color(0xFF1E3A8A)); // chrome
      expect(AppTheme.surface, const Color(0xFFF8FAFC)); // nền trang
      expect(AppTheme.textPrimary, const Color(0xFF111827));
      expect(AppTheme.textMuted, const Color(0xFF4B5563));
      expect(AppTheme.border, const Color(0xFFE2E8F0));
    });

    test('chỉ có font Be Vietnam Pro', () {
      expect(AppTheme.fontFamily, 'Be Vietnam Pro');
      expect(theme.textTheme.bodyMedium?.fontFamily, AppTheme.fontFamily);
      expect(theme.textTheme.titleMedium?.fontFamily, AppTheme.fontFamily);
      expect(theme.appBarTheme.titleTextStyle?.fontFamily, AppTheme.fontFamily);
    });

    test('header xanh navy, cao 56px như h-14 của web', () {
      expect(theme.appBarTheme.backgroundColor, AppTheme.govBlueDark);
      expect(theme.appBarTheme.foregroundColor, Colors.white);
      expect(theme.appBarTheme.toolbarHeight, 56);
      expect(theme.appBarTheme.elevation, 0);
    });

    test('thẻ trắng bo 12px kèm viền, giống rounded-xl border-gray-200', () {
      final shape = theme.cardTheme.shape! as RoundedRectangleBorder;
      expect((shape.borderRadius as BorderRadius).topLeft.x, AppTheme.radius);
      expect(shape.side.color, AppTheme.border);
      expect(theme.cardTheme.color, Colors.white);
    });

    test('ô nhập bo 8px, viền xanh khi focus, đỏ khi lỗi', () {
      final decoration = theme.inputDecorationTheme;
      expect(
        (decoration.border! as OutlineInputBorder).borderRadius.topLeft.x,
        AppTheme.radiusSmall,
      );
      expect(decoration.focusedBorder!.borderSide.color, AppTheme.govBlueDark);
      expect(decoration.errorBorder!.borderSide.color, AppTheme.govRed);
      expect(decoration.fillColor, Colors.white);
    });

    test('viền tiêu điểm đỏ quốc kỳ như :focus-visible của web', () {
      expect(theme.focusColor, AppTheme.govRed);
    });

    test('nút chính xanh navy, nút phá huỷ đỏ quốc kỳ', () {
      final primary = theme.filledButtonTheme.style!.backgroundColor!.resolve(
        {},
      );
      expect(primary, AppTheme.govBlueDark);
      expect(theme.colorScheme.error, AppTheme.govRed);
    });

    test('thang chữ đúng từng bước đã đối chiếu với web', () {
      // Bảng ánh xạ Tailwind → token nằm ở `AppTheme._textTheme`; test này giữ
      // cho các con số đó không bị sửa lẻ ở một màn nào đó.
      expect(theme.textTheme.displaySmall?.fontSize, 30); // text-3xl
      expect(theme.textTheme.headlineSmall?.fontSize, 24); // text-2xl
      expect(theme.textTheme.titleLarge?.fontSize, 20); // text-xl
      expect(theme.textTheme.titleMedium?.fontSize, 16); // text-base
      expect(theme.textTheme.titleSmall?.fontSize, 14); // text-sm
      expect(theme.textTheme.bodyLarge?.fontSize, 16); // nội dung chat
      expect(theme.textTheme.bodyMedium?.fontSize, 14); // text-sm
      expect(theme.textTheme.bodySmall?.fontSize, 12); // text-xs
      expect(theme.textTheme.labelLarge?.fontSize, 14);
      expect(theme.textTheme.labelMedium?.fontSize, 12);
      expect(theme.textTheme.labelSmall?.fontSize, 11); // text-[11px]
      expect(AppTheme.appBarTitleSize, 18);
      expect(AppTheme.badgeSize, 10);
    });

    test('mọi token chữ đều là Be Vietnam Pro và đúng độ đậm web', () {
      for (final style in [
        theme.textTheme.displaySmall,
        theme.textTheme.headlineSmall,
        theme.textTheme.titleLarge,
        theme.textTheme.titleMedium,
        theme.textTheme.titleSmall,
        theme.textTheme.bodyLarge,
        theme.textTheme.bodyMedium,
        theme.textTheme.bodySmall,
        theme.textTheme.labelLarge,
        theme.textTheme.labelSmall,
      ]) {
        expect(style?.fontFamily, AppTheme.fontFamily);
      }

      expect(theme.textTheme.titleLarge?.fontWeight, FontWeight.w700);
      expect(theme.textTheme.titleMedium?.fontWeight, FontWeight.w600);
      expect(theme.textTheme.bodyMedium?.fontWeight, FontWeight.w400);
      // Nhãn siêu nhỏ của web có `tracking-wider` (0.05–0.1em).
      expect(theme.textTheme.labelSmall?.letterSpacing, greaterThan(0));
    });

    test('băng lỗi dùng đúng tông đỏ của web', () {
      expect(AppTheme.dangerSurface, const Color(0xFFFEF2F2)); // red-50
      expect(AppTheme.dangerBorder, const Color(0xFFFECACA)); // red-200
      expect(AppTheme.dangerText, const Color(0xFFB91C1C)); // red-700
      // Thẻ phê duyệt HITL của web là cam, không phải tím M3.
      expect(AppTheme.hitlSurface, const Color(0xFFFFF7ED)); // orange-50
      expect(AppTheme.hitlBorder, const Color(0xFFFED7AA)); // orange-200
      expect(AppTheme.hitlText, const Color(0xFFC2410C)); // orange-700
    });

    /// Nút icon là loại nút Material duy nhất của app nằm trên chrome navy, nên
    /// nền của nó **không được đổi theo trạng thái**. Forui dựng
    /// `iconButtonTheme` từ nút "ghost" của nó, và nút ghost đổi nền khi hover
    /// thành một tông gần trắng — trên dải navy, hover biến nút thành ô trắng
    /// đúng bằng kích thước nút rồi icon trắng biến mất trong đó.
    test('nút icon không đổi nền khi hover/nhấn, phản hồi nằm ở lớp phủ', () {
      final style = theme.iconButtonTheme.style!;
      for (final state in <WidgetState>{
        WidgetState.hovered,
        WidgetState.pressed,
        WidgetState.focused,
        WidgetState.disabled,
      }) {
        expect(
          style.backgroundColor?.resolve({state}),
          Colors.transparent,
          reason: 'nền nút icon phải luôn trong suốt (trạng thái $state)',
        );
      }
      final hoverOverlay = style.overlayColor?.resolve({WidgetState.hovered});
      expect(hoverOverlay, isNotNull);
      // Lớp phủ phải là navy mờ: thấy được trên nền trắng mà không chói trên
      // chrome tối. Trắng ở đây chính là lỗi cũ.
      expect(hoverOverlay!.a, greaterThan(0));
      expect(hoverOverlay.g, lessThan(0.5));
    });

    test('snackbar nổi (không che nội dung cuối trang)', () {
      expect(theme.snackBarTheme.behavior, SnackBarBehavior.floating);
    });
  });

  testWidgets('theme được áp thật lên chrome khi app chạy', (tester) async {
    tester.view.physicalSize = const Size(390, 844);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          authControllerProvider.overrideWith(
            () => _FakeAuth(
              AuthSession(
                accessToken: 'a',
                refreshToken: 'r',
                accessTokenExpiresAt: DateTime(2030),
                refreshTokenExpiresAt: DateTime(2030),
                user: const UserModel(
                  id: 'u1',
                  email: 'admin@example.com',
                  fullName: 'Nguyễn Văn A',
                  role: 'Admin',
                  tenantId: 't1',
                  tenant: TenantBranding(
                    name: 'Trung tâm Thông tin và Lưu trữ Quốc gia',
                    agentName: 'CILA - AI Agent',
                  ),
                ),
              ),
            ),
          ),
        ],
        child: const LmKitOmniApp(),
      ),
    );
    // pumpAndSettle chứ không pump một nhịp: hiệu ứng chuyển màn đầu tiên còn
    // để lại timer, và test sẽ đỏ oan vì "A Timer is still pending".
    await tester.pumpAndSettle(const Duration(milliseconds: 50));

    expect(tester.getSize(find.byType(AppBar).first).height, 56);

    final appBarSurface = tester.widget<Material>(
      find
          .descendant(
            of: find.byType(AppBar).first,
            matching: find.byType(Material),
          )
          .first,
    );
    expect(appBarSurface.color, AppTheme.govBlueDark);

    // Thanh điều hướng dưới đã bị bỏ khỏi app: mọi mục cấp một giờ nằm sau nút
    // ba chấm trên **chính header xanh này**. Kiểm luôn chỗ đó, nếu không hai
    // phép đo màu/chiều cao ở trên chỉ đang đo một thanh mà app không còn dùng.
    expect(find.byType(NavigationBar), findsNothing);
    expect(
      find.descendant(
        of: find.byType(AppBar).first,
        matching: find.byTooltip('Danh sách chức năng'),
      ),
      findsOneWidget,
      reason: 'nút mở danh sách chức năng phải nằm trong header',
    );
  });

  testWidgets('băng lỗi hiện đúng tông đỏ và đóng được', (tester) async {
    var dismissed = 0;
    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.material(AppTheme.forui()),
        home: Scaffold(
          body: AppErrorBanner(
            message: 'Không kết nối được máy chủ.',
            onDismiss: () => dismissed++,
          ),
        ),
      ),
    );

    expect(find.text('Không kết nối được máy chủ.'), findsOneWidget);
    final surface = tester.widget<Container>(
      find
          .ancestor(
            of: find.text('Không kết nối được máy chủ.'),
            matching: find.byType(Container),
          )
          .first,
    );
    expect((surface.decoration as BoxDecoration).color, AppTheme.dangerSurface);

    await tester.tap(find.byTooltip('Đóng thông báo lỗi'));
    await tester.pump(const Duration(milliseconds: 350));
    expect(dismissed, 1);
  });

  /// Bắt đúng thứ người dùng nhìn thấy: **pixel vẽ ra** khi hover nút trên
  /// header. Test theme ở trên chỉ khẳng định giá trị token; chỉ phép đo pixel
  /// mới phát hiện được lớp khác (theme của Forui, AppBar sinh lại style) ghi đè
  /// lên đó — chính là lúc bug cũ lọt qua 147 test xanh.
  testWidgets('hover nút trên header không làm nút thành ô trắng', (tester) async {
    tester.view.physicalSize = const Size(390, 200);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    final key = GlobalKey();
    await tester.pumpWidget(
      FTheme(
        data: AppTheme.forui(),
        child: RepaintBoundary(
          key: key,
          child: MaterialApp(
            theme: AppTheme.material(AppTheme.forui()),
            home: Scaffold(
              appBar: AppTopBar(
                title: const Text('AI Chat'),
                actions: [
                  IconButton(
                    tooltip: 'Danh sách chức năng',
                    onPressed: () {},
                    icon: const Icon(Icons.more_vert),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pump();

    final button = find.byTooltip('Danh sách chức năng');
    final box = tester.getRect(button);
    final gesture = await tester.createGesture(kind: ui.PointerDeviceKind.mouse);
    await gesture.addPointer(location: Offset.zero);
    await gesture.moveTo(tester.getCenter(button));
    await tester.pumpAndSettle();

    // Đọc pixel phải chạy trong `runAsync`: `toImage` cần event loop thật.
    final boundary =
        key.currentContext!.findRenderObject()! as RenderRepaintBoundary;
    final bytes = await tester.runAsync(() async {
      final image = await boundary.toImage();
      final data = await image.toByteData(format: ui.ImageByteFormat.rawRgba);
      image.dispose();
      return data!.buffer.asUint8List();
    });
    await gesture.removePointer();

    final width = tester.view.physicalSize.width.round();
    ({int r, int g, int b}) at(double x, double y) {
      final o = (y.round() * width + x.round()) * 4;
      return (r: bytes![o], g: bytes[o + 1], b: bytes[o + 2]);
    }

    // Hai điểm trong nút nhưng tránh nét icon (icon nằm giữa): sát trái, sát phải.
    for (final point in [
      at(box.left + 3, box.center.dy),
      at(box.right - 3, box.center.dy),
    ]) {
      expect(
        point.g,
        lessThan(120),
        reason: 'nút hover ra gần trắng ($point) — icon trắng sẽ biến mất',
      );
      expect(
        point.b,
        greaterThan(point.r),
        reason: 'lớp phủ hover phải giữ tông navy của header ($point)',
      );
    }

    // Và phản hồi phải **nhìn thấy được**: nút không được trùng màu nền header.
    final chrome = at(8, box.center.dy);
    expect(
      at(box.left + 3, box.center.dy).b,
      greaterThan(chrome.b),
      reason: 'nút lúc hover phải sáng hơn dải header',
    );
  });

  /// Nút trên header phải vẽ **đúng 40×40** (kích thước nút Material 3) trong khi
  /// vùng bấm vẫn **48×48**.
  ///
  /// `AppBar` ép `leading`/`actions` vào ô 56×56 (cao bằng cả thanh) và Material
  /// vẽ nước chạm kín ô đó, nên hover thành một khối vuông to hơn nút gần gấp
  /// rưỡi. Đo bằng pixel/kích thước thật thay vì đọc mã: `AppTopBar` phải giữ
  /// được cả hai vế — vẽ nhỏ mà **không** thu hẹp vùng chạm.
  testWidgets('nút header vẽ 40×40 nhưng vùng chạm vẫn 48×48', (tester) async {
    tester.view.physicalSize = const Size(390, 400);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      FTheme(
        data: AppTheme.forui(),
        child: MaterialApp(
          theme: AppTheme.material(AppTheme.forui()),
          home: Scaffold(
            appBar: AppTopBar(
              title: const Text('AI Chat'),
              leading: Builder(
                builder: (context) => IconButton(
                  tooltip: 'Lịch sử chat',
                  onPressed: () {},
                  icon: const Icon(Icons.menu),
                ),
              ),
              actions: [
                IconButton(
                  tooltip: 'Danh sách chức năng',
                  onPressed: () {},
                  icon: const Icon(Icons.more_vert),
                ),
              ],
            ),
            body: const SizedBox.shrink(),
          ),
        ),
      ),
    );
    await tester.pump();

    // Nút trên header: một ở `leading`, một ở `actions`.
    final buttons = find.descendant(
      of: find.byType(AppBar),
      matching: find.byType(IconButton),
    );
    expect(buttons, findsNWidgets(2));

    for (final element in buttons.evaluate()) {
      final button = find.byWidget(element.widget);
      final label = tester.widget<Tooltip>(
        find.descendant(of: button, matching: find.byType(Tooltip)),
      ).message;

      // Vùng **vẽ ra**: Material của nút, tức chỗ nước chạm được tô.
      final visual = tester.getSize(
        find.descendant(of: button, matching: find.byType(Material)).first,
      );
      expect(
        visual.width,
        lessThanOrEqualTo(40.5),
        reason: 'vùng sáng của "$label" còn rộng $visual',
      );
      expect(visual.height, lessThanOrEqualTo(40.5));

      // Vùng **bấm được**: phải rộng hơn phần vẽ ra, không được thu hẹp theo.
      final tapTarget = tester.getSize(button);
      expect(
        tapTarget.width,
        greaterThanOrEqualTo(48),
        reason: 'thu nhỏ "$label" nhưng đã hạ vùng chạm xuống $tapTarget',
      );
      expect(tapTarget.height, greaterThanOrEqualTo(48));
    }
  });
}

class _FakeAuth extends AuthController {
  _FakeAuth(this.session);

  final AuthSession? session;

  @override
  Future<AuthSession?> build() async => session;
}
