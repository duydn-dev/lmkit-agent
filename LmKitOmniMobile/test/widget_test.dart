import 'package:flutter/material.dart';
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
    // Mỗi ô có **hai** chỗ cùng chữ: nhãn đứng trên ô và placeholder nằm trong
    // ô trống (giống `LoginView.vue` — `label` + `placeholder` trùng nội dung).
    expect(find.text('Tên tài khoản'), findsNWidgets(2));
    expect(find.text('Mật khẩu'), findsNWidgets(2));
    expect(find.byType(SvgPicture), findsOneWidget);
  });

  testWidgets('ô đăng nhập có placeholder trong ô', (tester) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          authControllerProvider.overrideWith(TestAuthController.new),
        ],
        child: const LmKitOmniApp(),
      ),
    );
    await tester.pumpAndSettle();

    // Ô nhập của Forui không dùng `InputDecorator` của Material nên không soi
    // được `hintText`; thay vào đó kiểm bằng **vị trí**: trong hai chỗ có cùng
    // chữ, một chỗ là nhãn nằm trên ô, chỗ còn lại phải nằm *ngang hàng với con
    // trỏ* — đúng nghĩa "placeholder trong ô".
    final emailBox = tester.getCenter(find.byType(EditableText).first);
    final emailTexts = find.text('Tên tài khoản');
    final inBox = [
      for (var i = 0; i < 2; i++)
        if ((tester.getCenter(emailTexts.at(i)).dy - emailBox.dy).abs() < 2) i,
    ];
    expect(inBox, hasLength(1), reason: 'phải có đúng một chỗ là placeholder');
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
