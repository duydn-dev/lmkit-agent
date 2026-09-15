import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:lmkit_omni_mobile/features/chat/message_format.dart';

void main() {
  group('buildMessageSpans', () {
    test('in đậm phần nằm trong ** và giữ nguyên phần còn lại', () {
      final spans = buildMessageSpans('Kết quả **quan trọng** đây');

      expect(spans.map((span) => span.text), [
        'Kết quả ',
        'quan trọng',
        ' đây',
      ]);
      expect(spans[1].style?.fontWeight, FontWeight.bold);
      expect(spans[0].style, isNull);
    });

    test('nhiều cặp đậm trong cùng một dòng', () {
      final spans = buildMessageSpans('**a** và **b**');

      expect(spans.map((span) => span.text), ['a', ' và ', 'b']);
      expect(spans[2].style?.fontWeight, FontWeight.bold);
    });

    test('giữ nguyên ** khi thiếu cặp đóng', () {
      final spans = buildMessageSpans('Nhân ** hai');

      expect(spans.length, 1);
      expect(spans.single.text, 'Nhân ** hai');
    });

    test('không đậm khi dấu * nằm trong cặp **', () {
      final spans = buildMessageSpans('**a * b**');

      expect(spans.single.text, '**a * b**');
    });

    test('chuẩn hoá CRLF về LF', () {
      final spans = buildMessageSpans('dòng 1\r\ndòng 2');

      expect(spans.single.text, 'dòng 1\ndòng 2');
    });

    test('chuỗi rỗng trả về danh sách rỗng', () {
      expect(buildMessageSpans(''), isEmpty);
    });

    test('đậm ở đầu và cuối chuỗi', () {
      final spans = buildMessageSpans('**đầu** giữa **cuối**');

      expect(spans.map((span) => span.text), ['đầu', ' giữa ', 'cuối']);
      expect(spans.first.style?.fontWeight, FontWeight.bold);
      expect(spans.last.style?.fontWeight, FontWeight.bold);
    });

    test('kế thừa style nền và chỉ đổi độ đậm', () {
      final spans = buildMessageSpans(
        '**đậm**',
        baseStyle: const TextStyle(fontSize: 18, color: Colors.red),
      );

      expect(spans.single.style?.fontSize, 18);
      expect(spans.single.style?.color, Colors.red);
      expect(spans.single.style?.fontWeight, FontWeight.bold);
    });
  });

  testWidgets('FormattedMessage hiển thị đúng nội dung đã tách', (
    tester,
  ) async {
    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(body: FormattedMessage(text: 'Xin **chào** bạn')),
      ),
    );

    expect(find.textContaining('chào'), findsOneWidget);
  });
}
