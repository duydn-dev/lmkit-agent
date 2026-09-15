import 'package:flutter/material.dart';

/// Định dạng markdown tối giản đúng bằng tập con mà bản desktop hỗ trợ
/// (`formatSafeMessage`): `**đậm**` và xuống dòng. Mọi thứ khác giữ nguyên văn.
///
/// Desktop escape HTML trước rồi mới chèn thẻ nên nội dung không thể chèn markup;
/// Flutter không có HTML nên chỉ cần tách span là đủ an toàn.
final _bold = RegExp(r'\*\*([^*]+)\*\*');

/// Tách [text] thành các span, phần nằm trong `**...**` được in đậm.
///
/// `**` không có cặp đóng sẽ được giữ nguyên (giống regex của desktop, nơi chỉ
/// thay thế khi khớp đủ cặp).
List<TextSpan> buildMessageSpans(String text, {TextStyle? baseStyle}) {
  final normalized = text.replaceAll('\r\n', '\n');
  if (normalized.isEmpty) return const [];

  final boldStyle = (baseStyle ?? const TextStyle()).copyWith(
    fontWeight: FontWeight.bold,
  );

  final spans = <TextSpan>[];
  var cursor = 0;
  for (final match in _bold.allMatches(normalized)) {
    if (match.start > cursor) {
      spans.add(TextSpan(text: normalized.substring(cursor, match.start)));
    }
    spans.add(TextSpan(text: match.group(1), style: boldStyle));
    cursor = match.end;
  }
  if (cursor < normalized.length) {
    spans.add(TextSpan(text: normalized.substring(cursor)));
  }
  return spans;
}

/// Nội dung tin nhắn đã định dạng, cho phép bôi đen sao chép như bản desktop.
class FormattedMessage extends StatelessWidget {
  const FormattedMessage({
    super.key,
    required this.text,
    this.style,
    this.selectable = true,
  });

  final String text;
  final TextStyle? style;
  final bool selectable;

  @override
  Widget build(BuildContext context) {
    final effective = style ?? DefaultTextStyle.of(context).style;
    final span = TextSpan(
      style: effective,
      children: buildMessageSpans(text, baseStyle: effective),
    );
    return selectable
        ? SelectableText.rich(span)
        : Text.rich(span, style: effective);
  }
}
