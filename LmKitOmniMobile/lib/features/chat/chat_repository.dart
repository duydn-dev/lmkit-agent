// ignore_for_file: use_null_aware_elements

import 'dart:convert';

import 'package:dio/dio.dart';

import '../../core/network/api_client.dart';
import 'chat_models.dart';
import 'chat_sse_parser.dart';

class ChatRepository {
  ChatRepository(this._client);

  final ApiClient _client;

  Future<List<ChatSessionModel>> listSessions({
    String? before,
    int limit = 20,
  }) async {
    final response = await _client.get(
      '/api/chat/sessions',
      queryParameters: {'limit': limit, if (before != null) 'before': before},
    );
    final rows = response.data is List
        ? response.data as List<dynamic>
        : <dynamic>[];
    return rows
        .whereType<Map<String, dynamic>>()
        .map(ChatSessionModel.fromJson)
        .where((session) => session.id.isNotEmpty)
        .toList();
  }

  Future<List<ChatMessageModel>> getMessages(String sessionId) async {
    final response = await _client.get(
      '/api/chat/sessions/$sessionId/messages',
    );
    final rows = response.data is List
        ? response.data as List<dynamic>
        : <dynamic>[];
    return rows
        .whereType<Map<String, dynamic>>()
        .map(ChatMessageModel.fromJson)
        .toList();
  }

  Future<ChatSessionModel> createSession({
    String? projectId,
    bool ephemeral = false,
  }) async {
    final body = <String, dynamic>{
      if (projectId != null) 'projectId': projectId,
      if (ephemeral) 'ephemeral': true,
    };
    final response = await _client.post(
      '/api/chat/sessions',
      data: body.isEmpty ? null : body,
    );
    return ChatSessionModel.fromJson(
      Map<String, dynamic>.from(response.data as Map),
    );
  }

  Future<void> renameSession(String id, String title) async {
    await _client.patch('/api/chat/sessions/$id', data: {'title': title});
  }

  Future<void> deleteSession(String id) async {
    await _client.delete('/api/chat/sessions/$id');
  }

  Future<void> streamMessage({
    required String sessionId,
    required String message,
    required void Function(ChatStreamEvent event) onEvent,
    bool regenerate = false,
    bool replaceLastExchange = false,
    bool enableWebSearch = true,
    bool ephemeral = false,
    CancelToken? cancelToken,
  }) async {
    final response = await _client.stream(
      '/api/chat/stream',
      cancelToken: cancelToken,
      data: {
        'sessionId': sessionId,
        'message': message,
        'regenerate': regenerate,
        'replaceLastExchange': replaceLastExchange,
        'enableWebSearch': enableWebSearch,
        'ephemeral': ephemeral,
      },
    );
    final body = response.data as ResponseBody;
    final parser = ChatSseParser();
    await for (final chunk in utf8.decoder.bind(body.stream)) {
      for (final event in parser.push(chunk)) {
        onEvent(event);
        if (event.type == 'done') return;
      }
    }
    for (final event in parser.finish()) {
      onEvent(event);
    }
  }
}
