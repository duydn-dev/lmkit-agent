import 'package:flutter_test/flutter_test.dart';
import 'package:lmkit_omni_mobile/features/admin/quota_admin_models.dart';

/// Payload thật của `/api/admin/quota/*`. Test ở đây ghim hai thứ dễ vỡ khi server đổi DTO:
/// quy ước "0 = không giới hạn" và việc chấp nhận field thiếu (app cũ gặp server mới).
void main() {
  group('PlanModel', () {
    test('doc duoc goi khong gioi han tu monthlyTokenLimit = 0', () {
      final plan = PlanModel.fromJson(const {
        'id': 'p1',
        'name': 'Gói nội bộ',
        'monthlyTokenLimit': 0,
        'isActive': true,
        'tenantCount': 3,
      });

      expect(plan.name, 'Gói nội bộ');
      // 0 là gói KHÔNG GIỚI HẠN, không phải gói 0 token — hiển thị sai chỗ này là gán nhầm hạn mức.
      expect(plan.isUnlimited, isTrue);
      expect(plan.tenantCount, 3);
      expect(plan.isActive, isTrue);
    });

    test('goi co tran thi khong phai khong gioi han', () {
      final plan = PlanModel.fromJson(const {
        'id': 'p2',
        'name': 'Gói 1K',
        'monthlyTokenLimit': 1000,
        'isActive': false,
        'tenantCount': 0,
      });

      expect(plan.isUnlimited, isFalse);
      expect(plan.isActive, isFalse);
    });

    test('thieu field thi ve 0/false chu khong nem', () {
      final plan = PlanModel.fromJson(const {'id': 'p3'});

      expect(plan.name, '');
      expect(plan.monthlyTokenLimit, 0);
      expect(plan.tenantCount, 0);
    });
  });

  group('TenantQuotaModel', () {
    test('doc day du trang thai han muc cua mot don vi', () {
      final tenant = TenantQuotaModel.fromJson(const {
        'tenantId': 't1',
        'tenantName': 'Cục Trồng trọt',
        'subscriptionId': 's1',
        'planId': 'p1',
        'planName': 'Gói 2K',
        'monthlyTokenLimit': 2000,
        'renewalAtUtc': '2026-11-07T00:00:00Z',
        'usedTokens': 1800,
        'utilizationPct': 90,
        'isUnlimited': false,
        'creditBalance': 250,
        'activeGrantCount': 2,
        'grantRemainingTokens': 1500,
      });

      expect(tenant.tenantName, 'Cục Trồng trọt');
      expect(tenant.hasPlan, isTrue);
      expect(tenant.planName, 'Gói 2K');
      expect(tenant.usedTokens, 1800);
      expect(tenant.utilizationPct, 90);
      expect(tenant.renewalAtUtc!.toUtc().year, 2026);
      expect(tenant.grantRemainingTokens, 1500);
    });

    test('don vi chua gan goi: planId/planName null va hasPlan false', () {
      final tenant = TenantQuotaModel.fromJson(const {
        'tenantId': 't2',
        'tenantName': 'Đơn vị mới',
        'planId': null,
        'planName': null,
        'isUnlimited': true,
      });

      expect(tenant.hasPlan, isFalse);
      expect(tenant.planName, isNull);
      expect(tenant.isUnlimited, isTrue);
      expect(tenant.usedTokens, 0);
      expect(tenant.creditBalance, 0);
    });
  });

  group('GrantModel', () {
    test('chi grant chua tieu dong nao moi go duoc', () {
      final unused = GrantModel.fromJson(const {
        'id': 'g1',
        'tokens': 5000,
        'usedTokens': 0,
        'remainingTokens': 5000,
      });
      final spent = GrantModel.fromJson(const {
        'id': 'g2',
        'tokens': 5000,
        'usedTokens': 400,
        'remainingTokens': 4600,
      });

      // Server chặn gỡ grant đã tiêu (xoá nó là xoá lịch sử chi tiêu) — UI khoá nút trước
      // để người dùng không bấm rồi nhận lỗi.
      expect(unused.canBeRemoved, isTrue);
      expect(spent.canBeRemoved, isFalse);
    });

    test('grant het han va ly do doc duoc', () {
      final grant = GrantModel.fromJson(const {
        'id': 'g3',
        'tokens': 900,
        'usedTokens': 0,
        'remainingTokens': 900,
        'expiresAtUtc': '2026-10-01T00:00:00Z',
        'reason': 'bù hạn mức',
        'isExpired': true,
      });

      expect(grant.isExpired, isTrue);
      expect(grant.reason, 'bù hạn mức');
      expect(grant.expiresAtUtc, isNotNull);
    });

    test('grant vo han thi expiresAtUtc null', () {
      final grant = GrantModel.fromJson(const {
        'id': 'g4',
        'tokens': 100,
        'usedTokens': 0,
        'remainingTokens': 100,
        'expiresAtUtc': null,
      });

      expect(grant.expiresAtUtc, isNull);
      expect(grant.isExpired, isFalse);
    });
  });

  group('quotaRows', () {
    test('nhan ca list thang lan envelope co items', () {
      expect(quotaRows([const {'id': 'a'}]), hasLength(1));
      expect(quotaRows(const {
        'items': [
          {'id': 'a'},
        ],
      }), hasLength(1));
    });

    test('du lieu la thi tra rong chu khong nem', () {
      expect(quotaRows(null), isEmpty);
      expect(quotaRows('không phải list'), isEmpty);
      expect(quotaRows(const {'totalCount': 0}), isEmpty);
    });
  });
}
