import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:lmkit_omni_mobile/app/app.dart';
import 'package:lmkit_omni_mobile/app/theme.dart';
import 'package:lmkit_omni_mobile/app/ui/watermark_background.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_models.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_provider.dart';

/// Thanh trạng thái của Android (API 35+) là một phần của cửa sổ app — app vẽ
/// tràn ra sau nó. Hai thứ phải luôn đúng, nếu không người dùng sẽ thấy đồng hồ /
/// sóng / pin "biến mất":
///
/// 1. Icon thanh trạng thái phải **tối** ở những màn nền sáng không có header
///    (màn đăng nhập) — không được thừa hưởng icon trắng của màn header navy
///    trước đó.
/// 2. Hoạ tiết trống đồng phải chừa một dải trống đúng chiều cao thanh trạng
///    thái, để icon nằm trên nền phẳng thay vì chồng lên các đường vòng.
void main() {
  testWidgets('thanh trạng thái mặc định dùng icon tối', (tester) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          authControllerProvider.overrideWith(TestAuthController.new),
        ],
        child: const LmKitOmniApp(),
      ),
    );
    await tester.pumpAndSettle();

    final regions = tester
        .widgetList<AnnotatedRegion<SystemUiOverlayStyle>>(
          find.byType(AnnotatedRegion<SystemUiOverlayStyle>),
        )
        .toList();
    expect(regions, isNotEmpty, reason: 'app phải khai báo kiểu thanh hệ thống');

    final value = regions.first.value;
    expect(value.statusBarIconBrightness, Brightness.dark);
    expect(value.systemNavigationBarIconBrightness, Brightness.dark);
    // Trong suốt để nền trống đồng (chứ không phải một khối màu) lộ phía sau
    // thanh trạng thái.
    expect(value.statusBarColor, Colors.transparent);
  });

  // Kiểm "dải dành riêng" bằng cấu trúc cây: dải phải nằm **trên** ảnh hoạ tiết
  // và đúng chiều cao thanh trạng thái. (Không so pixel: ảnh asset giải mã bằng
  // I/O thật, `pumpAndSettle` trong widget test sẽ treo ở đó.)
  testWidgets('hoạ tiết chừa dải trống cho thanh trạng thái', (tester) async {
    Future<Stack> pumpWith(double inset) async {
      await tester.pumpWidget(
        MediaQuery(
          data: MediaQueryData(padding: EdgeInsets.only(top: inset)),
          child: const Directionality(
            textDirection: TextDirection.ltr,
            child: WatermarkBackground(child: SizedBox.expand()),
          ),
        ),
      );
      return tester.widget<Stack>(
        find.descendant(
          of: find.byType(WatermarkBackground),
          matching: find.byType(Stack),
        ),
      );
    }

    final stack = await pumpWith(40);
    // Thứ tự lớp: [hoạ tiết] → [dải trạng thái] → [nội dung màn].
    expect(stack.children, hasLength(3));
    expect(stack.children.first, isA<OverflowBox>());

    final band = stack.children[1];
    expect(band, isA<Positioned>());
    final positioned = band as Positioned;
    expect(positioned.top, 0);
    expect(positioned.height, 40);
    expect((positioned.child as ColoredBox).color, AppTheme.surface);

    // Màn không có thanh trạng thái (test thường, màn hình ngoài) thì không
    // sinh dải thừa.
    final noInset = await pumpWith(0);
    expect(noInset.children, hasLength(2));
  });
}

class TestAuthController extends AuthController {
  @override
  Future<AuthSession?> build() async => null;
}
