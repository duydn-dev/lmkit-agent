class CustomAgentModel {
  const CustomAgentModel({
    required this.id,
    required this.name,
    this.description,
    this.icon,
    this.personaPrompt,
    this.allowedTools,
    this.knowledgeDocumentIds = const [],
    this.loraAdapterId,
    this.isSharedWithTenant = false,
    this.isOwner = false,
  });

  final String id;
  final String name;
  final String? description;
  final String? icon;
  final String? personaPrompt;

  /// `null` = dùng bộ công cụ mặc định theo vai trò; có giá trị = whitelist.
  final List<String>? allowedTools;

  /// Tài liệu được ghim làm ngữ cảnh riêng cho agent này.
  final List<String> knowledgeDocumentIds;

  /// LoRA adapter đang gán cho agent (tham chiếu mềm).
  final String? loraAdapterId;
  final bool isSharedWithTenant;
  final bool isOwner;

  factory CustomAgentModel.fromJson(Map<String, dynamic> json) =>
      CustomAgentModel(
        id: json['id']?.toString() ?? '',
        name: json['name'] as String? ?? '',
        description: json['description'] as String?,
        icon: json['icon'] as String?,
        personaPrompt: json['personaPrompt'] as String?,
        allowedTools: (json['allowedTools'] as List<dynamic>?)
            ?.whereType<String>()
            .toList(),
        knowledgeDocumentIds:
            (json['knowledgeDocumentIds'] as List<dynamic>? ?? const [])
                .map((item) => item.toString())
                .toList(),
        loraAdapterId: json['loraAdapterId']?.toString(),
        isSharedWithTenant: json['isSharedWithTenant'] as bool? ?? false,
        isOwner: json['isOwner'] as bool? ?? false,
      );
}

class ScheduledTaskModel {
  const ScheduledTaskModel({
    required this.id,
    required this.name,
    required this.prompt,
    required this.scheduleKind,
    required this.enabled,
    required this.nextRunUtc,
    this.runMode = 'completion',
    this.intervalMinutes,
    this.timeOfDayMinutes,
    this.dayOfWeek,
    this.lastStatus,
    this.lastError,
  });

  final String id;
  final String name;
  final String prompt;
  final String scheduleKind;
  final bool enabled;
  final DateTime? nextRunUtc;
  final String runMode;
  final int? intervalMinutes;
  final int? timeOfDayMinutes;
  final int? dayOfWeek;
  final String? lastStatus;
  final String? lastError;

  factory ScheduledTaskModel.fromJson(Map<String, dynamic> json) =>
      ScheduledTaskModel(
        id: json['id']?.toString() ?? '',
        name: json['name'] as String? ?? '',
        prompt: json['prompt'] as String? ?? '',
        scheduleKind: json['scheduleKind'] as String? ?? 'once',
        enabled: json['enabled'] as bool? ?? false,
        nextRunUtc: _date(json['nextRunUtc']),
        runMode: json['runMode'] as String? ?? 'completion',
        intervalMinutes: (json['intervalMinutes'] as num?)?.toInt(),
        timeOfDayMinutes: (json['timeOfDayMinutes'] as num?)?.toInt(),
        dayOfWeek: (json['dayOfWeek'] as num?)?.toInt(),
        lastStatus: json['lastStatus'] as String?,
        lastError: json['lastError'] as String?,
      );

  static DateTime? _date(Object? value) =>
      value is String ? DateTime.tryParse(value) : null;
}

class AgentRunModel {
  const AgentRunModel({
    required this.id,
    required this.goal,
    required this.status,
    required this.stepCount,
    this.createdAtUtc,
    this.completedAtUtc,
  });

  final String id;
  final String goal;
  final String status;
  final int stepCount;
  final DateTime? createdAtUtc;
  final DateTime? completedAtUtc;

  factory AgentRunModel.fromJson(Map<String, dynamic> json) => AgentRunModel(
    id: json['id']?.toString() ?? '',
    goal: json['goal'] as String? ?? '',
    status: json['status'] as String? ?? '',
    stepCount: (json['stepCount'] as num?)?.toInt() ?? 0,
    createdAtUtc: _date(json['createdAtUtc']),
    completedAtUtc: _date(json['completedAtUtc']),
  );

  static DateTime? _date(Object? value) =>
      value is String ? DateTime.tryParse(value) : null;
}

/// Một công cụ agent được phép dùng (`GET /api/agents/custom/tools`).
class AgentToolModel {
  const AgentToolModel({
    required this.name,
    required this.label,
    required this.description,
  });

  final String name;
  final String label;
  final String description;

  factory AgentToolModel.fromJson(Map<String, dynamic> json) => AgentToolModel(
    name: json['name'] as String? ?? '',
    label: json['label'] as String? ?? '',
    description: json['description'] as String? ?? '',
  );
}

/// Tài liệu có thể ghim vào custom agent.
///
/// Chỉ tài liệu do chính người dùng tải lên mới ghim được, nên picker dùng
/// `?ownedOnly=true`.
class KnowledgeDocModel {
  const KnowledgeDocModel({
    required this.id,
    required this.fileName,
    this.isVectorized = false,
  });

  final String id;
  final String fileName;
  final bool isVectorized;

  factory KnowledgeDocModel.fromJson(Map<String, dynamic> json) =>
      KnowledgeDocModel(
        id: json['id']?.toString() ?? '',
        fileName: json['fileName'] as String? ?? '',
        isVectorized: json['isVectorized'] as bool? ?? false,
      );
}

class AgentRunStepModel {
  const AgentRunStepModel({
    required this.ordinal,
    required this.action,
    required this.input,
    required this.observation,
    this.createdAtUtc,
  });

  final int ordinal;
  final String action;
  final String input;
  final String observation;
  final DateTime? createdAtUtc;

  factory AgentRunStepModel.fromJson(Map<String, dynamic> json) =>
      AgentRunStepModel(
        ordinal: (json['ordinal'] as num?)?.toInt() ?? 0,
        action: json['action'] as String? ?? '',
        input: json['input'] as String? ?? '',
        observation: json['observation'] as String? ?? '',
        createdAtUtc: json['createdAtUtc'] is String
            ? DateTime.tryParse(json['createdAtUtc'] as String)
            : null,
      );
}

/// Chi tiết một agent run kèm log từng bước.
class AgentRunDetailModel {
  const AgentRunDetailModel({
    required this.id,
    required this.goal,
    required this.status,
    this.result,
    this.error,
    this.createdAtUtc,
    this.completedAtUtc,
    this.steps = const [],
  });

  final String id;
  final String goal;
  final String status;
  final String? result;
  final String? error;
  final DateTime? createdAtUtc;
  final DateTime? completedAtUtc;
  final List<AgentRunStepModel> steps;

  bool get isRunning =>
      status.toLowerCase() == 'running' || status.toLowerCase() == 'pending';

  factory AgentRunDetailModel.fromJson(Map<String, dynamic> json) =>
      AgentRunDetailModel(
        id: json['id']?.toString() ?? '',
        goal: json['goal'] as String? ?? '',
        status: json['status'] as String? ?? '',
        result: json['result'] as String?,
        error: json['error'] as String?,
        createdAtUtc: json['createdAtUtc'] is String
            ? DateTime.tryParse(json['createdAtUtc'] as String)
            : null,
        completedAtUtc: json['completedAtUtc'] is String
            ? DateTime.tryParse(json['completedAtUtc'] as String)
            : null,
        steps: (json['steps'] as List<dynamic>? ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(AgentRunStepModel.fromJson)
            .toList(),
      );
}
