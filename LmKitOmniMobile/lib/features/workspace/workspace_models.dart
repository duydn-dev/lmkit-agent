class ProjectModel {
  const ProjectModel({
    required this.id,
    required this.name,
    this.description,
    this.icon,
    this.instructions,
    this.sessionCount = 0,
    this.createdAt,
    this.updatedAt,
  });

  final String id;
  final String name;
  final String? description;
  final String? icon;
  final String? instructions;
  final int sessionCount;
  final DateTime? createdAt;
  final DateTime? updatedAt;

  factory ProjectModel.fromJson(Map<String, dynamic> json) => ProjectModel(
    id: json['id']?.toString() ?? '',
    name: json['name'] as String? ?? '',
    description: json['description'] as String?,
    icon: json['icon'] as String?,
    instructions: json['instructions'] as String?,
    sessionCount: (json['sessionCount'] as num?)?.toInt() ?? 0,
    createdAt: _date(json['createdAt']),
    updatedAt: _date(json['updatedAt']),
  );

  static DateTime? _date(Object? value) =>
      value is String ? DateTime.tryParse(value) : null;
}

class DocumentModel {
  const DocumentModel({
    required this.id,
    required this.fileName,
    required this.isVectorized,
    required this.vectorizationStatus,
    this.uploadedAt,
    this.hasError = false,
  });

  final String id;
  final String fileName;
  final bool isVectorized;
  final String vectorizationStatus;
  final DateTime? uploadedAt;
  final bool hasError;

  factory DocumentModel.fromJson(Map<String, dynamic> json) => DocumentModel(
    id: json['id']?.toString() ?? '',
    fileName: json['fileName'] as String? ?? '',
    isVectorized: json['isVectorized'] as bool? ?? false,
    vectorizationStatus: json['vectorizationStatus'] as String? ?? 'Pending',
    uploadedAt: json['uploadedAt'] is String
        ? DateTime.tryParse(json['uploadedAt'] as String)
        : null,
    hasError: json['hasError'] as bool? ?? false,
  );
}

class MemoryModel {
  const MemoryModel({
    required this.id,
    required this.memoryType,
    required this.memoryKey,
    required this.memoryValue,
    required this.confidence,
    required this.isConfirmed,
    this.updatedAt,
  });

  final String id;
  final String memoryType;
  final String memoryKey;
  final String memoryValue;
  final double confidence;
  final bool isConfirmed;
  final DateTime? updatedAt;

  factory MemoryModel.fromJson(Map<String, dynamic> json) => MemoryModel(
    id: json['id']?.toString() ?? '',
    memoryType: json['memoryType'] as String? ?? '',
    memoryKey: json['memoryKey'] as String? ?? '',
    memoryValue: json['memoryValue'] as String? ?? '',
    confidence: (json['confidence'] as num?)?.toDouble() ?? 0,
    isConfirmed: json['isConfirmed'] as bool? ?? false,
    updatedAt: json['updatedAtUtc'] is String
        ? DateTime.tryParse(json['updatedAtUtc'] as String)
        : null,
  );
}

class CustomInstructionsModel {
  const CustomInstructionsModel({
    this.aboutUser,
    this.responseStyle,
    this.updatedAt,
  });

  final String? aboutUser;
  final String? responseStyle;
  final DateTime? updatedAt;

  factory CustomInstructionsModel.fromJson(Map<String, dynamic> json) =>
      CustomInstructionsModel(
        aboutUser: json['aboutUser'] as String?,
        responseStyle: json['responseStyle'] as String?,
        updatedAt: json['updatedAtUtc'] is String
            ? DateTime.tryParse(json['updatedAtUtc'] as String)
            : null,
      );
}
