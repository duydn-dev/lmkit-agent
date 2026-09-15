import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
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
    expect(find.text('Đăng nhập'), findsOneWidget);
  });
}

class TestAuthController extends AuthController {
  @override
  Future<AuthSession?> build() async => null;
}
