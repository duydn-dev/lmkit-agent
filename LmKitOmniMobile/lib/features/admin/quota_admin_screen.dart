import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';
import '../../app/ui/app_controls.dart';
import 'admin_provider.dart';
import 'admin_repository.dart';
import 'admin_widgets.dart';
import 'quota_admin_models.dart';

/// Quản trị hạn mức trên mobile: gói, gán gói cho đơn vị, token cấp thêm (grant) và số dư.
///
/// Đây là màn thay cho việc insert SQL tay vào Plans/Subscriptions/TokenGrants/TenantCredits.
///
/// Hai lựa chọn thiết kế đáng nói:
/// * Kỳ gia hạn và hạn của grant chọn theo LÔ ngày ("30 ngày", "90 ngày"…) thay vì mở lịch.
///   Ngoài việc gọn hơn trên điện thoại, `showDatePicker` của Material không tra được tổ tiên
///   `Material` bên trong hộp thoại Forui nên sẽ ném lỗi — đã gặp thật với các widget Material
///   khác trong hộp thoại của app.
/// * Hành động của từng dòng nằm trong [AppMenuButton] (menu ba chấm) thay vì một loạt nút, để
///   mỗi dòng vẫn đọc được trên màn hình hẹp.
class QuotaAdminScreen extends ConsumerStatefulWidget {
  const QuotaAdminScreen({super.key});

  @override
  ConsumerState<QuotaAdminScreen> createState() => _QuotaAdminScreenState();
}

class _QuotaAdminScreenState extends ConsumerState<QuotaAdminScreen> {
  bool _loading = true;
  String? _error;
  List<PlanModel> _plans = const [];
  List<TenantQuotaModel> _tenants = const [];

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final repository = ref.read(adminRepositoryProvider);
      final plans = await repository.quotaPlans();
      final tenants = await repository.tenantQuotas();
      if (!mounted) return;
      setState(() {
        _plans = plans;
        _tenants = tenants;
        _loading = false;
      });
    } catch (error) {
      if (!mounted) return;
      setState(() {
        _error = adminErrorMessage(error);
        _loading = false;
      });
    }
  }

  // ------------------------------------------------------------------ tiện ích

  /// 0 là "không giới hạn" (gói nội bộ), không phải gói 0 token.
  String _limitLabel(int limit) => limit > 0
      ? '${_number(limit)} token/tháng'
      : 'Không giới hạn';

  String _number(int value) {
    final text = value.abs().toString();
    final buffer = StringBuffer(value < 0 ? '-' : '');
    for (var index = 0; index < text.length; index++) {
      if (index > 0 && (text.length - index) % 3 == 0) buffer.write('.');
      buffer.write(text[index]);
    }
    return buffer.toString();
  }

  String _date(DateTime value) {
    final local = value.toLocal();
    final day = local.day.toString().padLeft(2, '0');
    final month = local.month.toString().padLeft(2, '0');
    return '$day/$month/${local.year}';
  }

  /// Mốc hạn theo số ngày kể từ bây giờ. Đặt CUỐI ngày để "hôm nay + 0" vẫn không rơi vào quá
  /// khứ — server từ chối mọi mốc không ở tương lai.
  DateTime _horizon(int days) {
    final now = DateTime.now().add(Duration(days: days));
    return DateTime(now.year, now.month, now.day, 23, 59, 59);
  }

  void _fail(Object error) {
    if (!mounted) return;
    showAdminSnack(context, adminErrorMessage(error));
  }

  // ------------------------------------------------------------------ gói

  Future<void> _openPlanDialog({PlanModel? plan}) async {
    final name = TextEditingController(text: plan?.name ?? '');
    final limit = TextEditingController(text: '${plan?.monthlyTokenLimit ?? 0}');
    var isActive = plan?.isActive ?? true;

    final saved = await showAppDialog<bool>(
      context,
      builder: (dialogContext) => AppDialog(
        title: plan == null ? 'Thêm gói' : 'Sửa gói',
        content: StatefulBuilder(
          builder: (context, setDialogState) => Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              AdminField(controller: name, label: 'Tên gói'),
              AdminField(
                controller: limit,
                label: 'Hạn mức token mỗi tháng',
                hint: '0 = không giới hạn',
                keyboardType: TextInputType.number,
              ),
              AppSelectTile<bool>(
                label: 'Trạng thái',
                value: isActive,
                items: const [true, false],
                labelOf: (value) => value ? 'Đang dùng' : 'Đã ngừng',
                helper: 'Gói đã ngừng sẽ không gán được cho đơn vị.',
                onChanged: (value) => setDialogState(() => isActive = value),
              ),
            ],
          ),
        ),
        actions: [
          AppSecondaryButton(
            label: 'Huỷ',
            onPressed: () => Navigator.pop(dialogContext, false),
          ),
          AppPrimaryButton(
            label: 'Lưu',
            expand: false,
            onPressed: () => Navigator.pop(dialogContext, true),
          ),
        ],
      ),
    );

    if (saved != true || !mounted) return;

    final trimmed = name.text.trim();
    if (trimmed.isEmpty) {
      showAdminSnack(context, 'Vui lòng nhập tên gói.');
      return;
    }
    final parsedLimit = int.tryParse(limit.text.trim());
    if (parsedLimit == null || parsedLimit < 0) {
      showAdminSnack(context, 'Hạn mức phải là số không âm (0 = không giới hạn).');
      return;
    }

    try {
      final repository = ref.read(adminRepositoryProvider);
      if (plan == null) {
        await repository.createQuotaPlan(
          name: trimmed,
          monthlyTokenLimit: parsedLimit,
          isActive: isActive,
        );
      } else {
        await repository.updateQuotaPlan(
          id: plan.id,
          name: trimmed,
          monthlyTokenLimit: parsedLimit,
          isActive: isActive,
        );
      }
      if (!mounted) return;
      showAdminSnack(context, plan == null ? 'Đã thêm gói.' : 'Đã lưu gói.');
      await _load();
    } catch (error) {
      _fail(error);
    }
  }

  Future<void> _deactivatePlan(PlanModel plan) async {
    final confirmed = await confirmAdminAction(
      context,
      title: 'Ngừng dùng gói',
      message:
          'Ngừng dùng gói "${plan.name}"? Các đơn vị phải được chuyển sang gói khác trước, '
          'nếu không server sẽ từ chối.',
    );
    if (confirmed != true || !mounted) return;

    try {
      await ref.read(adminRepositoryProvider).deactivateQuotaPlan(plan.id);
      if (!mounted) return;
      showAdminSnack(context, 'Đã ngừng dùng gói.');
      await _load();
    } catch (error) {
      _fail(error);
    }
  }

  // ------------------------------------------------------------------ gán gói

  Future<void> _assignPlan(TenantQuotaModel tenant) async {
    final assignable = _plans.where((plan) => plan.isActive).toList();
    if (assignable.isEmpty) {
      showAdminSnack(context, 'Chưa có gói nào đang dùng để gán.');
      return;
    }

    var planId = tenant.planId != null && assignable.any((p) => p.id == tenant.planId)
        ? tenant.planId!
        : assignable.first.id;
    // 0 = không đặt kỳ hạn (gói chạy tới khi đổi).
    var renewalDays = 0;

    final saved = await showAppDialog<bool>(
      context,
      builder: (dialogContext) => AppDialog(
        title: 'Gán gói cho ${tenant.tenantName}',
        content: StatefulBuilder(
          builder: (context, setDialogState) => Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              AppSelectTile<String>(
                label: 'Gói',
                value: planId,
                items: assignable.map((plan) => plan.id).toList(),
                labelOf: (id) => assignable
                    .firstWhere((plan) => plan.id == id, orElse: () => assignable.first)
                    .name,
                subtitleOf: (id) => _limitLabel(
                  assignable
                      .firstWhere((plan) => plan.id == id, orElse: () => assignable.first)
                      .monthlyTokenLimit,
                ),
                onChanged: (value) => setDialogState(() => planId = value),
              ),
              AppSelectTile<int>(
                label: 'Kỳ gia hạn',
                value: renewalDays,
                items: const [0, 30, 90, 365],
                labelOf: (days) => days == 0 ? 'Không đặt kỳ hạn' : '$days ngày nữa',
                helper: 'Bỏ trống nghĩa là gói chạy tới khi được đổi.',
                onChanged: (value) => setDialogState(() => renewalDays = value),
              ),
            ],
          ),
        ),
        actions: [
          AppSecondaryButton(
            label: 'Huỷ',
            onPressed: () => Navigator.pop(dialogContext, false),
          ),
          AppPrimaryButton(
            label: 'Gán gói',
            expand: false,
            onPressed: () => Navigator.pop(dialogContext, true),
          ),
        ],
      ),
    );

    if (saved != true || !mounted) return;

    try {
      await ref.read(adminRepositoryProvider).assignQuotaPlan(
        tenantId: tenant.tenantId,
        planId: planId,
        renewalAtUtc: renewalDays == 0 ? null : _horizon(renewalDays),
      );
      if (!mounted) return;
      showAdminSnack(context, 'Đã gán gói cho ${tenant.tenantName}.');
      await _load();
    } catch (error) {
      _fail(error);
    }
  }

  Future<void> _removePlan(TenantQuotaModel tenant) async {
    final confirmed = await confirmAdminAction(
      context,
      title: 'Gỡ gói',
      message: 'Gỡ gói khỏi "${tenant.tenantName}"? Đơn vị sẽ không còn hạn mức tháng.',
    );
    if (confirmed != true || !mounted) return;

    try {
      await ref.read(adminRepositoryProvider).removeQuotaPlan(tenant.tenantId);
      if (!mounted) return;
      showAdminSnack(context, 'Đã gỡ gói.');
      await _load();
    } catch (error) {
      _fail(error);
    }
  }

  // ------------------------------------------------------------------ số dư

  Future<void> _openCreditDialog(TenantQuotaModel tenant) async {
    final balance = TextEditingController(text: '${tenant.creditBalance}');

    final saved = await showAppDialog<bool>(
      context,
      builder: (dialogContext) => AppDialog(
        title: 'Số dư token — ${tenant.tenantName}',
        content: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            AdminField(
              controller: balance,
              label: 'Số dư',
              hint: 'Số dư mua trước, không reset theo tháng',
              keyboardType: TextInputType.number,
            ),
          ],
        ),
        actions: [
          AppSecondaryButton(
            label: 'Huỷ',
            onPressed: () => Navigator.pop(dialogContext, false),
          ),
          AppPrimaryButton(
            label: 'Lưu',
            expand: false,
            onPressed: () => Navigator.pop(dialogContext, true),
          ),
        ],
      ),
    );

    if (saved != true || !mounted) return;

    final parsed = int.tryParse(balance.text.trim());
    if (parsed == null || parsed < 0) {
      showAdminSnack(context, 'Số dư phải là số không âm.');
      return;
    }

    try {
      await ref
          .read(adminRepositoryProvider)
          .setQuotaCredit(tenantId: tenant.tenantId, balance: parsed);
      if (!mounted) return;
      showAdminSnack(context, 'Đã lưu số dư.');
      await _load();
    } catch (error) {
      _fail(error);
    }
  }

  // ------------------------------------------------------------------ grant

  Future<void> _openGrantDialog(TenantQuotaModel tenant) async {
    // Nạp grant TRƯỚC khi mở hộp thoại. Nhờ vậy bên trong hộp thoại không cần vòng xoay —
    // `CircularProgressIndicator` là widget chỉ Material mới dựng được, và
    // test/material_ancestor_test.dart chặn đúng lỗi đó — đồng thời người dùng không thấy
    // danh sách nhảy một nhịp khi hộp thoại vừa hiện.
    var grants = <GrantModel>[];
    String? grantsError;
    try {
      grants = await ref.read(adminRepositoryProvider).tenantGrants(tenant.tenantId);
    } catch (error) {
      grantsError = adminErrorMessage(error);
    }
    if (!mounted) return;

    final tokens = TextEditingController();
    final reason = TextEditingController();
    // 0 = vô hạn: grant không có hạn thì không bao giờ "hết hạn".
    var expiryDays = 30;

    Future<void> refresh(StateSetter setDialogState) async {
      try {
        final rows = await ref
            .read(adminRepositoryProvider)
            .tenantGrants(tenant.tenantId);
        setDialogState(() {
          grants = rows;
          grantsError = null;
        });
      } catch (error) {
        setDialogState(() => grantsError = adminErrorMessage(error));
      }
    }

    await showAppDialog<void>(
      context,
      builder: (dialogContext) => StatefulBuilder(
        builder: (context, setDialogState) => AppDialog(
          title: 'Token cấp thêm — ${tenant.tenantName}',
          content: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                AdminField(
                  controller: tokens,
                  label: 'Số token cấp thêm',
                  keyboardType: TextInputType.number,
                ),
                AppSelectTile<int>(
                  label: 'Hạn của grant',
                  value: expiryDays,
                  items: const [0, 7, 30, 90, 365],
                  labelOf: (days) => days == 0 ? 'Vô hạn' : '$days ngày nữa',
                  onChanged: (value) => setDialogState(() => expiryDays = value),
                ),
                AdminField(
                  controller: reason,
                  label: 'Lý do',
                  hint: 'Ví dụ: bù hạn mức quý IV',
                ),
                const SizedBox(height: 4),
                AppPrimaryButton(
                  label: 'Cấp thêm',
                  icon: Icons.add,
                  onPressed: () async {
                    final parsed = int.tryParse(tokens.text.trim());
                    if (parsed == null || parsed <= 0) {
                      showAdminSnack(context, 'Số token phải lớn hơn 0.');
                      return;
                    }
                    try {
                      await ref
                          .read(adminRepositoryProvider)
                          .createGrant(
                            tenantId: tenant.tenantId,
                            tokens: parsed,
                            expiresAtUtc: expiryDays == 0
                                ? null
                                : _horizon(expiryDays),
                            reason: reason.text,
                          );
                      tokens.clear();
                      reason.clear();
                      if (!context.mounted) return;
                      showAdminSnack(context, 'Đã cấp thêm token.');
                      await refresh(setDialogState);
                      await _load();
                    } catch (error) {
                      if (!context.mounted) return;
                      showAdminSnack(context, adminErrorMessage(error));
                    }
                  },
                ),
                const SizedBox(height: 12),
                AppSectionTitle(
                  title: 'Các grant đã cấp',
                  subtitle:
                      'Grant đã tiêu một phần không gỡ được — để hết hạn tự nhiên.',
                ),
                if (grantsError != null) AppErrorBanner(message: grantsError!),
                if (grants.isEmpty && grantsError == null)
                  Text(
                    'Chưa cấp thêm token nào cho đơn vị này.',
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: AppTheme.textMuted,
                    ),
                  ),
                for (final grant in grants)
                  AppTile(
                    title: Text('${_number(grant.tokens)} token'),
                    subtitle: Text(
                      [
                        'còn ${_number(grant.remainingTokens)}',
                        if (grant.expiresAtUtc != null)
                          'hết hạn ${_date(grant.expiresAtUtc!)}'
                        else
                          'vô hạn',
                        if (grant.reason != null) grant.reason!,
                      ].join(' · '),
                    ),
                    destructive: grant.isExpired,
                    suffix: grant.canBeRemoved
                        ? AppMenuButton(
                            tooltip: 'Thao tác grant',
                            items: [
                              AppMenuItem('Gỡ grant', () async {
                                try {
                                  await ref
                                      .read(adminRepositoryProvider)
                                      .deleteGrant(grant.id);
                                  if (!context.mounted) return;
                                  showAdminSnack(context, 'Đã gỡ grant.');
                                  await refresh(setDialogState);
                                  await _load();
                                } catch (error) {
                                  if (!context.mounted) return;
                                  showAdminSnack(
                                    context,
                                    adminErrorMessage(error),
                                  );
                                }
                              }, destructive: true),
                            ],
                          )
                        : null,
                  ),
              ],
            ),
          ),
          actions: [
            AppSecondaryButton(
              label: 'Đóng',
              onPressed: () => Navigator.pop(dialogContext),
            ),
          ],
        ),
      ),
    );

    if (!mounted) return;
    await _load();
  }

  // ------------------------------------------------------------------ dựng

  @override
  Widget build(BuildContext context) {
    final texts = Theme.of(context).textTheme;

    return Scaffold(
      appBar: AppTopBar(
        title: const Text('Hạn mức & Token'),
        actions: [
          AppIconButton(
            icon: Icons.refresh,
            tooltip: 'Tải lại',
            busy: _loading,
            onPressed: _load,
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            Text(
              'Gán gói, cấp thêm token và số dư cho từng đơn vị. Số đã dùng tính cả lượt '
              'chat lẫn lần chạy agent, nên đơn vị đốt hạn mức qua agent cũng hiện đúng.',
              style: texts.bodyMedium?.copyWith(color: AppTheme.textMuted),
            ),
            const SizedBox(height: 16),

            if (_error != null) AppErrorBanner(message: _error!),
            if (_loading && _tenants.isEmpty && _plans.isEmpty)
              const Padding(
                padding: EdgeInsets.symmetric(vertical: 48),
                child: Center(child: CircularProgressIndicator()),
              ),

            AppSectionTitle(
              title: 'Gói hạn mức',
              subtitle: 'Gói đã ngừng dùng không gán được cho đơn vị.',
            ),
            if (!_loading && _plans.isEmpty)
              AppEmptyState(
                icon: Icons.account_balance_wallet_outlined,
                message: 'Chưa có gói nào',
                hint: 'Thêm gói đầu tiên để bắt đầu gán hạn mức cho đơn vị.',
              ),
            for (final plan in _plans)
              AppTile(
                prefix: Icon(
                  plan.isActive ? Icons.sell_outlined : Icons.block_outlined,
                ),
                title: Text(plan.name),
                subtitle: Text(
                  '${_limitLabel(plan.monthlyTokenLimit)} · '
                  '${plan.tenantCount} đơn vị đang dùng',
                ),
                enabled: plan.isActive,
                suffix: AppMenuButton(
                  tooltip: 'Thao tác gói',
                  items: [
                    AppMenuItem('Sửa gói', () => _openPlanDialog(plan: plan)),
                    if (plan.isActive)
                      AppMenuItem(
                        'Ngừng dùng gói',
                        () => _deactivatePlan(plan),
                        destructive: true,
                      ),
                  ],
                ),
              ),
            const SizedBox(height: 8),
            AppPrimaryButton(
              label: 'Thêm gói',
              icon: Icons.add,
              onPressed: () => _openPlanDialog(),
            ),

            const SizedBox(height: 24),
            AppSectionTitle(
              title: 'Hạn mức theo đơn vị',
              subtitle: 'Mở menu ba chấm ở mỗi dòng để gán gói, cấp grant hoặc đặt số dư.',
            ),
            for (final tenant in _tenants)
              AppTile(
                prefix: const Icon(Icons.apartment_outlined),
                title: Text(tenant.tenantName),
                subtitle: Text(_tenantSummary(tenant)),
                suffix: AppMenuButton(
                  tooltip: 'Thao tác hạn mức',
                  items: [
                    AppMenuItem('Gán gói', () => _assignPlan(tenant)),
                    if (tenant.hasPlan)
                      AppMenuItem(
                        'Gỡ gói',
                        () => _removePlan(tenant),
                        destructive: true,
                      ),
                    AppMenuItem('Cấp thêm token', () => _openGrantDialog(tenant)),
                    AppMenuItem('Đặt số dư', () => _openCreditDialog(tenant)),
                  ],
                ),
              ),
          ],
        ),
      ),
    );
  }

  /// Một dòng đọc được cả bốn con số quan trọng: gói, mức dùng, grant còn lại và số dư.
  String _tenantSummary(TenantQuotaModel tenant) {
    final parts = <String>[];

    if (!tenant.hasPlan) {
      parts.add('Chưa gán gói');
    } else if (tenant.isUnlimited) {
      parts.add('${tenant.planName} · không giới hạn');
    } else {
      parts.add(
        '${tenant.planName} · ${_number(tenant.usedTokens)}/'
        '${_number(tenant.monthlyTokenLimit)} (${tenant.utilizationPct}%)',
      );
    }

    if (tenant.isUnlimited && tenant.usedTokens > 0) {
      parts.add('đã dùng ${_number(tenant.usedTokens)}');
    }
    if (tenant.renewalAtUtc != null) {
      parts.add('gia hạn ${_date(tenant.renewalAtUtc!)}');
    }
    if (tenant.activeGrantCount > 0) {
      parts.add('grant +${_number(tenant.grantRemainingTokens)}');
    }
    parts.add('số dư ${_number(tenant.creditBalance)}');

    return parts.join(' · ');
  }
}
