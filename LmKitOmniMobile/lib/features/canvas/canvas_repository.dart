// ignore_for_file: use_null_aware_elements

import '../../core/network/api_client.dart';
import 'canvas_models.dart';

/// API `/api/canvas`: artifact sinh ra trong phiên chat, có versioning.
class CanvasRepository {
  CanvasRepository(this._client);

  final ApiClient _client;

  /// Artifact mới nhất của từng root, lọc theo phiên chat nếu truyền vào.
  Future<List<CanvasArtifactModel>> artifacts({String? sessionId}) async {
    final response = await _client.get(
      '/api/canvas',
      queryParameters: {
        if (sessionId != null && sessionId.isNotEmpty) 'sessionId': sessionId,
      },
    );
    return _rows(response.data).map(CanvasArtifactModel.fromJson).toList();
  }

  /// Nội dung artifact ở một phiên bản; bỏ trống [version] để lấy bản mới nhất.
  Future<CanvasArtifactDetailModel> artifact({
    required String rootId,
    int? version,
  }) async {
    final response = await _client.get(
      '/api/canvas/$rootId',
      queryParameters: {if (version != null) 'version': version},
    );
    return CanvasArtifactDetailModel.fromJson(
      Map<String, dynamic>.from(response.data as Map),
    );
  }

  Future<List<CanvasVersionModel>> versions(String rootId) async {
    final response = await _client.get('/api/canvas/$rootId/versions');
    return _rows(response.data).map(CanvasVersionModel.fromJson).toList();
  }

  Future<CanvasArtifactModel> create({
    required String content,
    String? sessionId,
    String? title,
    String? kind,
    String? language,
  }) async {
    final response = await _client.post(
      '/api/canvas',
      data: {
        if (sessionId != null && sessionId.isNotEmpty)
          'chatSessionId': sessionId,
        if (title != null && title.trim().isNotEmpty) 'title': title.trim(),
        if (kind != null && kind.trim().isNotEmpty) 'kind': kind,
        if (language != null && language.trim().isNotEmpty)
          'language': language,
        'content': content,
      },
    );
    return CanvasArtifactModel.fromJson(
      Map<String, dynamic>.from(response.data as Map),
    );
  }

  /// Lưu phiên bản mới. Bỏ trống [title] để giữ tiêu đề cũ.
  Future<void> update({
    required String rootId,
    required String content,
    String? title,
  }) => _client
      .put(
        '/api/canvas/$rootId',
        data: {
          if (title != null && title.trim().isNotEmpty) 'title': title.trim(),
          'content': content,
        },
      )
      .then((_) {});

  Future<void> delete(String rootId) =>
      _client.delete('/api/canvas/$rootId').then((_) {});

  static List<Map<String, dynamic>> _rows(Object? value) {
    final raw = value is Map ? value['items'] : value;
    if (raw is! List) return const [];
    return raw
        .whereType<Map>()
        .map((row) => Map<String, dynamic>.from(row))
        .toList();
  }
}
