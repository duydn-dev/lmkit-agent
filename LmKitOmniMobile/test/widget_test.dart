import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:lmkit_omni_mobile/app/app.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_models.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_provider.dart';

void main() {
  testWidgets('renders the CILA login screen', (tester) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          authControllerProvider.overrideWith(TestAuthController.new),
        ],
        child: const LmKitOmniApp(),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Trợ lý ảo - CILA AI'), findsOneWidget);
    expect(find.textContaining('Trung tâm Thông tin lưu trữ'), findsOneWidget);
    expect(find.text('Đăng Nhập'), findsOneWidget);
    expect(find.text('Tên tài khoản'), findsOneWidget);
    expect(find.text('Mật khẩu'), findsOneWidget);
    expect(find.byType(SvgPicture), findsOneWidget);
  });

  testWidgets('Quốc huy tải được thành picture', (tester) async {
    final loader = SvgAssetLoader('assets/images/quochuy.svg');
    final info = await vg.loadPicture(loader, null);

    expect(info.size.width, greaterThan(0));
    expect(info.size.height, greaterThan(0));
    info.picture.dispose();
  });
}

class TestAuthController extends AuthController {
  @override
  Future<AuthSession?> build() async => null;
}
