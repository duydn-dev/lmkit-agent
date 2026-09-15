import 'dart:convert';

import 'chat_models.dart';

class ChatSseParser {
  String _buffer = '';

  List<ChatStreamEvent> push(String chunk) {
    _buffer += chunk;
    final events = <ChatStreamEvent>[];
    var lineEnd = _buffer.indexOf('\n');
    while (lineEnd != -1) {
      final line = _buffer
          .substring(0, lineEnd)
          .replaceFirst(RegExp(r'\r$'), '');
      _buffer = _buffer.substring(lineEnd + 1);
      final event = _parseLine(line);
      if (event != null) {
        events.add(event);
      }
      lineEnd = _buffer.indexOf('\n');
    }
    return events;
  }

  List<ChatStreamEvent> finish() {
    if (_buffer.isEmpty) {
      return const [];
    }
    final event = _parseLine(_buffer.replaceFirst(RegExp(r'\r$'), ''));
    _buffer = '';
    return event == null ? const [] : [event];
  }

  ChatStreamEvent? _parseLine(String line) {
    if (!line.startsWith('data:')) {
      return null;
    }
    var raw = line.substring(5);
    if (raw.startsWith(' ')) {
      raw = raw.substring(1);
    }
    var value = raw;
    try {
      final decoded = jsonDecode(raw);
      if (decoded is String) {
        value = decoded;
      }
    } catch (_) {
      // Older API responses may send plain SSE data.
    }

    if (value == '[DONE]') {
      return const ChatStreamEvent('done', '');
    }
    if (value.startsWith('[ERROR]:')) {
      return ChatStreamEvent('error', value.substring(8).trim());
    }
    if (value.startsWith('[THINKING]:')) {
      return ChatStreamEvent('thinking', value.substring(11).trim());
    }
    if (value.startsWith('[REASONING]:')) {
      return ChatStreamEvent('reasoning', value.substring(12));
    }
    if (value.startsWith('[WEB_SEARCH]:')) {
      return ChatStreamEvent(
        'web-search',
        value
            .substring(13)
            .replaceAll(RegExp(r'\\[rn]'), '')
            .replaceAll(RegExp(r'[\r\n]'), '')
            .trim(),
      );
    }
    if (value.startsWith('[HITL_APPROVAL_REQUIRED:') && value.endsWith(']')) {
      return ChatStreamEvent(
        'approval',
        value.substring(24, value.length - 1).trim(),
      );
    }
    if (value.startsWith('[FILE:') && value.endsWith(']')) {
      return ChatStreamEvent('file', value.substring(6, value.length - 1));
    }
    if (value.startsWith('[RESEARCH_SAVED:') && value.endsWith(']')) {
      return ChatStreamEvent('saved', value.substring(16, value.length - 1));
    }
    if (value.startsWith('[Agent invoked:')) {
      return ChatStreamEvent('agent-log', value);
    }
    return ChatStreamEvent('content', value);
  }
}
