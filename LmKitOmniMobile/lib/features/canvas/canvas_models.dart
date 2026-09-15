/// Một artifact trong Canvas (bản mới nhất của mỗi root).
class CanvasArtifactModel {
  const CanvasArtifactModel({
    required this.id,
    required this.rootId,
    required this.title,
    required this.kind,
    this.language,
    this.version = 1,
    this.chatSessionId,
    this.updatedAt,
  });

  final String id;
  final String rootId;
  final String title;
  final String kind;
  final String? language;
  final int version;
  final String? chatSessionId;
  final DateTime? updatedAt;

  factory CanvasArtifactModel.fromJson(Map<String, dynamic> json) =>
      CanvasArtifactModel(
        id: json['id']?.toString() ?? '',
        rootId: json['rootId']?.toString() ?? '',
        title: json['title'] as String? ?? '',
        kind: json['kind'] as String? ?? 'text',
        language: json['language'] as String?,
        version: (json['version'] as num?)?.toInt() ?? 1,
        chatSessionId: json['chatSessionId']?.toString(),
        updatedAt: _date(json['updatedAt']),
      );

  static DateTime? _date(Object? value) =>
      value is String ? DateTime.tryParse(value) : null;
}

/// Nội dung đầy đủ của một artifact ở một phiên bản cụ thể.
class CanvasArtifactDetailModel {
  const CanvasArtifactDetailModel({
    required this.id,
    required this.rootId,
    required this.title,
    required this.kind,
    required this.content,
    this.language,
    this.version = 1,
    this.createdAt,
  });

  final String id;
  final String rootId;
  final String title;
  final String kind;
  final String content;
  final String? language;
  final int version;
  final DateTime? createdAt;

  factory CanvasArtifactDetailModel.fromJson(Map<String, dynamic> json) =>
      CanvasArtifactDetailModel(
        id: json['id']?.toString() ?? '',
        rootId: json['rootId']?.toString() ?? '',
        title: json['title'] as String? ?? '',
        kind: json['kind'] as String? ?? 'text',
        content: json['content'] as String? ?? '',
        language: json['language'] as String?,
        version: (json['version'] as num?)?.toInt() ?? 1,
        createdAt: json['createdAt'] is String
            ? DateTime.tryParse(json['createdAt'] as String)
            : null,
      );
}

/// Một mốc phiên bản trong lịch sử của artifact.
class CanvasVersionModel {
  const CanvasVersionModel({required this.version, this.createdAt});

  final int version;
  final DateTime? createdAt;

  factory CanvasVersionModel.fromJson(Map<String, dynamic> json) =>
      CanvasVersionModel(
        version: (json['version'] as num?)?.toInt() ?? 1,
        createdAt: json['createdAt'] is String
            ? DateTime.tryParse(json['createdAt'] as String)
            : null,
      );
}
