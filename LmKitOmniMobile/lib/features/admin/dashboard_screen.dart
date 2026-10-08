import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';
import '../../app/ui/app_controls.dart';
import 'admin_provider.dart';
import 'admin_repository.dart';
import 'admin_widgets.dart';
import 'dashboard_csv_export.dart';
import 'dashboard_models.dart';

/// Dashboard vận hành trên mobile — cùng dữ liệu và cùng luật phân quyền với trang
/// `/admin` của web: bốn con số cấp hệ thống cho mọi vai trò, khối biểu đồ CHỈ Admin
/// (server trả `cockpit: null` cho Member nên màn tự ẩn), và "Sử dụng của tôi" luôn có.
///
/// Không có gói vẽ biểu đồ trong `pubspec.yaml` và cũng không cần thêm: các chuỗi theo
/// ngày được vẽ bằng cột `Container` tỉ lệ (xem [_MiniBarChart]) — đủ để đọc hình dạng
/// mà không kéo thêm một dependency nặng vào app.
class DashboardScreen extends ConsumerStatefulWidget {
  const DashboardScreen({super.key});

  @override
  ConsumerState<DashboardScreen> createState() => _DashboardScreenState();
}

class _DashboardScreenState extends ConsumerState<DashboardScreen> {
  int _days = 30;
  bool _loading = true;
  bool _exporting = false;
  String? _error;
  DashboardStats? _stats;

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
      final stats = await ref
          .read(adminRepositoryProvider)
          .dashboard(days: _days);
      if (!mounted) return;
      setState(() {
        _stats = stats;
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

  void _setDays(int days) {
    if (_days == days) return;
    setState(() => _days = days);
    _load();
  }

  /// Báo cáo CSV giờ được ghi RA MỘT TỆP trong bộ nhớ máy — trước đây chỉ vào clipboard: dán
  /// được một lần rồi mất, không mở lại được và không gửi đi được.
  ///
  /// Vẫn sao chép vào clipboard sau khi ghi tệp: đường cũ không gãy cho ai đang dùng nó, và
  /// clipboard là phương án dự phòng khi thiết bị không cho ghi tệp.
  Future<void> _exportCsv() async {
    setState(() => _exporting = true);
    try {
      final csv = await ref
          .read(adminRepositoryProvider)
          .dashboardCsv(days: _days);
      final file = await saveDashboardCsv(
        csv: csv,
        days: _days,
        now: DateTime.now(),
        resolveDirectory: resolveDashboardExportDirectory,
      );
      await Clipboard.setData(ClipboardData(text: csv));
      if (!mounted) return;
      showAdminSnack(
        context,
        'Đã lưu báo cáo $_days ngày vào:\n${file.path}\n(đồng thời sao chép vào clipboard)',
      );
    } catch (error) {
      if (!mounted) return;
      showAdminSnack(context, adminErrorMessage(error));
    } finally {
      if (mounted) setState(() => _exporting = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final stats = _stats;
    final cockpit = stats?.cockpit;

    return Scaffold(
      appBar: AppTopBar(
        title: const Text('Dashboard vận hành'),
        actions: [
          AppIconButton(
            icon: Icons.refresh,
            tooltip: 'Tải lại',
            busy: _loading,
            onPressed: _load,
          ),
          if (cockpit != null)
            AppIconButton(
              icon: Icons.download_outlined,
              tooltip: 'Lưu báo cáo CSV ra tệp',
              busy: _exporting,
              onPressed: _exporting ? null : _exportCsv,
            ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            _periodRow(),
            if (_error != null) AppErrorBanner(message: _error!),
            if (_loading && stats == null)
              const Padding(
                padding: EdgeInsets.symmetric(vertical: 48),
                child: Center(child: CircularProgressIndicator()),
              ),
            if (stats != null) ...[
              AppSectionTitle(
                title: 'Chỉ số chính',
                subtitle: 'Toàn hệ thống trong $_days ngày gần nhất',
              ),
              _kpiGrid(stats, cockpit),
              if (cockpit != null) ...[
                AppSectionTitle(title: 'Cần chú ý'),
                _alerts(cockpit),
                AppSectionTitle(
                  title: 'Diễn biến trong kỳ',
                  subtitle:
                      'Token là ước lượng, chỉ tính các lượt trả lời của model',
                ),
                _tokenChart(cockpit),
                _usersChart(cockpit),
                _modelBreakdown(cockpit),
                AppSectionTitle(title: 'Chi tiết'),
                _topUsers(cockpit),
                _spend(cockpit),
                _quota(cockpit),
                _knowledge(cockpit),
                _activity(cockpit),
              ],
              AppSectionTitle(title: 'Sử dụng của tôi'),
              _selfUsage(stats),
            ],
          ],
        ),
      ),
    );
  }

  Widget _periodRow() => Padding(
    padding: const EdgeInsets.only(bottom: 12),
    child: Row(
      children: [
        for (final days in dashboardPeriodDays)
          Expanded(
            child: Padding(
              padding: EdgeInsets.only(
                right: days == dashboardPeriodDays.last ? 0 : 8,
              ),
              child: days == _days
                  ? AppPrimaryButton(
                      label: '$days ngày',
                      onPressed: () => _setDays(days),
                    )
                  : AppSecondaryButton(
                      label: '$days ngày',
                      onPressed: () => _setDays(days),
                    ),
            ),
          ),
      ],
    ),
  );

  Widget _kpiGrid(DashboardStats stats, DashboardCockpit? cockpit) {
    final cards = <({String label, String value, String hint})>[
      (label: 'Người dùng', value: dashNumber(stats.totalUsers), hint: ''),
      (label: 'Đơn vị', value: dashNumber(stats.totalTenants), hint: ''),
      (label: 'Phiên chat', value: dashNumber(stats.totalSessions), hint: ''),
      (label: 'Tài liệu', value: dashNumber(stats.totalDocuments), hint: ''),
      if (cockpit != null)
        (
          label: 'Token $_days ngày',
          value: dashNumber(cockpit.tokens.totalTokens),
          hint: 'Ước lượng, chỉ tính lượt trả lời',
        ),
      if (cockpit != null)
        (
          label: 'Lượt trả lời',
          value: dashNumber(cockpit.tokens.messages),
          hint: '',
        ),
      if (cockpit != null)
        (
          label: 'Token agent-run',
          value: dashNumber(cockpit.tokens.totalAgentRunTokens),
          hint: '${dashNumber(cockpit.tokens.agentRuns)} lần chạy có gọi model',
        ),
      if (cockpit != null)
        (
          label: 'Độ phổ cập',
          value: '${cockpit.users.adoptionPct}%',
          hint: 'MAU / tổng người dùng',
        ),
      if (cockpit != null)
        (
          label: 'Độ trễ TB',
          value: cockpit.performance.samples == 0
              ? '—'
              : '${dashNumber(cockpit.performance.avgLatencyMs)} ms',
          hint: cockpit.performance.samples == 0
              ? 'Chưa có mẫu'
              : cockpit.performance.agentRunSamples == 0
              ? 'p95 ${dashNumber(cockpit.performance.p95LatencyMs)} ms'
              : 'p95 ${dashNumber(cockpit.performance.p95LatencyMs)} ms · '
                    'agent ${dashNumber(cockpit.performance.agentRunAvgLatencyMs)} ms',
        ),
    ];

    return AppCard(
      child: Column(
        children: [
          for (var i = 0; i < cards.length; i += 2)
            Padding(
              padding: EdgeInsets.only(top: i == 0 ? 0 : 12),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(child: _kpi(cards[i])),
                  const SizedBox(width: 12),
                  Expanded(
                    child: i + 1 < cards.length
                        ? _kpi(cards[i + 1])
                        : const SizedBox.shrink(),
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }

  Widget _kpi(({String label, String value, String hint}) card) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text(
        card.label,
        style: Theme.of(
          context,
        ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
      ),
      const SizedBox(height: 2),
      Text(card.value, style: Theme.of(context).textTheme.titleLarge),
      if (card.hint.isNotEmpty)
        Text(
          card.hint,
          style: Theme.of(
            context,
          ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
        ),
    ],
  );

  Widget _alerts(DashboardCockpit cockpit) {
    final lines = <String>[];
    if (cockpit.quota.tenantsOverLimit > 0) {
      lines.add(
        '${dashNumber(cockpit.quota.tenantsOverLimit)} đơn vị đã VƯỢT hạn mức token tháng này.',
      );
    }
    if (cockpit.quota.tenantsOverThreshold > 0) {
      lines.add(
        '${dashNumber(cockpit.quota.tenantsOverThreshold)} đơn vị đã dùng từ 80% hạn mức tháng.',
      );
    }
    if (cockpit.alerts.expiringGrantCount > 0) {
      lines.add(
        '${dashNumber(cockpit.alerts.expiringGrantCount)} khoản cấp thêm token sắp hết hạn trong 30 ngày.',
      );
    }
    for (final grant in cockpit.alerts.expiringGrants) {
      lines.add(
        '"${grant.tenantName}" còn ${dashNumber(grant.remainingTokens)} token cấp thêm, hết hạn sau ${grant.daysLeft} ngày.',
      );
    }
    if (cockpit.documents.failed > 0) {
      lines.add(
        '${dashNumber(cockpit.documents.failed)} tài liệu lập chỉ mục thất bại.',
      );
    }
    if (cockpit.documents.pending > 0) {
      lines.add(
        '${dashNumber(cockpit.documents.pending)} tài liệu đang chờ lập chỉ mục.',
      );
    }

    if (lines.isEmpty) {
      return AppCard(
        child: Text(
          'Không có cảnh báo nào trong kỳ này.',
          style: Theme.of(
            context,
          ).textTheme.bodyMedium?.copyWith(color: AppTheme.textMuted),
        ),
      );
    }

    return AppCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          for (final line in lines)
            Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Icon(
                    Icons.warning_amber_rounded,
                    size: 18,
                    color: Color(0xFFD97706),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      line,
                      style: Theme.of(context).textTheme.bodyMedium,
                    ),
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }

  Widget _tokenChart(DashboardCockpit cockpit) {
    final daily = cockpit.tokens.daily;
    final prompt = daily.map((d) => d.promptTokens).toList();
    final completion = daily.map((d) => d.completionTokens).toList();
    final maxValue = [
      ...prompt,
      ...completion,
    ].fold<int>(0, (peak, value) => value > peak ? value : peak);

    return AppCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Token theo ngày', style: Theme.of(context).textTheme.titleMedium),
          Text(
            'Prompt (dữ liệu gửi lên model) và completion (model sinh ra) của '
            '${dashNumber(cockpit.tokens.messages)} lượt trả lời.',
            style: Theme.of(
              context,
            ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
          ),
          const SizedBox(height: 12),
          _MiniBarChart(
            series: [
              (values: prompt, color: const Color(0xFF3B82F6)),
              (values: completion, color: const Color(0xFF10B981)),
            ],
            maxValue: maxValue,
            height: 120,
          ),
          const SizedBox(height: 8),
          Wrap(
            spacing: 16,
            children: [
              _legendDot(const Color(0xFF3B82F6), 'Prompt'),
              _legendDot(const Color(0xFF10B981), 'Completion'),
            ],
          ),
          if (daily.isNotEmpty) ...[
            const SizedBox(height: 6),
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Text(
                  daily.first.date,
                  style: Theme.of(
                    context,
                  ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
                ),
                Text(
                  daily.last.date,
                  style: Theme.of(
                    context,
                  ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }

  Widget _usersChart(DashboardCockpit cockpit) => AppCard(
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          'Người dùng hoạt động theo ngày',
          style: Theme.of(context).textTheme.titleMedium,
        ),
        Text(
          'Số người dùng riêng biệt có phiên chat mỗi ngày — không tính phiên chạy '
          'agent và chat tạm thời.',
          style: Theme.of(
            context,
          ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
        ),
        const SizedBox(height: 6),
        Text(
          'DAU ${dashNumber(cockpit.users.dailyActive)} · '
          'WAU ${dashNumber(cockpit.users.weeklyActive)} · '
          'MAU ${dashNumber(cockpit.users.monthlyActive)}',
          style: Theme.of(context).textTheme.bodySmall,
        ),
        const SizedBox(height: 12),
        _MiniBarChart(
          series: [
            (
              values: cockpit.users.daily.map((d) => d.count).toList(),
              color: const Color(0xFF8B5CF6),
            ),
          ],
          maxValue: cockpit.users.daily.fold<int>(
            0,
            (peak, d) => d.count > peak ? d.count : peak,
          ),
          height: 90,
        ),
      ],
    ),
  );

  Widget _modelBreakdown(DashboardCockpit cockpit) {
    if (cockpit.tokens.byModel.isEmpty) {
      return AppCard(
        child: Text(
          'Chưa có lượt trả lời nào trong kỳ.',
          style: Theme.of(
            context,
          ).textTheme.bodyMedium?.copyWith(color: AppTheme.textMuted),
        ),
      );
    }
    final total = cockpit.tokens.totalTokens;
    return AppCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Phân bổ token theo model',
            style: Theme.of(context).textTheme.titleMedium,
          ),
          const SizedBox(height: 8),
          for (final model in cockpit.tokens.byModel)
            Padding(
              padding: const EdgeInsets.only(bottom: 10),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Expanded(
                        child: Text(
                          model.modelName,
                          style: Theme.of(context).textTheme.bodyMedium,
                        ),
                      ),
                      const SizedBox(width: 8),
                      Text(
                        '${dashNumber(model.totalTokens)} · '
                        '${_share(model.totalTokens, total)}%',
                        style: Theme.of(
                          context,
                        ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
                      ),
                    ],
                  ),
                  const SizedBox(height: 4),
                  _progressBar(_share(model.totalTokens, total) / 100),
                ],
              ),
            ),
        ],
      ),
    );
  }

  Widget _topUsers(DashboardCockpit cockpit) {
    if (cockpit.users.topUsers.isEmpty) {
      return AppCard(
        child: Text(
          'Chưa có lượt chat nào trong kỳ.',
          style: Theme.of(
            context,
          ).textTheme.bodyMedium?.copyWith(color: AppTheme.textMuted),
        ),
      );
    }

    return AppCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Người dùng dùng nhiều nhất',
            style: Theme.of(context).textTheme.titleMedium,
          ),
          const SizedBox(height: 8),
          for (final user in cockpit.users.topUsers)
            Padding(
              padding: const EdgeInsets.only(bottom: 10),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          user.name.isEmpty ? '(không có tên)' : user.name,
                          style: Theme.of(context).textTheme.bodyMedium,
                        ),
                        Text(
                          user.email,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: Theme.of(context).textTheme.bodySmall?.copyWith(
                            color: AppTheme.textMuted,
                          ),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(width: 8),
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.end,
                    children: [
                      Text(
                        '${dashNumber(user.totalTokens)} token',
                        style: Theme.of(context).textTheme.bodyMedium,
                      ),
                      Text(
                        '${dashNumber(user.questions)} hỏi · '
                        '${dashNumber(user.answers)} trả lời',
                        style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AppTheme.textMuted,
                        ),
                      ),
                    ],
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }

  Widget _spend(DashboardCockpit cockpit) => AppCard(
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          'Tập trung chi tiêu theo đơn vị',
          style: Theme.of(context).textTheme.titleMedium,
        ),
        const SizedBox(height: 4),
        Text(
          '${dashNumber(cockpit.spend.totalTokens)} token',
          style: Theme.of(context).textTheme.titleLarge,
        ),
        Text(
          'Top 3 đơn vị chiếm ${cockpit.spend.top3SharePct}% · đơn vị lớn nhất '
          'chiếm ${cockpit.spend.topTenantSharePct}%',
          style: Theme.of(
            context,
          ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
        ),
        if (cockpit.spend.totalAgentRunTokens > 0)
          Text(
            'Cộng thêm ${dashNumber(cockpit.spend.totalAgentRunTokens)} token của '
            '${dashNumber(cockpit.tokens.agentRuns)} lần chạy agent',
            style: Theme.of(
              context,
            ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
          ),
        if (cockpit.spend.topTenantName.isNotEmpty)
          Text(
            'Nhiều nhất: ${cockpit.spend.topTenantName}',
            style: Theme.of(context).textTheme.bodySmall,
          ),
        const SizedBox(height: 8),
        for (final tenant in cockpit.spend.byTenant)
          Padding(
            padding: const EdgeInsets.only(bottom: 10),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        tenant.tenantName,
                        style: Theme.of(context).textTheme.bodyMedium,
                      ),
                    ),
                    const SizedBox(width: 8),
                    Text(
                      '${dashNumber(tenant.totalTokens)} · '
                      '${cockpit.spend.sharePct(tenant)}%',
                      style: Theme.of(
                        context,
                      ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
                    ),
                  ],
                ),
                if (tenant.agentRuns > 0)
                  Text(
                    'trong đó ${dashNumber(tenant.agentTokens)} token agent-run '
                    '(${dashNumber(tenant.agentRuns)} lần chạy)',
                    style: Theme.of(
                      context,
                    ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
                  ),
                const SizedBox(height: 4),
                _progressBar(cockpit.spend.sharePct(tenant) / 100),
              ],
            ),
          ),
      ],
    ),
  );

  Widget _quota(DashboardCockpit cockpit) {
    final limited = cockpit.quota.limited;
    return AppCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Hạn mức & số dư', style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 8),
          _kv(
            'Đang dùng gói',
            '${dashNumber(cockpit.quota.tenantsOnPlan)} · '
                '${dashNumber(cockpit.quota.tenantsWithoutPlan)} chưa gán',
          ),
          _kv(
            'Hạn mức tháng',
            dashNumber(cockpit.quota.totalMonthlyLimit),
          ),
          _kv(
            'Đã dùng tháng này',
            dashNumber(cockpit.quota.totalMonthlyUsed),
          ),
          _kv(
            'Token mua trước',
            dashNumber(cockpit.quota.totalCreditBalance),
          ),
          if (limited.isEmpty)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(
                'Chưa có đơn vị nào được gán gói có hạn mức.',
                style: Theme.of(
                  context,
                ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
              ),
            ),
          const SizedBox(height: 8),
          for (final tenant in limited)
            Padding(
              padding: const EdgeInsets.only(bottom: 10),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Expanded(
                        child: Text(
                          tenant.tenantName,
                          style: Theme.of(context).textTheme.bodyMedium,
                        ),
                      ),
                      const SizedBox(width: 8),
                      Text(
                        '${dashNumber(tenant.usedTokens)} / '
                        '${dashNumber(tenant.monthlyLimit)} · '
                        '${tenant.utilizationPct}%',
                        style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: tenant.utilizationPct >= 100
                              ? const Color(0xFFC62828)
                              : AppTheme.textMuted,
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 4),
                  _progressBar(
                    tenant.utilizationPct / 100,
                    color: tenant.utilizationPct >= 100
                        ? const Color(0xFFC62828)
                        : tenant.utilizationPct >= 80
                        ? const Color(0xFFD97706)
                        : const Color(0xFF2E7D32),
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }

  Widget _knowledge(DashboardCockpit cockpit) => AppCard(
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('Cơ sở tri thức', style: Theme.of(context).textTheme.titleMedium),
        const SizedBox(height: 8),
        _kv('Tài liệu', dashNumber(cockpit.documents.total)),
        _kv(
          'Đã lập chỉ mục',
          '${dashNumber(cockpit.documents.indexed)} '
              '(${cockpit.documents.indexRatePct}%)',
        ),
        _kv('Đang chờ', dashNumber(cockpit.documents.pending)),
        _kv('Lỗi', dashNumber(cockpit.documents.failed)),
        _kv('Đoạn (chunk)', dashNumber(cockpit.documents.totalChunks)),
      ],
    ),
  );

  Widget _activity(DashboardCockpit cockpit) {
    if (cockpit.activity.topActions.isEmpty) {
      return AppCard(
        child: Text(
          'Chưa ghi nhận hoạt động nào trong kỳ.',
          style: Theme.of(
            context,
          ).textTheme.bodyMedium?.copyWith(color: AppTheme.textMuted),
        ),
      );
    }
    return AppCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Hoạt động hệ thống',
            style: Theme.of(context).textTheme.titleMedium,
          ),
          Text(
            '${dashNumber(cockpit.activity.total)} sự kiện trong kỳ',
            style: Theme.of(
              context,
            ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
          ),
          const SizedBox(height: 8),
          for (final action in cockpit.activity.topActions)
            _kv(action.key, dashNumber(action.count)),
        ],
      ),
    );
  }

  Widget _selfUsage(DashboardStats stats) => AppCard(
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _kv('Câu hỏi', dashNumber(stats.myUsage.questions)),
        _kv('Lượt trả lời', dashNumber(stats.myUsage.answers)),
        _kv('Token', dashNumber(stats.myUsage.totalTokens)),
        _kv('Phiên chat', dashNumber(stats.myUsage.sessions)),
      ],
    ),
  );

  Widget _legendDot(Color color, String label) => Row(
    mainAxisSize: MainAxisSize.min,
    children: [
      Container(
        width: 10,
        height: 10,
        decoration: BoxDecoration(color: color, shape: BoxShape.circle),
      ),
      const SizedBox(width: 6),
      Text(label, style: Theme.of(context).textTheme.bodySmall),
    ],
  );

  /// Một dòng nhãn–giá trị. Hai vế đều `Expanded`/`Flexible`: nhãn dài (tên hành động
  /// audit) không được phép đẩy giá trị ra khỏi màn ở máy 320dp.
  Widget _kv(String label, String value) => Padding(
    padding: const EdgeInsets.only(bottom: 6),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Expanded(
          child: Text(
            label,
            style: Theme.of(
              context,
            ).textTheme.bodyMedium?.copyWith(color: AppTheme.textMuted),
          ),
        ),
        const SizedBox(width: 8),
        Text(value, style: Theme.of(context).textTheme.bodyMedium),
      ],
    ),
  );

  Widget _progressBar(double fraction, {Color color = const Color(0xFF0B5394)}) {
    final clamped = fraction.isFinite ? fraction.clamp(0.0, 1.0) : 0.0;
    return ClipRRect(
      borderRadius: BorderRadius.circular(3),
      child: LinearProgressIndicator(
        value: clamped,
        minHeight: 6,
        backgroundColor: const Color(0xFFE2E8F0),
        valueColor: AlwaysStoppedAnimation<Color>(color),
      ),
    );
  }

  int _share(int value, int total) =>
      total <= 0 ? 0 : ((value * 100) / total).round();
}

/// Biểu đồ cột tối giản: mỗi mốc là một cột cao theo tỉ lệ, không trục, không nhãn.
///
/// Vẽ bằng `Container` thay vì thêm một gói chart: app chỉ cần đọc *hình dạng* của chuỗi
/// (ngày nào cao, ngày nào trống), việc đó không đáng một dependency ~200KB.
class _MiniBarChart extends StatelessWidget {
  const _MiniBarChart({
    required this.series,
    required this.maxValue,
    required this.height,
  });

  final List<({List<int> values, Color color})> series;
  final int maxValue;
  final double height;

  @override
  Widget build(BuildContext context) {
    final length = series.isEmpty ? 0 : series.first.values.length;
    if (length == 0) {
      return SizedBox(
        height: height,
        child: Center(
          child: Text(
            'Không có dữ liệu trong kỳ.',
            style: Theme.of(
              context,
            ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
          ),
        ),
      );
    }

    final peak = maxValue <= 0 ? 1 : maxValue;
    return SizedBox(
      height: height,
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          for (var index = 0; index < length; index++)
            Expanded(
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 0.5),
                child: Column(
                  mainAxisAlignment: MainAxisAlignment.end,
                  children: [
                    for (final entry in series)
                      Padding(
                        padding: const EdgeInsets.only(top: 1),
                        child: Container(
                          // Cột 0 vẫn để 1px để ngày trống vẫn thấy được trên trục.
                          height:
                              (entry.values[index] / peak) * (height - 8) + 1,
                          decoration: BoxDecoration(
                            color: entry.color,
                            borderRadius: BorderRadius.circular(2),
                          ),
                        ),
                      ),
                  ],
                ),
              ),
            ),
        ],
      ),
    );
  }
}
