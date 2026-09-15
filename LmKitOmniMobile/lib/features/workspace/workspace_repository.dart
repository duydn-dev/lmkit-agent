import 'package:dio/dio.dart';
import 'package:file_picker/file_picker.dart';

import '../../core/network/api_client.dart';
import 'workspace_models.dart';

class WorkspaceRepository {
  WorkspaceRepository(this._client);

  final ApiClient _client;

  Future<List<ProjectModel>> projects({
    int page = 1,
    int pageSize = 50,
    String? search,
  }) async {
    final response = await _client.get(
      '/api/projects',
      queryParameters: {
        'page': page,
        'pageSize': pageSize,
        if (search != null && search.trim().isNotEmpty) 'search': search.trim(),
      },
    );
    final data = Map<String, dynamic>.from(response.data as Map);
    final rows = data['items'] as List<dynamic>? ?? const [];
    return rows
        .whereType<Map<String, dynamic>>()
        .map(ProjectModel.fromJson)
        .toList();
  }

  Future<ProjectModel> createProject({
    required String name,
    String? description,
    String? icon,
    String? instructions,
  }) async {
    final response = await _client.post(
      '/api/projects',
      data: {
        'name': name,
        if (description?.trim().isNotEmpty == true)
          'description': description!.trim(),
        if (icon?.trim().isNotEmpty == true) 'icon': icon!.trim(),
        if (instructions?.trim().isNotEmpty == true)
          'instructions': instructions!.trim(),
      },
    );
    return ProjectModel.fromJson(
      Map<String, dynamic>.from(response.data as Map),
    );
  }

  Future<void> updateProject(
    String id, {
    required String name,
    String? description,
    String? icon,
    String? instructions,
  }) async {
    await _client.put(
      '/api/projects/$id',
      data: {
        'name': name,
        'description': description,
        'icon': icon,
        'instructions': instructions,
      },
    );
  }

  Future<void> deleteProject(String id) =>
      _client.delete('/api/projects/$id').then((_) {});

  Future<List<DocumentModel>> documents() async {
    final response = await _client.get('/api/document');
    final rows = response.data as List<dynamic>? ?? const [];
    return rows
        .whereType<Map<String, dynamic>>()
        .map(DocumentModel.fromJson)
        .toList();
  }

  Future<void> uploadDocument(PlatformFile file) async {
    if (file.path == null) throw StateError('Không thể đọc file đã chọn.');
    final form = FormData.fromMap({
      'file': await MultipartFile.fromFile(file.path!, filename: file.name),
    });
    await _client.upload('/api/document/upload', data: form);
  }

  Future<void> deleteDocument(String id) =>
      _client.delete('/api/document/$id').then((_) {});

  Future<List<MemoryModel>> memories() async {
    final response = await _client.get('/api/memory');
    final rows = response.data as List<dynamic>? ?? const [];
    return rows
        .whereType<Map<String, dynamic>>()
        .map(MemoryModel.fromJson)
        .toList();
  }

  Future<void> confirmMemory(String id) =>
      _client.post('/api/memory/$id/confirm').then((_) {});
  Future<void> forgetMemory(String id) =>
      _client.delete('/api/memory/$id').then((_) {});

  Future<CustomInstructionsModel> instructions() async {
    final response = await _client.get('/api/user/custom-instructions');
    return CustomInstructionsModel.fromJson(
      Map<String, dynamic>.from(response.data as Map),
    );
  }

  Future<CustomInstructionsModel> saveInstructions({
    String? aboutUser,
    String? responseStyle,
  }) async {
    final response = await _client.put(
      '/api/user/custom-instructions',
      data: {
        'aboutUser': aboutUser?.trim().isEmpty == true
            ? null
            : aboutUser?.trim(),
        'responseStyle': responseStyle?.trim().isEmpty == true
            ? null
            : responseStyle?.trim(),
      },
    );
    return CustomInstructionsModel.fromJson(
      Map<String, dynamic>.from(response.data as Map),
    );
  }
}
