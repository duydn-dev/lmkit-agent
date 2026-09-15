// ignore_for_file: use_null_aware_elements

import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:path_provider/path_provider.dart';

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
    return _sessions(response.data);
  }

  /// Tìm phiên chat theo tiêu đề hoặc nội dung tin nhắn.
  Future<List<ChatSessionModel>> searchSessions(String query) async {
    final response = await _client.get(
      '/api/chat/sessions/search',
      queryParameters: {'q': query.trim()},
    );
    return _sessions(response.data);
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
    String? customAgentId,
    bool ephemeral = false,
  }) async {
    final body = <String, dynamic>{
      if (projectId != null) 'projectId': projectId,
      if (customAgentId != null) 'customAgentId': customAgentId,
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

  /// Gửi tin nhắn không kèm file.
  ///
  /// [regenerate] chạy lại tin nhắn người dùng cuối; [replaceLastExchange]
  /// thay thế cặp hỏi–đáp cuối bằng [message] đã sửa.
  Future<void> streamMessage({
    required String sessionId,
    required String message,
    required void Function(ChatStreamEvent event) onEvent,
    bool regenerate = false,
    bool replaceLastExchange = false,
    bool enableWebSearch = true,
    bool ephemeral = false,
    CancelToken? cancelToken,
  }) => _consume(
    () => _client.stream(
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
    ),
    onEvent,
  );

  /// Gửi tin nhắn kèm file đính kèm (multipart, vẫn nhận SSE).
  ///
  /// Server OCR/chuyển đổi file rồi chèn nội dung vào context; [saveToKnowledge]
  /// quyết định có lưu bản trích xuất vào kho tri thức hay không.
  Future<void> streamWithFiles({
    required String sessionId,
    required String message,
    required List<ChatAttachmentModel> attachments,
    required void Function(ChatStreamEvent event) onEvent,
    bool saveToKnowledge = false,
    bool enableWebSearch = true,
    CancelToken? cancelToken,
  }) async {
    final files = <MultipartFile>[
      for (final attachment in attachments)
        await MultipartFile.fromFile(
          attachment.path,
          filename: attachment.name,
        ),
    ];
    final form = FormData.fromMap({
      'sessionId': sessionId,
      'message': message,
      'saveToKnowledge': saveToKnowledge,
      'enableWebSearch': enableWebSearch,
      'files': files,
    });
    return _consume(
      () => _client.stream(
        '/api/chat/stream-with-files',
        data: form,
        cancelToken: cancelToken,
      ),
      onEvent,
    );
  }

  /// Tạo (và luân chuyển) link chia sẻ công khai cho phiên chat.
  ///
  /// Token cũ bị thu hồi ngay khi token mới được sinh ra.
  Future<ShareLinkModel> createShareLink(String sessionId) async {
    final response = await _client.post('/api/share/chat-sessions/$sessionId');
    return ShareLinkModel.fromJson(
      Map<String, dynamic>.from(response.data as Map),
    );
  }

  Future<void> revokeShareLink(String sessionId) =>
      _client.delete('/api/share/chat-sessions/$sessionId').then((_) {});

  /// Tải file do agent tạo về thư mục tài liệu của app, trả về đường dẫn đã lưu.
  Future<String> downloadProducedFile(ProducedFileModel file) async {
    final directory = await getApplicationDocumentsDirectory();
    final target = '${directory.path}/${file.name}';
    await _client.download('/api/files/${file.id}', target);
    return target;
  }

  Future<void> _consume(
    Future<Response<dynamic>> Function() send,
    void Function(ChatStreamEvent event) onEvent,
  ) async {
    final response = await send();
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

  List<ChatSessionModel> _sessions(Object? data) {
    final rows = data is List ? data : const <dynamic>[];
    return rows
        .whereType<Map<String, dynamic>>()
        .map(ChatSessionModel.fromJson)
        .where((session) => session.id.isNotEmpty)
        .toList();
  }
}
