import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:lmkit_omni_mobile/features/share/share_repository.dart';
import 'package:lmkit_omni_mobile/features/share/shared_chat_models.dart';
import 'package:lmkit_omni_mobile/features/share/shared_chat_screen.dart';

/// Repository giả: không gọi mạng, trả về kết quả định trước.
class _FakeShareRepository extends ShareRepository {
  _FakeShareRepository(this.result) : super(Dio());

  final SharedChatResult result;
  String? lastToken;

  @override
  Future<SharedChatResult> fetch(String token) async {
    lastToken = token;
    return result;
  }
}

Widget _wrap(ShareRepository repository, Widget child) => ProviderScope(
  overrides: [shareRepositoryProvider.overrideWithValue(repository)],
  child: MaterialApp(
    builder: (context, child) => FTheme(
      data: FTheme.neutral.light.touch,
      child: child ?? const SizedBox.shrink(),
    ),
    home: child,
  ),
);

void main() {
  group('ShareRepository.extractToken', () {
    test('lấy token từ URL đầy đủ', () {
      expect(
        ShareRepository.extractToken('https://app.example.com/share/abc123'),
        'abc123',
      );
    });

    test('lấy token khi URL có query và hash', () {
      expect(
        ShareRepository.extractToken(
          'https://app.example.com/share/abc123?utm=z#top',
        ),
        'abc123',
      );
    });

    test('nhận token trần', () {
      expect(ShareRepository.extractToken('  abc123  '), 'abc123');
    });

    test('trả null với chuỗi rỗng', () {
      expect(ShareRepository.extractToken('   '), isNull);
    });
  });

  testWidgets('hiển thị nội dung khi link hợp lệ', (tester) async {
    final repository = _FakeShareRepository(
      const SharedChatResult(
        status: SharedChatStatus.ok,
        chat: SharedChatModel(
          title: 'Đoạn chat mẫu',
          messages: [
            SharedChatMessageModel(role: 'user', content: 'Xin chào'),
            SharedChatMessageModel(role: 'assistant', content: 'Chào bạn'),
          ],
        ),
      ),
    );

    await tester.pumpWidget(
      _wrap(repository, const SharedChatScreen(initialToken: 'token-1')),
    );
    await tester.pumpAndSettle();

    expect(repository.lastToken, 'token-1');
    expect(find.text('Đoạn chat mẫu'), findsOneWidget);
    expect(find.text('Xin chào'), findsOneWidget);
    expect(find.text('Chào bạn'), findsOneWidget);
  });

  testWidgets('báo rõ khi link đã bị thu hồi', (tester) async {
    final repository = _FakeShareRepository(
      SharedChatResult(
        status: SharedChatStatus.revoked,
        refusedAtUtc: DateTime.utc(2026, 9, 1),
      ),
    );

    await tester.pumpWidget(
      _wrap(repository, const SharedChatScreen(initialToken: 'token-2')),
    );
    await tester.pumpAndSettle();

    expect(find.text('Link đã bị thu hồi'), findsOneWidget);
  });

  testWidgets('báo lỗi khi không tìm thấy token', (tester) async {
    final repository = _FakeShareRepository(
      const SharedChatResult(status: SharedChatStatus.notFound),
    );

    await tester.pumpWidget(
      _wrap(repository, const SharedChatScreen(initialToken: 'token-3')),
    );
    await tester.pumpAndSettle();

    expect(find.text('Không tìm thấy đoạn chat'), findsOneWidget);
  });
}
