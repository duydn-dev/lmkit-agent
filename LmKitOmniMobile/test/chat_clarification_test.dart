import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';

import 'package:lmkit_omni_mobile/app/theme.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_models.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_provider.dart';
import 'package:lmkit_omni_mobile/features/chat/chat_message_view.dart';
import 'package:lmkit_omni_mobile/features/chat/chat_models.dart';
import 'package:lmkit_omni_mobile/features/chat/chat_sse_parser.dart';

/// Thẻ hỏi lại (clarification) trên mobile phải khớp web/API: backend gửi marker
/// `[CLARIFICATION:{json}]`, app dựng câu hỏi + lựa chọn bấm được (đề xuất có nhãn)
/// + nút "Khác", và sau khi trả lời thì đổi thành dòng xác nhận.
void main() {
  group('ChatSseParser', () {
    test('nhận diện marker [CLARIFICATION] thành sự kiện riêng', () {
      final parser = ChatSseParser();
      const payload =
          '{"question":"Bạn muốn báo cáo về chủ đề nào?","options":['
          '{"label":"Kinh doanh","value":"Kinh doanh","recommended":true},'
          '{"label":"Kỹ thuật","value":"Kỹ thuật","recommended":false}]}';

      // Backend gửi nguyên marker dưới dạng JSON string (đã escape) trong dòng data:.
      final events = parser.push(
        'data: ${jsonEncode('[CLARIFICATION:$payload]')}\n\n',
      );

      expect(events, hasLength(1));
      expect(events.single.type, 'clarification');
      expect(events.single.value, payload);
    });

    test('marker clarification không bị coi là nội dung câu trả lời', () {
      final parser = ChatSseParser();
      final events = parser.push(
        'data: "[CLARIFICATION:{\\"question\\":\\"Chọn?\\",\\"options\\":[]}]"\n\n',
      );
      expect(events.single.type, isNot('content'));
    });
  });

  group('ChatMessageView — thẻ hỏi lại', () {
    Future<void> pumpView(
      WidgetTester tester,
      ChatMessageModel message, {
      ValueChanged<String>? onAnswer,
    }) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            authControllerProvider.overrideWith(() => _FakeAuth(_session)),
          ],
          child: MaterialApp(
            theme: AppTheme.material(AppTheme.forui()),
            builder: (context, inner) =>
                FTheme(data: AppTheme.forui(), child: inner ?? const SizedBox()),
            home: Scaffold(
              body: SingleChildScrollView(
                child: ChatMessageView(
                  message: message,
                  onClarificationAnswer: onAnswer,
                ),
              ),
            ),
          ),
        ),
      );
      await tester.pump();
    }

    ChatMessageModel clarificationMessage() => ChatMessageModel(
      role: 'assistant',
      content: '',
      clarification: const {
        'question': 'Bạn muốn báo cáo về chủ đề nào?',
        'options': [
          {'label': 'Kinh doanh', 'value': 'Kinh doanh', 'recommended': true},
          {'label': 'Kỹ thuật', 'value': 'Kỹ thuật', 'recommended': false},
          {'label': 'KPI', 'value': 'KPI', 'recommended': false},
        ],
      },
    );

    testWidgets('hiện nhãn, câu hỏi, lựa chọn (badge Đề xuất) và hàng Khác', (tester) async {
      await pumpView(tester, clarificationMessage());

      expect(find.text('CẦN LÀM RÕ'), findsOneWidget);
      expect(find.text('Bạn muốn báo cáo về chủ đề nào?'), findsOneWidget);
      expect(find.text('Kinh doanh'), findsOneWidget);
      expect(find.text('Kỹ thuật'), findsOneWidget);
      expect(find.text('KPI'), findsOneWidget);
      expect(find.text('Đề xuất'), findsOneWidget);
      expect(find.text('Khác — tự nhập câu trả lời'), findsOneWidget);
    });

    testWidgets('bấm một lựa chọn → trả về đúng value', (tester) async {
      String? answer;
      await pumpView(
        tester,
        clarificationMessage(),
        onAnswer: (value) => answer = value,
      );

      await tester.tap(find.text('Kỹ thuật'));
      await tester.pump();

      expect(answer, 'Kỹ thuật');
    });

    testWidgets('hàng Khác mở ô nhập tự do ngay tại chỗ và gửi câu trả lời', (tester) async {
      String? answer;
      await pumpView(
        tester,
        clarificationMessage(),
        onAnswer: (value) => answer = value,
      );

      await tester.tap(find.text('Khác — tự nhập câu trả lời'));
      await tester.pumpAndSettle();
      expect(find.byType(TextField), findsOneWidget);
      expect(find.text('Nhập câu trả lời khác...'), findsOneWidget);

      await tester.enterText(find.byType(TextField), 'Báo cáo về xuất khẩu gạo');
      await tester.pump();
      await tester.tap(find.widgetWithText(FilledButton, 'Gửi'));
      await tester.pumpAndSettle();

      expect(answer, 'Báo cáo về xuất khẩu gạo');
      expect(find.byType(TextField), findsNothing);
    });

    testWidgets('nút Huỷ đóng ô nhập Khác mà không gửi gì', (tester) async {
      String? answer;
      await pumpView(
        tester,
        clarificationMessage(),
        onAnswer: (value) => answer = value,
      );

      await tester.tap(find.text('Khác — tự nhập câu trả lời'));
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextField), 'gõ dở');
      await tester.pump();
      await tester.tap(find.text('Huỷ'));
      await tester.pumpAndSettle();

      expect(answer, isNull);
      expect(find.byType(TextField), findsNothing);
    });

    testWidgets('đã trả lời → thay lựa chọn bằng dòng xác nhận', (tester) async {
      final message = clarificationMessage()..clarificationAnswered = true;
      await pumpView(tester, message, onAnswer: (_) {});

      expect(find.text('ĐÃ TRẢ LỜI'), findsOneWidget);
      expect(find.text('Khác — tự nhập câu trả lời'), findsNothing);
    });
  });
}

class _FakeAuth extends AuthController {
  _FakeAuth(this.session);

  final AuthSession? session;

  @override
  Future<AuthSession?> build() async => session;
}

final _session = AuthSession(
  accessToken: 'access',
  refreshToken: 'refresh',
  accessTokenExpiresAt: DateTime(2030),
  refreshTokenExpiresAt: DateTime(2030),
  user: const UserModel(
    id: 'u1',
    email: 'admin@cila.gov.vn',
    fullName: 'Nguyễn Văn Duy',
    role: 'Admin',
    tenantId: 't1',
  ),
);
