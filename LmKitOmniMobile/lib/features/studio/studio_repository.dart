import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:image_picker/image_picker.dart';

import '../../core/network/api_client.dart';
import '../chat/chat_models.dart';
import '../chat/chat_sse_parser.dart';
import 'studio_models.dart';

class StudioRepository {
  StudioRepository(this._client);

  final ApiClient _client;

  Future<List<CustomAgentModel>> customAgents({String? search}) async {
    final response = await _client.get(
      '/api/agents/custom',
      queryParameters: {
        'page': 1,
        'pageSize': 50,
        if (search != null && search.trim().isNotEmpty) 'search': search.trim(),
      },
    );
    return _paged(response.data).map(CustomAgentModel.fromJson).toList();
  }

  Future<List<AgentToolModel>> toolCatalog() async {
    final response = await _client.get('/api/agents/custom/tools');
    return _list(response.data).map(AgentToolModel.fromJson).toList();
  }

  /// Tài liệu do chính người dùng tải lên — chỉ những tài liệu này mới ghim
  /// được vào custom agent (backend kiểm tra owner-only).
  Future<List<KnowledgeDocModel>> ownedDocuments() async {
    final response = await _client.get(
      '/api/document',
      queryParameters: {'ownedOnly': true},
    );
    return _list(response.data).map(KnowledgeDocModel.fromJson).toList();
  }

  Future<CustomAgentModel> createCustomAgent({
    required String name,
    required String personaPrompt,
    String? description,
    String? icon,
    bool shared = false,
    List<String>? allowedTools,
    List<String>? knowledgeDocumentIds,
  }) async {
    final response = await _client.post(
      '/api/agents/custom',
      data: _agentBody(
        name: name,
        personaPrompt: personaPrompt,
        description: description,
        icon: icon,
        shared: shared,
        allowedTools: allowedTools,
        knowledgeDocumentIds: knowledgeDocumentIds,
      ),
    );
    return CustomAgentModel.fromJson(
      Map<String, dynamic>.from(response.data as Map),
    );
  }

  Future<CustomAgentModel> updateCustomAgent({
    required String id,
    required String name,
    required String personaPrompt,
    String? description,
    String? icon,
    bool shared = false,
    List<String>? allowedTools,
    List<String>? knowledgeDocumentIds,
  }) async {
    final response = await _client.put(
      '/api/agents/custom/$id',
      data: _agentBody(
        name: name,
        personaPrompt: personaPrompt,
        description: description,
        icon: icon,
        shared: shared,
        allowedTools: allowedTools,
        knowledgeDocumentIds: knowledgeDocumentIds,
      ),
    );
    return CustomAgentModel.fromJson(
      Map<String, dynamic>.from(response.data as Map),
    );
  }

  Map<String, dynamic> _agentBody({
    required String name,
    required String personaPrompt,
    String? description,
    String? icon,
    required bool shared,
    List<String>? allowedTools,
    List<String>? knowledgeDocumentIds,
  }) => {
    'name': name.trim(),
    'personaPrompt': personaPrompt.trim(),
    if (description?.trim().isNotEmpty == true)
      'description': description!.trim(),
    if (icon?.trim().isNotEmpty == true) 'icon': icon!.trim(),
    'isSharedWithTenant': shared,
    // null = giữ bộ công cụ mặc định theo vai trò; list = whitelist tường minh.
    'allowedTools': allowedTools,
    'knowledgeDocumentIds': knowledgeDocumentIds,
  };

  Future<void> deleteCustomAgent(String id) =>
      _client.delete('/api/agents/custom/$id').then((_) {});

  Future<AgentRunDetailModel> agentRun(String id) async {
    final response = await _client.get('/api/agent-runs/$id');
    return AgentRunDetailModel.fromJson(_map(response.data));
  }

  Future<List<ScheduledTaskModel>> schedules({String? search}) async {
    final response = await _client.get(
      '/api/schedules',
      queryParameters: {
        'page': 1,
        'pageSize': 50,
        if (search != null && search.trim().isNotEmpty) 'search': search.trim(),
      },
    );
    return _paged(response.data).map(ScheduledTaskModel.fromJson).toList();
  }

  Future<ScheduledTaskModel> createSchedule({
    required String name,
    required String prompt,
    required String scheduleKind,
    int? intervalMinutes,
    int? timeOfDayMinutes,
    int? dayOfWeek,
    DateTime? runAtUtc,
  }) async {
    final response = await _client.post(
      '/api/schedules',
      data: {
        'name': name.trim(),
        'prompt': prompt.trim(),
        'scheduleKind': scheduleKind,
        'intervalMinutes': ?intervalMinutes,
        'timeOfDayMinutes': ?timeOfDayMinutes,
        'dayOfWeek': ?dayOfWeek,
        'runAtUtc': ?runAtUtc?.toUtc().toIso8601String(),
      },
    );
    return ScheduledTaskModel.fromJson(
      Map<String, dynamic>.from(response.data as Map),
    );
  }

  Future<void> toggleSchedule(String id) =>
      _client.post('/api/schedules/$id/toggle').then((_) {});
  Future<void> deleteSchedule(String id) =>
      _client.delete('/api/schedules/$id').then((_) {});

  Future<List<AgentRunModel>> agentRuns({String? search}) async {
    final response = await _client.get(
      '/api/agent-runs',
      queryParameters: {
        'page': 1,
        'pageSize': 50,
        if (search != null && search.trim().isNotEmpty) 'search': search.trim(),
      },
    );
    return _paged(response.data).map(AgentRunModel.fromJson).toList();
  }

  Future<void> cancelAgentRun(String id) =>
      _client.post('/api/agent-runs/$id/cancel').then((_) {});

  /// Bắt đầu một agent run và đọc tiến trình qua SSE.
  ///
  /// `POST /api/agent-runs` trả về `text/event-stream` (không phải JSON): server
  /// chạy agent tự hành và đẩy `step`/`thinking`/`content`/`approval`/`done`.
  /// Huỷ [cancelToken] sẽ ngắt stream — đúng cách dừng theo backend (nút Dừng).
  Future<void> startAgentRun({
    required String goal,
    String? customAgentId,
    required void Function(ChatStreamEvent event) onEvent,
    CancelToken? cancelToken,
  }) async {
    final response = await _client.stream(
      '/api/agent-runs',
      data: {'goal': goal.trim(), 'customAgentId': ?customAgentId},
      cancelToken: cancelToken,
    );
    final parser = ChatSseParser();
    final body = response.data as ResponseBody;
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

  Future<void> runResearch({
    required String query,
    required int maxSources,
    required void Function(ChatStreamEvent event) onEvent,
    CancelToken? cancelToken,
  }) async {
    final response = await _client.stream(
      '/api/research',
      data: {'query': query.trim(), 'maxSources': maxSources},
      cancelToken: cancelToken,
    );
    final parser = ChatSseParser();
    final body = response.data as ResponseBody;
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

  Future<Map<String, dynamic>> analyzeText(String text) async =>
      _postMap('/api/textanalysis/analyze', {'text': text});

  Future<Map<String, dynamic>> classifyText(
    String text,
    List<String> categories,
  ) async => _postMap('/api/textanalysis/classify', {
    'text': text,
    'categories': categories,
  });

  Future<Map<String, dynamic>> detectLanguage(String text) async =>
      _postMap('/api/textanalysis/detect-language', {'text': text});

  Future<Map<String, dynamic>> extractKeywords(String text) async =>
      _postMap('/api/textanalysis/extract-keywords', {'text': text});

  Future<Map<String, dynamic>> embeddings(String text) async =>
      _postMap('/api/textanalysis/embeddings', {'text': text});

  Future<String> uploadVisionImage(XFile file) async {
    final form = FormData.fromMap({
      'image': await MultipartFile.fromFile(file.path, filename: file.name),
    });
    final response = await _client.upload('/api/vision/upload', data: form);
    return (response.data as Map)['imagePath']?.toString() ??
        (response.data as Map)['ImagePath']?.toString() ??
        '';
  }

  Future<Map<String, dynamic>> analyzeVision(
    String imagePath,
    String prompt,
  ) async => _postMap('/api/vision/analyze', {
    'imagePath': imagePath,
    'prompt': prompt,
  });

  Future<Map<String, dynamic>> ocrVision(String imagePath) async => _postMap(
    '/api/vision/ocr',
    {'imagePath': imagePath, 'includeCoordinates': false},
  );

  Future<Map<String, dynamic>> classifyVision(
    String imagePath,
    List<String> categories,
  ) async => _postMap('/api/vision/classify', {
    'imagePath': imagePath,
    'categories': categories,
  });

  Future<Map<String, dynamic>> removeVisionBackground(String imagePath) async =>
      _postMap('/api/vision/remove-background', {'imagePath': imagePath});

  Future<Map<String, dynamic>> createContent(String topic) async => _postMap(
    '/api/agents/content-creation-pipeline',
    {'topic': topic.trim()},
  );

  Future<Map<String, dynamic>> _postMap(String path, Object data) async {
    final response = await _client.post(path, data: data);
    return response.data is Map
        ? Map<String, dynamic>.from(response.data as Map)
        : <String, dynamic>{};
  }

  Future<List<Map<String, dynamic>>> pendingApprovals() async {
    final response = await _client.get('/api/taskapproval/pending');
    return _list(response.data);
  }

  Future<Map<String, dynamic>> approve(String id) async {
    final response = await _client.post('/api/taskapproval/$id/approve');
    return _map(response.data);
  }

  Future<void> reject(String id, {String? comment}) => _client
      .post('/api/taskapproval/$id/reject', data: {'comment': comment})
      .then((_) {});

  Future<List<Map<String, dynamic>>> apiKeys({String? search}) async {
    final response = await _client.get(
      '/api/api-keys',
      queryParameters: {
        'page': 1,
        'pageSize': 50,
        if (search != null && search.trim().isNotEmpty) 'search': search.trim(),
      },
    );
    return _paged(response.data);
  }

  Future<Map<String, dynamic>> createApiKey({
    required String name,
    int? expiresInDays,
    int? maxRequests,
  }) async {
    final response = await _client.post(
      '/api/api-keys',
      data: {
        'name': name.trim(),
        'expiresInDays': ?expiresInDays,
        'maxRequests': ?maxRequests,
      },
    );
    return _map(response.data);
  }

  Future<void> revokeApiKey(String id) =>
      _client.delete('/api/api-keys/$id').then((_) {});

  Future<List<Map<String, dynamic>>> notifications({
    bool unreadOnly = false,
  }) async {
    final response = await _client.get(
      '/api/notifications',
      queryParameters: {if (unreadOnly) 'unreadOnly': true},
    );
    return _list(response.data);
  }

  Future<void> markNotificationRead(String id) =>
      _client.post('/api/notifications/$id/read').then((_) {});
  Future<void> markAllNotificationsRead() =>
      _client.post('/api/notifications/read-all').then((_) {});

  static List<Map<String, dynamic>> _paged(Object? value) {
    if (value is! Map) return const [];
    final rows = value['items'];
    return rows is List
        ? rows
              .whereType<Map>()
              .map((row) => Map<String, dynamic>.from(row))
              .toList()
        : const [];
  }

  static List<Map<String, dynamic>> _list(Object? value) => value is List
      ? value
            .whereType<Map>()
            .map((row) => Map<String, dynamic>.from(row))
            .toList()
      : const [];

  static Map<String, dynamic> _map(Object? value) =>
      value is Map ? Map<String, dynamic>.from(value) : <String, dynamic>{};
}
