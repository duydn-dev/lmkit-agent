/// Trạng thái của một link chia sẻ khi truy vấn công khai.
enum SharedChatStatus { ok, notFound, revoked, expired, failed }

class SharedChatMessageModel {
  const SharedChatMessageModel({
    required this.role,
    required this.content,
    this.createdAt,
  });

  final String role;
  final String content;
  final DateTime? createdAt;

  bool get isUser => role.toLowerCase() == 'user';

  factory SharedChatMessageModel.fromJson(Map<String, dynamic> json) =>
      SharedChatMessageModel(
        role: json['role'] as String? ?? 'assistant',
        content: json['content'] as String? ?? '',
        createdAt: json['createdAt'] is String
            ? DateTime.tryParse(json['createdAt'] as String)
            : null,
      );
}

class SharedChatModel {
  const SharedChatModel({
    required this.title,
    this.createdAt,
    this.messages = const [],
  });

  final String title;
  final DateTime? createdAt;
  final List<SharedChatMessageModel> messages;

  factory SharedChatModel.fromJson(Map<String, dynamic> json) =>
      SharedChatModel(
        title: json['title'] as String? ?? '',
        createdAt: json['createdAt'] is String
            ? DateTime.tryParse(json['createdAt'] as String)
            : null,
        messages: (json['messages'] as List<dynamic>? ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(SharedChatMessageModel.fromJson)
            .toList(),
      );
}

/// Kết quả truy vấn link chia sẻ.
///
/// 404 = token không tồn tại; 410 = đã thu hồi hoặc hết hạn (kèm mốc thời gian).
class SharedChatResult {
  const SharedChatResult({
    required this.status,
    this.chat,
    this.refusedAtUtc,
    this.message,
  });

  final SharedChatStatus status;
  final SharedChatModel? chat;
  final DateTime? refusedAtUtc;
  final String? message;
}
