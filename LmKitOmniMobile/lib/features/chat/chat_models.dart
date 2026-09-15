import 'dart:convert';

class ChatSessionModel {
  const ChatSessionModel({
    required this.id,
    required this.title,
    required this.createdAt,
    this.customAgentId,
    this.agentName,
    this.agentIcon,
    this.projectId,
    this.isEphemeral = false,
  });

  final String id;
  final String? title;
  final DateTime? createdAt;
  final String? customAgentId;
  final String? agentName;
  final String? agentIcon;
  final String? projectId;
  final bool isEphemeral;

  factory ChatSessionModel.fromJson(Map<String, dynamic> json) =>
      ChatSessionModel(
        id: json['id']?.toString() ?? '',
        title: json['title'] as String?,
        createdAt: _parseDate(json['createdAt']),
        customAgentId: json['customAgentId']?.toString(),
        agentName: json['agentName'] as String?,
        agentIcon: json['agentIcon'] as String?,
        projectId: json['projectId']?.toString(),
        isEphemeral: json['isEphemeral'] as bool? ?? false,
      );

  static DateTime? _parseDate(Object? value) =>
      value is String ? DateTime.tryParse(value) : null;
}

class ProducedFileModel {
  const ProducedFileModel({
    required this.id,
    required this.name,
    required this.contentType,
    required this.size,
  });

  final String id;
  final String name;
  final String contentType;
  final int size;

  factory ProducedFileModel.fromJson(Map<String, dynamic> json) =>
      ProducedFileModel(
        id: json['id']?.toString() ?? '',
        name: json['name'] as String? ?? json['id']?.toString() ?? 'file',
        contentType:
            json['contentType'] as String? ?? 'application/octet-stream',
        size: (json['size'] as num?)?.toInt() ?? 0,
      );
}

class ChatMessageModel {
  ChatMessageModel({
    required this.role,
    required this.content,
    this.id,
    this.createdAt,
    this.thinkingSteps = const [],
    this.reasoning = '',
    this.webUrls = const [],
    this.producedFiles = const [],
    this.approvalId,
    this.isTyping = false,
  });

  final String? id;
  final String role;
  String content;
  final DateTime? createdAt;
  final List<String> thinkingSteps;
  String reasoning;
  final List<String> webUrls;
  final List<ProducedFileModel> producedFiles;
  String? approvalId;
  bool isTyping;

  bool get isUser => role.toLowerCase() == 'user';
  bool get isAssistant => role.toLowerCase() == 'assistant';

  factory ChatMessageModel.fromJson(Map<String, dynamic> json) {
    final parsed = parseStoredContent(json['content'] as String? ?? '');
    return ChatMessageModel(
      id: json['id']?.toString(),
      role: json['role'] as String? ?? 'assistant',
      content: parsed.content,
      createdAt: json['createdAt'] is String
          ? DateTime.tryParse(json['createdAt'] as String)
          : null,
      thinkingSteps: parsed.thinkingSteps,
      reasoning: parsed.reasoning,
      webUrls: parsed.webUrls,
      producedFiles: parsed.files,
    );
  }

  ChatMessageModel copy() => ChatMessageModel(
    id: id,
    role: role,
    content: content,
    createdAt: createdAt,
    thinkingSteps: List<String>.from(thinkingSteps),
    reasoning: reasoning,
    webUrls: List<String>.from(webUrls),
    producedFiles: List<ProducedFileModel>.from(producedFiles),
    approvalId: approvalId,
    isTyping: isTyping,
  );
}

class ParsedStoredContent {
  const ParsedStoredContent({
    required this.content,
    this.thinkingSteps = const [],
    this.reasoning = '',
    this.webUrls = const [],
    this.files = const [],
  });

  final String content;
  final List<String> thinkingSteps;
  final String reasoning;
  final List<String> webUrls;
  final List<ProducedFileModel> files;
}

ParsedStoredContent parseStoredContent(String raw) {
  var content = raw.replaceAll(RegExp(r'\[Agent invoked:.*?\][\n\r]*'), '');
  final files = <ProducedFileModel>[];
  final fileMatches = RegExp(r'\[FILE:(.+?)\]').allMatches(content).toList();
  for (final match in fileMatches) {
    try {
      final json = match.group(1);
      if (json == null) continue;
      final value = Map<String, dynamic>.from(_decodeJson(json));
      final file = ProducedFileModel.fromJson(value);
      if (file.id.isNotEmpty) files.add(file);
    } catch (_) {
      // Keep malformed protocol markers out of the visible answer.
    }
  }
  content = content.replaceAll(RegExp(r'\[FILE:.+?\][\n\r]*'), '');

  final thinkingSteps = RegExp(r'\[THINKING\]:([^\n\r]+)')
      .allMatches(content)
      .map((match) => _cleanThinking(match.group(1) ?? ''))
      .where(
        (value) =>
            value.isNotEmpty && !value.contains('Agent đang suy luận...'),
      )
      .toList();
  content = content.replaceAll(RegExp(r'\[THINKING\]:[^\n\r]+[\n\r]*'), '');

  final reasoning = RegExp(
    r'\[REASONING\]:([^\n\r]+)',
  ).allMatches(content).map((match) => match.group(1) ?? '').join('\n').trim();
  content = content.replaceAll(RegExp(r'\[REASONING\]:[^\n\r]+[\n\r]*'), '');

  final searchMatch = RegExp(r'\[WEB_SEARCH\]:([^\n\r]+)').firstMatch(content);
  final webUrls =
      searchMatch?.group(1)?.split('|').where(_isSafeUrl).toList() ??
      <String>[];
  content = content.replaceAll(RegExp(r'\[WEB_SEARCH\]:[^\n\r]+[\n\r]*'), '');

  return ParsedStoredContent(
    content: content.trimLeft(),
    thinkingSteps: thinkingSteps,
    reasoning: reasoning,
    webUrls: webUrls,
    files: files,
  );
}

Map<String, dynamic> _decodeJson(String value) {
  return jsonDecode(value) as Map<String, dynamic>;
}

String _cleanThinking(String value) => value
    .replaceAll(RegExp(r'[\u{1F300}-\u{1FAFF}\uFE0F\u200D]', unicode: true), '')
    .replaceAll(RegExp(r'\s{2,}'), ' ')
    .trim();

bool _isSafeUrl(String value) {
  final uri = Uri.tryParse(value);
  return uri != null && (uri.scheme == 'http' || uri.scheme == 'https');
}

class ChatStreamEvent {
  const ChatStreamEvent(this.type, this.value);

  final String type;
  final String value;
}
