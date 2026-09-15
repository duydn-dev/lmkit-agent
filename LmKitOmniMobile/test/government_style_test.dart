import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
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

    test('thanh điều hướng dưới cùng cao 64px, nền trắng', () {
      expect(theme.navigationBarTheme.height, 64);
      expect(theme.navigationBarTheme.backgroundColor, Colors.white);
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

    // Không được có chỉ báo Material mặc định (nền hồng/tím của M3).
    expect(
      tester.getSize(find.byType(NavigationBar)).height,
      64,
      reason: 'chiều cao thanh tab phải theo theme',
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
}

class _FakeAuth extends AuthController {
  _FakeAuth(this.session);

  final AuthSession? session;

  @override
  Future<AuthSession?> build() async => session;
}
