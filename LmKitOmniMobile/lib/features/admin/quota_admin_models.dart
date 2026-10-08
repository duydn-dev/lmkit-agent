// Model cho màn quản trị hạn mức (/api/admin/quota). Parse theo kiểu chịu được field lạ và
// field thiếu: server có thể thêm cột mà app cũ vẫn phải mở được màn này.

String _string(Object? value) =>
    value is String ? value : (value?.toString() ?? '');

String? _nullableString(Object? value) {
  final text = _string(value).trim();
  return text.isEmpty ? null : text;
}

int _int(Object? value) {
  if (value is int) return value;
  if (value is num) return value.toInt();
  return int.tryParse('${value ?? ''}') ?? 0;
}

DateTime? _dateTime(Object? value) {
  final text = _string(value).trim();
  if (text.isEmpty) return null;
  return DateTime.tryParse(text);
}

/// Gói hạn mức. `monthlyTokenLimit == 0` là KHÔNG GIỚI HẠN (gói nội bộ), không phải gói 0 token.
class PlanModel {
  const PlanModel({
    required this.id,
    required this.name,
    required this.monthlyTokenLimit,
    required this.isActive,
    required this.tenantCount,
  });

  final String id;
  final String name;
  final int monthlyTokenLimit;
  final bool isActive;

  /// Số đơn vị ĐANG dùng gói này — dùng để biết vì sao chưa ngừng dùng được gói.
  final int tenantCount;

  bool get isUnlimited => monthlyTokenLimit <= 0;

  factory PlanModel.fromJson(Map<String, dynamic> json) => PlanModel(
    id: _string(json['id']),
    name: _string(json['name']),
    monthlyTokenLimit: _int(json['monthlyTokenLimit']),
    isActive: json['isActive'] == true,
    tenantCount: _int(json['tenantCount']),
  );
}

/// Trạng thái hạn mức của một đơn vị. `usedTokens` gồm CẢ lượt chat lẫn lần chạy agent.
class TenantQuotaModel {
  const TenantQuotaModel({
    required this.tenantId,
    required this.tenantName,
    required this.planId,
    required this.planName,
    required this.monthlyTokenLimit,
    required this.renewalAtUtc,
    required this.usedTokens,
    required this.utilizationPct,
    required this.isUnlimited,
    required this.creditBalance,
    required this.activeGrantCount,
    required this.grantRemainingTokens,
  });

  final String tenantId;
  final String tenantName;
  final String? planId;
  final String? planName;
  final int monthlyTokenLimit;
  final DateTime? renewalAtUtc;
  final int usedTokens;
  final int utilizationPct;
  final bool isUnlimited;
  final int creditBalance;
  final int activeGrantCount;
  final int grantRemainingTokens;

  bool get hasPlan => planId != null;

  factory TenantQuotaModel.fromJson(Map<String, dynamic> json) =>
      TenantQuotaModel(
        tenantId: _string(json['tenantId']),
        tenantName: _string(json['tenantName']),
        planId: _nullableString(json['planId']),
        planName: _nullableString(json['planName']),
        monthlyTokenLimit: _int(json['monthlyTokenLimit']),
        renewalAtUtc: _dateTime(json['renewalAtUtc']),
        usedTokens: _int(json['usedTokens']),
        utilizationPct: _int(json['utilizationPct']),
        isUnlimited: json['isUnlimited'] == true,
        creditBalance: _int(json['creditBalance']),
        activeGrantCount: _int(json['activeGrantCount']),
        grantRemainingTokens: _int(json['grantRemainingTokens']),
      );
}

/// Token cấp thêm có thời hạn. `isExpired` do server tính (đã quá hạn → phần còn lại không dùng được).
class GrantModel {
  const GrantModel({
    required this.id,
    required this.tokens,
    required this.usedTokens,
    required this.remainingTokens,
    required this.expiresAtUtc,
    required this.reason,
    required this.isExpired,
  });

  final String id;
  final int tokens;
  final int usedTokens;
  final int remainingTokens;
  final DateTime? expiresAtUtc;
  final String? reason;
  final bool isExpired;

  /// Grant đã tiêu một phần không gỡ được (server chặn) — vô hiệu hoá nút gỡ thay vì để
  /// người dùng bấm rồi nhận lỗi.
  bool get canBeRemoved => usedTokens == 0;

  factory GrantModel.fromJson(Map<String, dynamic> json) => GrantModel(
    id: _string(json['id']),
    tokens: _int(json['tokens']),
    usedTokens: _int(json['usedTokens']),
    remainingTokens: _int(json['remainingTokens']),
    expiresAtUtc: _dateTime(json['expiresAtUtc']),
    reason: _nullableString(json['reason']),
    isExpired: json['isExpired'] == true,
  );
}

/// Endpoint trả list thẳng; hàm này chấp nhận cả list lẫn envelope có `items` để không vỡ khi
/// server đổi hình dạng phân trang.
List<Map<String, dynamic>> quotaRows(Object? value) {
  final raw = value is Map ? value['items'] : value;
  if (raw is! List) return const [];
  return raw.whereType<Map>().map(Map<String, dynamic>.from).toList();
}
