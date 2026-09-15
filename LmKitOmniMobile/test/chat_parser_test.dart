import 'package:flutter_test/flutter_test.dart';

import 'package:lmkit_omni_mobile/features/chat/chat_models.dart';
import 'package:lmkit_omni_mobile/features/chat/chat_sse_parser.dart';

void main() {
  test('parses split JSON SSE lines and desktop markers', () {
    final parser = ChatSseParser();
    expect(parser.push('data: "[THINKING]: đang đọc"\n\n'), hasLength(1));
    final events = parser.push('data: "Xin chào"\n\n');
    expect(events.single.type, 'content');
    expect(events.single.value, 'Xin chào');
    expect(parser.push('data: "[DONE]"\n\n').single.type, 'done');
  });

  test('removes persisted protocol markers from assistant content', () {
    final parsed = parseStoredContent(
      '[THINKING]: Đang tìm\n[REASONING]: kiểm tra\n[WEB_SEARCH]:https://example.com\nKết quả',
    );
    expect(parsed.content, 'Kết quả');
    expect(parsed.thinkingSteps, ['Đang tìm']);
    expect(parsed.reasoning, 'kiểm tra');
    expect(parsed.webUrls, ['https://example.com']);
  });
}
