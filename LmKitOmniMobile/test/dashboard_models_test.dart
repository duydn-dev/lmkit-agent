import 'package:flutter_test/flutter_test.dart';
import 'package:lmkit_omni_mobile/features/admin/dashboard_models.dart';

/// Payload Admin đầy đủ (rút gọn) — mọi khối phải parse được, kể cả khi server thêm field.
Map<String, dynamic> _adminPayload() => <String, dynamic>{
  'periodDays': 7,
  'totalUsers': 5,
  'totalTenants': 4,
  'totalSessions': 8,
  'totalDocuments': 4,
  'myUsage': {
    'questions': 1,
    'answers': 2,
    'promptTokens': 111,
    'completionTokens': 222,
    'sessions': 3,
    // Trường server thêm sau này không được làm hỏng parse.
    'futureField': 'x',
  },
  'cockpit': {
    'periodDays': 7,
    'tokens': {
      'promptTokens': 1611,
      'completionTokens': 972,
      'messages': 6,
      'daily': [
        {'date': '2026-10-07', 'promptTokens': 100, 'completionTokens': 50, 'messages': 1},
        {'date': '2026-10-08', 'promptTokens': 1511, 'completionTokens': 922, 'messages': 5},
      ],
      'byModel': [
        {'modelName': 'gemma4:e4b', 'promptTokens': 811, 'completionTokens': 572, 'messages': 4},
        {'modelName': '(unknown)', 'promptTokens': 500, 'completionTokens': 250, 'messages': 1},
      ],
    },
    'users': {
      'dailyActive': 2,
      'weeklyActive': 4,
      'monthlyActive': 4,
      'totalUsers': 5,
      'newUsers': 3,
      'adoptionPct': 67,
      'daily': [
        {'date': '2026-10-08', 'count': 2},
      ],
      'topUsers': [
        {
          'userId': 'aaaaaaaa-0000-0000-0000-000000000001',
          'name': 'Nguyễn Văn A',
          'email': 'u1@t1.local',
          'questions': 1,
          'answers': 3,
          'promptTokens': 600,
          'completionTokens': 300,
        },
      ],
    },
    'spend': {
      'totalTokens': 2583,
      'top3SharePct': 100,
      'topTenantSharePct': 64,
      'topTenantName': 'Cục Trồng trọt',
      'byTenant': [
        {
          'tenantId': '11111111-1111-1111-1111-111111111111',
          'tenantName': 'Cục Trồng trọt',
          'promptTokens': 1100,
          'completionTokens': 550,
          'messages': 4,
        },
      ],
    },
    'quota': {
      'tenantsOnPlan': 2,
      'tenantsOverThreshold': 0,
      'tenantsOverLimit': 2,
      'tenantsWithoutPlan': 2,
      'totalMonthlyLimit': 1600,
      'totalMonthlyUsed': 2250,
      'totalCreditBalance': 213155,
      'byTenant': [
        {
          'tenantId': '11111111-1111-1111-1111-111111111111',
          'tenantName': 'Cục Trồng trọt',
          'planName': 'Gói 1K',
          'monthlyLimit': 1500,
          'usedTokens': 1650,
          'utilizationPct': 110,
          'creditBalance': 200000,
          'isUnlimited': false,
        },
        {
          'tenantId': '99999999-1111-1111-1111-111111111111',
          'tenantName': 'Đơn vị chưa gán gói',
          'planName': null,
          'monthlyLimit': 0,
          'usedTokens': 0,
          'utilizationPct': 0,
          'creditBalance': 0,
          'isUnlimited': true,
        },
      ],
    },
    'documents': {
      'total': 4,
      'indexed': 2,
      'pending': 1,
      'failed': 1,
      'totalChunks': 3,
      'byStatus': [
        {'key': 'Completed', 'count': 2},
      ],
    },
    'activity': {
      'total': 10,
      'daily': [
        {'date': '2026-10-08', 'count': 6},
      ],
      'topActions': [
        {'key': 'Added', 'count': 9},
      ],
    },
    'performance': {'samples': 5, 'avgLatencyMs': 3400, 'p95LatencyMs': 4000},
    'alerts': {
      'nearLimitTenants': 0,
      'overLimitTenants': 2,
      'expiringGrantCount': 1,
      'expiringGrants': [
        {
          'tenantId': '22222222-2222-2222-2222-222222222222',
          'tenantName': 'Cục Thủy sản',
          'remainingTokens': 4000,
          'expiresAtUtc': '2026-10-13T00:00:00Z',
          'daysLeft': 5,
        },
      ],
    },
  },
};

void main() {
  group('DashboardStats', () {
    test('parse đủ khối cockpit cho Admin', () {
      final stats = DashboardStats.fromJson(_adminPayload());

      expect(stats.periodDays, 7);
      expect(stats.totalUsers, 5);
      expect(stats.totalTenants, 4);
      expect(stats.myUsage.totalTokens, 333);

      final cockpit = stats.cockpit;
      expect(cockpit, isNotNull);
      expect(cockpit!.tokens.totalTokens, 2583);
      expect(cockpit.tokens.messages, 6);
      expect(cockpit.tokens.daily.length, 2);
      expect(cockpit.tokens.byModel.first.modelName, 'gemma4:e4b');
      expect(cockpit.users.topUsers.single.name, 'Nguyễn Văn A');
      expect(cockpit.performance.p95LatencyMs, 4000);
      expect(cockpit.alerts.expiringGrants.single.remainingTokens, 4000);
      expect(cockpit.alerts.expiringGrants.single.daysLeft, 5);
    });

    test('Member: cockpit null nhưng myUsage vẫn có', () {
      final stats = DashboardStats.fromJson(<String, dynamic>{
        'periodDays': 30,
        'totalUsers': 5,
        'totalTenants': 4,
        'totalSessions': 8,
        'totalDocuments': 4,
        'cockpit': null,
        'myUsage': {'questions': 4, 'answers': 3, 'sessions': 2},
      });

      // Server cắt khối biểu đồ cho Member — app phải tôn trọng đúng cờ đó, không tự
      // dựng số liệu thay thế.
      expect(stats.cockpit, isNull);
      expect(stats.myUsage.questions, 4);
      expect(stats.myUsage.totalTokens, 0);
      expect(stats.periodDays, 30);
    });

    test('payload thiếu khối con không ném lỗi', () {
      final stats = DashboardStats.fromJson(<String, dynamic>{
        'cockpit': <String, dynamic>{'tokens': <String, dynamic>{}},
      });

      expect(stats.cockpit, isNotNull);
      expect(stats.cockpit!.tokens.totalTokens, 0);
      expect(stats.cockpit!.users.topUsers, isEmpty);
      expect(stats.cockpit!.quota.byTenant, isEmpty);
      // periodDays mặc định 30 khi server không gửi.
      expect(stats.periodDays, 30);
    });
  });

  group('số liệu dẫn xuất', () {
    test('quota.limited loại đơn vị không giới hạn', () {
      final cockpit = DashboardStats.fromJson(_adminPayload()).cockpit!;
      expect(cockpit.quota.byTenant.length, 2);
      expect(cockpit.quota.limited.length, 1);
      expect(cockpit.quota.limited.single.tenantName, 'Cục Trồng trọt');
    });

    test('spend.sharePct tính trên tổng kỳ, không chia cho 0', () {
      final cockpit = DashboardStats.fromJson(_adminPayload()).cockpit!;
      final tenant = cockpit.spend.byTenant.single;
      // 1650/2583 = 63.9% -> 64
      expect(cockpit.spend.sharePct(tenant), 64);

      const empty = DashboardSpend(
        totalTokens: 0,
        top3SharePct: 0,
        topTenantSharePct: 0,
        topTenantName: '',
        byTenant: [],
      );
      expect(
        empty.sharePct(
          const DashboardTenantUsage(
            tenantId: 'x',
            tenantName: 'x',
            promptTokens: 5,
            completionTokens: 5,
            messages: 1,
          ),
        ),
        0,
      );
    });

    test('documents.indexRatePct không chia cho 0', () {
      final cockpit = DashboardStats.fromJson(_adminPayload()).cockpit!;
      expect(cockpit.documents.indexRatePct, 50);

      const empty = DashboardDocuments(
        total: 0,
        indexed: 0,
        pending: 0,
        failed: 0,
        totalChunks: 0,
        byStatus: [],
      );
      expect(empty.indexRatePct, 0);
    });
  });

  group('dashNumber', () {
    test('phân cách nghìn kiểu Việt Nam', () {
      expect(dashNumber(0), '0');
      expect(dashNumber(999), '999');
      expect(dashNumber(1000), '1.000');
      expect(dashNumber(213155), '213.155');
      expect(dashNumber(2583000), '2.583.000');
      expect(dashNumber(-1500), '-1.500');
    });
  });

  group('dashboardPeriodDays', () {
    test('đúng các mốc backend chấp nhận', () {
      expect(dashboardPeriodDays, <int>[7, 30, 90]);
    });
  });
}
