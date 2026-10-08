import 'package:flutter/foundation.dart';

/// Model cho dashboard vận hành (`GET /api/dashboard/stats`).
///
/// Khớp hợp đồng của web: bốn con số cấp hệ thống cho MỌI vai trò, khối [cockpit] CHỈ có
/// với Admin (server trả `null` cho Member nên app tự ẩn phần biểu đồ, không đoán quyền ở
/// client), và [myUsage] luôn có cho chính người đang đăng nhập.
///
/// Số token là ƯỚC LƯỢNG (đếm ký tự / token) ghi lúc lưu lượt trả lời; row tạo trước khi
/// có tính năng này mang token 0 nên vẫn được đếm là "lượt trả lời" nhưng không góp token.
@immutable
class DashboardStats {
  const DashboardStats({
    required this.periodDays,
    required this.totalUsers,
    required this.totalTenants,
    required this.totalSessions,
    required this.totalDocuments,
    required this.myUsage,
    this.cockpit,
  });

  final int periodDays;
  final int totalUsers;
  final int totalTenants;
  final int totalSessions;
  final int totalDocuments;
  final DashboardSelf myUsage;
  final DashboardCockpit? cockpit;

  factory DashboardStats.fromJson(Map<String, dynamic> json) => DashboardStats(
    periodDays: dashInt(json['periodDays'], fallback: 30),
    totalUsers: dashInt(json['totalUsers']),
    totalTenants: dashInt(json['totalTenants']),
    totalSessions: dashInt(json['totalSessions']),
    totalDocuments: dashInt(json['totalDocuments']),
    myUsage: DashboardSelf.fromJson(dashMap(json['myUsage'])),
    cockpit: json['cockpit'] is Map
        ? DashboardCockpit.fromJson(dashMap(json['cockpit']))
        : null,
  );
}

/// Số liệu của chính người đang đăng nhập — có với mọi vai trò.
@immutable
class DashboardSelf {
  const DashboardSelf({
    required this.questions,
    required this.answers,
    required this.promptTokens,
    required this.completionTokens,
    required this.sessions,
  });

  final int questions;
  final int answers;
  final int promptTokens;
  final int completionTokens;
  final int sessions;

  int get totalTokens => promptTokens + completionTokens;

  factory DashboardSelf.fromJson(Map<String, dynamic> json) => DashboardSelf(
    questions: dashInt(json['questions']),
    answers: dashInt(json['answers']),
    promptTokens: dashInt(json['promptTokens']),
    completionTokens: dashInt(json['completionTokens']),
    sessions: dashInt(json['sessions']),
  );
}

@immutable
class DashboardCockpit {
  const DashboardCockpit({
    required this.tokens,
    required this.users,
    required this.spend,
    required this.quota,
    required this.documents,
    required this.activity,
    required this.performance,
    required this.alerts,
  });

  final DashboardTokens tokens;
  final DashboardUsers users;
  final DashboardSpend spend;
  final DashboardQuota quota;
  final DashboardDocuments documents;
  final DashboardActivity activity;
  final DashboardPerformance performance;
  final DashboardAlerts alerts;

  factory DashboardCockpit.fromJson(Map<String, dynamic> json) =>
      DashboardCockpit(
        tokens: DashboardTokens.fromJson(dashMap(json['tokens'])),
        users: DashboardUsers.fromJson(dashMap(json['users'])),
        spend: DashboardSpend.fromJson(dashMap(json['spend'])),
        quota: DashboardQuota.fromJson(dashMap(json['quota'])),
        documents: DashboardDocuments.fromJson(dashMap(json['documents'])),
        activity: DashboardActivity.fromJson(dashMap(json['activity'])),
        performance: DashboardPerformance.fromJson(dashMap(json['performance'])),
        alerts: DashboardAlerts.fromJson(dashMap(json['alerts'])),
      );
}

@immutable
class DashboardTokens {
  const DashboardTokens({
    required this.promptTokens,
    required this.completionTokens,
    required this.messages,
    required this.agentRunPromptTokens,
    required this.agentRunCompletionTokens,
    required this.agentRuns,
    required this.daily,
    required this.byModel,
  });

  final int promptTokens;
  final int completionTokens;
  final int messages;

  /// Token của các lần chạy AGENT trong kỳ, tách khỏi lượt chat: một lần chạy gồm nhiều lượt
  /// suy luận nối tiếp nên tốn gấp nhiều lần một lượt chat, gộp chung sẽ giấu mất nguồn chi phí
  /// lớn nhất. Cộng dồn mọi lần chạy lại sau phê duyệt.
  final int agentRunPromptTokens;
  final int agentRunCompletionTokens;

  /// Số lần chạy THẬT SỰ gọi model (lần bị hàng đợi từ chối không tính).
  final int agentRuns;

  final List<DashboardDailyToken> daily;
  final List<DashboardModelUsage> byModel;

  int get totalTokens => promptTokens + completionTokens;

  int get totalAgentRunTokens => agentRunPromptTokens + agentRunCompletionTokens;

  factory DashboardTokens.fromJson(Map<String, dynamic> json) =>
      DashboardTokens(
        promptTokens: dashInt(json['promptTokens']),
        completionTokens: dashInt(json['completionTokens']),
        messages: dashInt(json['messages']),
        agentRunPromptTokens: dashInt(json['agentRunPromptTokens']),
        agentRunCompletionTokens: dashInt(json['agentRunCompletionTokens']),
        agentRuns: dashInt(json['agentRuns']),
        daily: dashList(json['daily'], DashboardDailyToken.fromJson),
        byModel: dashList(json['byModel'], DashboardModelUsage.fromJson),
      );
}

@immutable
class DashboardDailyToken {
  const DashboardDailyToken({
    required this.date,
    required this.promptTokens,
    required this.completionTokens,
    required this.messages,
  });

  final String date;
  final int promptTokens;
  final int completionTokens;
  final int messages;

  int get totalTokens => promptTokens + completionTokens;

  factory DashboardDailyToken.fromJson(Map<String, dynamic> json) =>
      DashboardDailyToken(
        date: dashString(json['date']),
        promptTokens: dashInt(json['promptTokens']),
        completionTokens: dashInt(json['completionTokens']),
        messages: dashInt(json['messages']),
      );
}

@immutable
class DashboardModelUsage {
  const DashboardModelUsage({
    required this.modelName,
    required this.promptTokens,
    required this.completionTokens,
    required this.messages,
  });

  final String modelName;
  final int promptTokens;
  final int completionTokens;
  final int messages;

  int get totalTokens => promptTokens + completionTokens;

  factory DashboardModelUsage.fromJson(Map<String, dynamic> json) =>
      DashboardModelUsage(
        modelName: dashString(json['modelName']),
        promptTokens: dashInt(json['promptTokens']),
        completionTokens: dashInt(json['completionTokens']),
        messages: dashInt(json['messages']),
      );
}

@immutable
class DashboardUsers {
  const DashboardUsers({
    required this.dailyActive,
    required this.weeklyActive,
    required this.monthlyActive,
    required this.totalUsers,
    required this.newUsers,
    required this.adoptionPct,
    required this.daily,
    required this.topUsers,
  });

  /// DAU/WAU/MAU luôn tính trên 1/7/30 ngày gần nhất, không theo kỳ đang chọn.
  final int dailyActive;
  final int weeklyActive;
  final int monthlyActive;
  final int totalUsers;
  final int newUsers;
  final int adoptionPct;
  final List<DashboardDailyCount> daily;
  final List<DashboardTopUser> topUsers;

  factory DashboardUsers.fromJson(Map<String, dynamic> json) => DashboardUsers(
    dailyActive: dashInt(json['dailyActive']),
    weeklyActive: dashInt(json['weeklyActive']),
    monthlyActive: dashInt(json['monthlyActive']),
    totalUsers: dashInt(json['totalUsers']),
    newUsers: dashInt(json['newUsers']),
    adoptionPct: dashInt(json['adoptionPct']),
    daily: dashList(json['daily'], DashboardDailyCount.fromJson),
    topUsers: dashList(json['topUsers'], DashboardTopUser.fromJson),
  );
}

@immutable
class DashboardTopUser {
  const DashboardTopUser({
    required this.userId,
    required this.name,
    required this.email,
    required this.questions,
    required this.answers,
    required this.promptTokens,
    required this.completionTokens,
  });

  final String userId;
  final String name;
  final String email;
  final int questions;
  final int answers;
  final int promptTokens;
  final int completionTokens;

  int get totalTokens => promptTokens + completionTokens;

  factory DashboardTopUser.fromJson(Map<String, dynamic> json) =>
      DashboardTopUser(
        userId: dashString(json['userId']),
        name: dashString(json['name']),
        email: dashString(json['email']),
        questions: dashInt(json['questions']),
        answers: dashInt(json['answers']),
        promptTokens: dashInt(json['promptTokens']),
        completionTokens: dashInt(json['completionTokens']),
      );
}

@immutable
class DashboardSpend {
  const DashboardSpend({
    required this.totalTokens,
    required this.top3SharePct,
    required this.topTenantSharePct,
    required this.topTenantName,
    required this.byTenant,
    this.totalAgentRunTokens = 0,
  });

  /// Token của LƯỢT CHAT trong kỳ (không gồm agent-run).
  final int totalTokens;

  /// Token của agent-run trong kỳ.
  final int totalAgentRunTokens;

  final int top3SharePct;
  final int topTenantSharePct;
  final String topTenantName;
  final List<DashboardTenantUsage> byTenant;

  /// Mẫu số dùng CHUNG với backend: backend tính "top 3 chiếm bao nhiêu %" trên tổng token
  /// chat + agent-run, nên thanh % ở đây cũng phải cộng cả hai — lệch mẫu số là thanh dài hơn
  /// con số ngay cạnh nó.
  int sharePct(DashboardTenantUsage usage) {
    final total = totalTokens + totalAgentRunTokens;
    if (total <= 0) return 0;
    return ((usage.allTokens * 100) / total).round();
  }

  factory DashboardSpend.fromJson(Map<String, dynamic> json) => DashboardSpend(
    totalTokens: dashInt(json['totalTokens']),
    totalAgentRunTokens: dashInt(json['totalAgentRunTokens']),
    top3SharePct: dashInt(json['top3SharePct']),
    topTenantSharePct: dashInt(json['topTenantSharePct']),
    topTenantName: dashString(json['topTenantName']),
    byTenant: dashList(json['byTenant'], DashboardTenantUsage.fromJson),
  );
}

@immutable
class DashboardTenantUsage {
  const DashboardTenantUsage({
    required this.tenantId,
    required this.tenantName,
    required this.promptTokens,
    required this.completionTokens,
    required this.messages,
    this.agentRunPromptTokens = 0,
    this.agentRunCompletionTokens = 0,
    this.agentRuns = 0,
  });

  final String tenantId;
  final String tenantName;
  final int promptTokens;
  final int completionTokens;
  final int messages;

  /// Phần agent-run của đơn vị, để riêng khỏi lượt chat.
  final int agentRunPromptTokens;
  final int agentRunCompletionTokens;
  final int agentRuns;

  int get totalTokens => promptTokens + completionTokens;

  int get agentTokens => agentRunPromptTokens + agentRunCompletionTokens;

  /// Tổng chi phí AI của đơn vị trong kỳ (chat + agent-run).
  int get allTokens => totalTokens + agentTokens;

  factory DashboardTenantUsage.fromJson(Map<String, dynamic> json) =>
      DashboardTenantUsage(
        tenantId: dashString(json['tenantId']),
        tenantName: dashString(json['tenantName']),
        promptTokens: dashInt(json['promptTokens']),
        completionTokens: dashInt(json['completionTokens']),
        messages: dashInt(json['messages']),
        agentRunPromptTokens: dashInt(json['agentRunPromptTokens']),
        agentRunCompletionTokens: dashInt(json['agentRunCompletionTokens']),
        agentRuns: dashInt(json['agentRuns']),
      );
}

@immutable
class DashboardQuota {
  const DashboardQuota({
    required this.tenantsOnPlan,
    required this.tenantsOverThreshold,
    required this.tenantsOverLimit,
    required this.tenantsWithoutPlan,
    required this.totalMonthlyLimit,
    required this.totalMonthlyUsed,
    required this.totalCreditBalance,
    required this.byTenant,
  });

  final int tenantsOnPlan;
  final int tenantsOverThreshold;
  final int tenantsOverLimit;
  final int tenantsWithoutPlan;
  final int totalMonthlyLimit;
  final int totalMonthlyUsed;
  final int totalCreditBalance;
  final List<DashboardTenantQuota> byTenant;

  /// Chỉ đơn vị có gói VÀ có hạn mức thật mới có % để so — gói không giới hạn thì không.
  List<DashboardTenantQuota> get limited =>
      byTenant.where((t) => !t.isUnlimited).toList(growable: false);

  factory DashboardQuota.fromJson(Map<String, dynamic> json) => DashboardQuota(
    tenantsOnPlan: dashInt(json['tenantsOnPlan']),
    tenantsOverThreshold: dashInt(json['tenantsOverThreshold']),
    tenantsOverLimit: dashInt(json['tenantsOverLimit']),
    tenantsWithoutPlan: dashInt(json['tenantsWithoutPlan']),
    totalMonthlyLimit: dashInt(json['totalMonthlyLimit']),
    totalMonthlyUsed: dashInt(json['totalMonthlyUsed']),
    totalCreditBalance: dashInt(json['totalCreditBalance']),
    byTenant: dashList(json['byTenant'], DashboardTenantQuota.fromJson),
  );
}

@immutable
class DashboardTenantQuota {
  const DashboardTenantQuota({
    required this.tenantId,
    required this.tenantName,
    required this.planName,
    required this.monthlyLimit,
    required this.usedTokens,
    required this.utilizationPct,
    required this.creditBalance,
    required this.isUnlimited,
  });

  final String tenantId;
  final String tenantName;
  final String? planName;
  final int monthlyLimit;

  /// Token đã dùng của THÁNG DƯƠNG LỊCH hiện tại — hạn mức gói reset theo tháng nên con
  /// số này không phụ thuộc kỳ 7/30/90 đang chọn.
  final int usedTokens;
  final int utilizationPct;
  final int creditBalance;
  final bool isUnlimited;

  factory DashboardTenantQuota.fromJson(Map<String, dynamic> json) =>
      DashboardTenantQuota(
        tenantId: dashString(json['tenantId']),
        tenantName: dashString(json['tenantName']),
        planName: json['planName'] is String ? json['planName'] as String : null,
        monthlyLimit: dashInt(json['monthlyLimit']),
        usedTokens: dashInt(json['usedTokens']),
        utilizationPct: dashInt(json['utilizationPct']),
        creditBalance: dashInt(json['creditBalance']),
        isUnlimited: json['isUnlimited'] == true,
      );
}

@immutable
class DashboardDocuments {
  const DashboardDocuments({
    required this.total,
    required this.indexed,
    required this.pending,
    required this.failed,
    required this.totalChunks,
    required this.byStatus,
  });

  final int total;
  final int indexed;
  final int pending;
  final int failed;
  final int totalChunks;
  final List<DashboardCount> byStatus;

  int get indexRatePct => total <= 0 ? 0 : ((indexed * 100) / total).round();

  factory DashboardDocuments.fromJson(Map<String, dynamic> json) =>
      DashboardDocuments(
        total: dashInt(json['total']),
        indexed: dashInt(json['indexed']),
        pending: dashInt(json['pending']),
        failed: dashInt(json['failed']),
        totalChunks: dashInt(json['totalChunks']),
        byStatus: dashList(json['byStatus'], DashboardCount.fromJson),
      );
}

@immutable
class DashboardCount {
  const DashboardCount({required this.key, required this.count});

  final String key;
  final int count;

  factory DashboardCount.fromJson(Map<String, dynamic> json) =>
      DashboardCount(key: dashString(json['key']), count: dashInt(json['count']));
}

@immutable
class DashboardActivity {
  const DashboardActivity({
    required this.total,
    required this.topActions,
  });

  final int total;
  final List<DashboardCount> topActions;

  factory DashboardActivity.fromJson(Map<String, dynamic> json) =>
      DashboardActivity(
        total: dashInt(json['total']),
        topActions: dashList(json['topActions'], DashboardCount.fromJson),
      );
}

@immutable
class DashboardPerformance {
  const DashboardPerformance({
    required this.samples,
    required this.avgLatencyMs,
    required this.p95LatencyMs,
    this.agentRunSamples = 0,
    this.agentRunAvgLatencyMs = 0,
    this.agentRunP95LatencyMs = 0,
  });

  /// Số mẫu có ghi độ trễ; 0 nghĩa là chưa lượt nào được đo.
  final int samples;
  final int avgLatencyMs;
  final int p95LatencyMs;

  /// Độ trễ của các lần chạy agent, đo riêng: một lần chạy gồm nhiều lượt suy luận cộng lại.
  final int agentRunSamples;
  final int agentRunAvgLatencyMs;
  final int agentRunP95LatencyMs;

  factory DashboardPerformance.fromJson(Map<String, dynamic> json) =>
      DashboardPerformance(
        samples: dashInt(json['samples']),
        avgLatencyMs: dashInt(json['avgLatencyMs']),
        p95LatencyMs: dashInt(json['p95LatencyMs']),
        agentRunSamples: dashInt(json['agentRunSamples']),
        agentRunAvgLatencyMs: dashInt(json['agentRunAvgLatencyMs']),
        agentRunP95LatencyMs: dashInt(json['agentRunP95LatencyMs']),
      );
}

@immutable
class DashboardAlerts {
  const DashboardAlerts({
    required this.nearLimitTenants,
    required this.overLimitTenants,
    required this.expiringGrantCount,
    required this.expiringGrants,
  });

  final int nearLimitTenants;
  final int overLimitTenants;
  final int expiringGrantCount;
  final List<DashboardExpiringGrant> expiringGrants;

  factory DashboardAlerts.fromJson(Map<String, dynamic> json) => DashboardAlerts(
    nearLimitTenants: dashInt(json['nearLimitTenants']),
    overLimitTenants: dashInt(json['overLimitTenants']),
    expiringGrantCount: dashInt(json['expiringGrantCount']),
    expiringGrants: dashList(
      json['expiringGrants'],
      DashboardExpiringGrant.fromJson,
    ),
  );
}

@immutable
class DashboardExpiringGrant {
  const DashboardExpiringGrant({
    required this.tenantId,
    required this.tenantName,
    required this.remainingTokens,
    required this.daysLeft,
  });

  final String tenantId;
  final String tenantName;
  final int remainingTokens;
  final int daysLeft;

  factory DashboardExpiringGrant.fromJson(Map<String, dynamic> json) =>
      DashboardExpiringGrant(
        tenantId: dashString(json['tenantId']),
        tenantName: dashString(json['tenantName']),
        remainingTokens: dashInt(json['remainingTokens']),
        daysLeft: dashInt(json['daysLeft']),
      );
}

@immutable
class DashboardDailyCount {
  const DashboardDailyCount({required this.date, required this.count});

  final String date;
  final int count;

  factory DashboardDailyCount.fromJson(Map<String, dynamic> json) =>
      DashboardDailyCount(
        date: dashString(json['date']),
        count: dashInt(json['count']),
      );
}

/// Các mốc thời gian backend chấp nhận cho `?days=` (đồng bộ DashboardPeriod.AllowedDays).
const dashboardPeriodDays = <int>[7, 30, 90];

int dashInt(Object? value, {int fallback = 0}) => switch (value) {
  int v => v,
  double v => v.toInt(),
  String v => int.tryParse(v) ?? fallback,
  _ => fallback,
};

String dashString(Object? value) => value is String ? value : '';

Map<String, dynamic> dashMap(Object? value) =>
    value is Map ? Map<String, dynamic>.from(value) : <String, dynamic>{};

List<T> dashList<T>(Object? value, T Function(Map<String, dynamic>) build) {
  if (value is! List) return const [];
  return value
      .whereType<Map>()
      .map((row) => build(Map<String, dynamic>.from(row)))
      .toList();
}

/// Định dạng số theo kiểu Việt Nam (dấu chấm phân cách nghìn) cho mọi con số trên màn.
String dashNumber(int value) {
  final digits = value.abs().toString();
  final buffer = StringBuffer();
  for (var i = 0; i < digits.length; i++) {
    if (i > 0 && (digits.length - i) % 3 == 0) buffer.write('.');
    buffer.write(digits[i]);
  }
  return value < 0 ? '-$buffer' : buffer.toString();
}
