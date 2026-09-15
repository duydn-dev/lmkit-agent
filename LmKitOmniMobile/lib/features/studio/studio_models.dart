class CustomAgentModel {
  const CustomAgentModel({
    required this.id,
    required this.name,
    this.description,
    this.icon,
    this.personaPrompt,
    this.allowedTools,
    this.isSharedWithTenant = false,
    this.isOwner = false,
  });

  final String id;
  final String name;
  final String? description;
  final String? icon;
  final String? personaPrompt;
  final List<String>? allowedTools;
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
