import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:forui/forui.dart';

import '../../app/theme.dart';

import '../../app/ui/app_controls.dart';
import '../../core/network/api_exception.dart';
import '../admin/admin_models.dart';
import '../admin/admin_provider.dart';
import '../chat/chat_provider.dart';
import '../chat/message_format.dart';
import 'agent_form_sheet.dart';
import 'content_creation_tab.dart';
import 'run_detail_screen.dart';
import 'studio_models.dart';
import 'studio_provider.dart';
import 'studio_repository.dart';

/// Tên trang của từng tab, đúng bằng nhãn mục trong menu chức năng — nên header
/// luôn đọc đúng tên trang người dùng vừa bấm vào, và đổi theo khi họ đổi tab.
const _pageTitles = <String>[
  'Agent Studio',
  'Task Scheduler',
  'Automation Agent',
  'Deep Research',
  'HITL Approvals',
  'Content Studio',
];

class StudioScreen extends ConsumerStatefulWidget {
  const StudioScreen({super.key, this.onOpenSession, this.initialTab = 0});

  /// Mở một phiên chat vừa tạo (chat với custom agent) trong tab AI Chat.
  final void Function(String sessionId)? onOpenSession;

  /// Tab mở đầu tiên: 0 Agents, 1 Lịch, 2 Automation Agent (Runs), 3 Deep
  /// Research, 4 HITL Approvals, 5 Content Studio — dùng để deep-link từ màn
  /// "Thêm" giống các route riêng của web.
  final int initialTab;

  @override
  ConsumerState<StudioScreen> createState() => _StudioScreenState();
}

class _StudioScreenState extends ConsumerState<StudioScreen>
    with SingleTickerProviderStateMixin {
  late final TabController _tabs = TabController(
    length: 6,
    vsync: this,
    initialIndex: widget.initialTab.clamp(0, 5),
  );
  final _search = TextEditingController();
  List<CustomAgentModel> _agents = const [];
  List<ScheduledTaskModel> _schedules = const [];
  List<AgentRunModel> _runs = const [];
  List<Map<String, dynamic>> _approvals = const [], _notifications = const [];
  String _research = '';
  final _researchQuery = TextEditingController();
  bool _loading = false, _researching = false;
  CancelToken? _researchCancel;

  /// Trạng thái tab Runs: mục tiêu, agent tuỳ chọn, log tiến trình đang chạy.
  final _runGoal = TextEditingController();
  String? _runAgentId;
  String _runOutput = '';
  bool _startingRun = false;
  CancelToken? _runCancel;
  String? _error;

  @override
  void initState() {
    super.initState();
    _loadAll();
  }

  @override
  void dispose() {
    _tabs.dispose();
    _search.dispose();
    _researchQuery.dispose();
    _researchCancel?.cancel();
    _runGoal.dispose();
    _runCancel?.cancel();
    super.dispose();
  }

  StudioRepository get _repo => ref.read(studioRepositoryProvider);

  Future<void> _loadAll() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final results = await Future.wait([
        _repo.customAgents(search: _search.text),
        _repo.schedules(search: _search.text),
        _repo.agentRuns(search: _search.text),
        _repo.pendingApprovals(),
        _repo.notifications(),
      ]);
      if (!mounted) return;
      setState(() {
        _agents = results[0] as List<CustomAgentModel>;
        _schedules = results[1] as List<ScheduledTaskModel>;
        _runs = results[2] as List<AgentRunModel>;
        _approvals = results[3] as List<Map<String, dynamic>>;
        _notifications = results[4] as List<Map<String, dynamic>>;
      });
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  String _message(Object error) =>
      error is ApiException ? error.message : error.toString();

  /// Nạp catalog công cụ, tài liệu và adapter LoRA rồi mở form tạo/sửa agent.
  Future<void> _openAgentForm({CustomAgentModel? existing}) async {
    List<AgentToolModel> tools = const [];
    List<KnowledgeDocModel> documents = const [];
    List<LoraAdapterModel> adapters = const [];
    try {
      tools = await _repo.toolCatalog();
      documents = await _repo.ownedDocuments();
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
      return;
    }
    try {
      // Adapter là tuỳ chọn tăng cường: tính năng tắt (501) không chặn form.
      adapters = await ref.read(adminRepositoryProvider).loraAdapters();
    } catch (_) {
      adapters = const [];
    }
    if (!mounted) return;

    final form = await showAgentForm(
      context,
      tools: tools,
      documents: documents,
      loraAdapters: adapters,
      existing: existing,
    );
    if (form == null) return;

    try {
      String? agentId;
      if (existing == null) {
        final created = await _repo.createCustomAgent(
          name: form.name,
          personaPrompt: form.personaPrompt,
          description: form.description,
          icon: form.icon,
          shared: form.shared,
          allowedTools: form.allowedTools,
          knowledgeDocumentIds: form.knowledgeDocumentIds,
        );
        agentId = created.id;
      } else {
        await _repo.updateCustomAgent(
          id: existing.id,
          name: form.name,
          personaPrompt: form.personaPrompt,
          description: form.description,
          icon: form.icon,
          shared: form.shared,
          allowedTools: form.allowedTools,
          knowledgeDocumentIds: form.knowledgeDocumentIds,
        );
        agentId = existing.id;
      }

      // Binding LoRA đi qua endpoint riêng, không nằm trong payload agent.
      if (agentId.isNotEmpty && form.loraAdapterId != existing?.loraAdapterId) {
        final admin = ref.read(adminRepositoryProvider);
        try {
          if (form.loraAdapterId != null) {
            await admin.assignLoraAdapter(
              agentId: agentId,
              adapterId: form.loraAdapterId!,
            );
          } else {
            await admin.unassignLoraAdapter(agentId);
          }
        } catch (error) {
          // Agent đã lưu thành công — chỉ cảnh báo phần adapter.
          if (mounted) {
            showAppSnack(
              context,
              'Agent đã lưu nhưng gán LoRA thất bại: ${_message(error)}',
            );
          }
        }
      }

      await _loadAll();
      if (mounted) {
        showAppSnack(
          context,
          existing == null ? 'Đã tạo custom agent.' : 'Đã lưu custom agent.',
        );
      }
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    }
  }

  /// Mở một đoạn chat mới đã gắn custom agent (nút "Chat với agent" của desktop).
  Future<void> _chatWithAgent(CustomAgentModel agent) async {
    try {
      final session = await ref
          .read(chatSessionsProvider.notifier)
          .create(customAgentId: agent.id);
      if (!mounted) return;
      final open = widget.onOpenSession;
      if (open == null) {
        showAppSnack(
          context,
          'Đã tạo đoạn chat với ${agent.name}. Mở tab AI Chat để tiếp tục.',
        );
        return;
      }
      open(session.id);
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    }
  }

  /// Bắt đầu một agent run và đọc tiến trình SSE ngay trên tab Runs.
  Future<void> _startRun() async {
    final goal = _runGoal.text.trim();
    if (goal.isEmpty || _startingRun) return;
    _runCancel?.cancel();
    final token = CancelToken();
    _runCancel = token;
    setState(() {
      _startingRun = true;
      _runOutput = '';
      _error = null;
    });
    try {
      await _repo.startAgentRun(
        goal: goal,
        customAgentId: _runAgentId,
        cancelToken: token,
        onEvent: (event) {
          if (!mounted) return;
          setState(() {
            if (event.type == 'step') _runOutput += '▶ ${event.value}\n';
            if (event.type == 'thinking') _runOutput += '• ${event.value}\n';
            if (event.type == 'content') _runOutput += event.value;
            if (event.type == 'approval') {
              _runOutput += '\n⏸ Cần phê duyệt: ${event.value}\n';
            }
            if (event.type == 'done' || event.type == 'error') {
              _runOutput += '\n${event.value}\n';
            }
          });
        },
      );
    } catch (error) {
      if (!token.isCancelled && mounted) {
        setState(() => _error = _message(error));
      }
    } finally {
      if (mounted) setState(() => _startingRun = false);
      await _loadAll();
    }
  }

  Future<void> _deleteAgent(CustomAgentModel agent) async {
    final confirmed = await confirmAppAction(
      context,
      title: 'Xoá custom agent',
      message:
          'Xoá "${agent.name}"? Các phiên chat đã dùng agent này vẫn giữ nguyên.',
    );
    if (!confirmed) return;
    try {
      await _repo.deleteCustomAgent(agent.id);
      await _loadAll();
      if (mounted) showAppSnack(context, 'Đã xoá custom agent.');
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    }
  }

  Future<void> _openRun(AgentRunModel run) async {
    await Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => RunDetailScreen(runId: run.id, goal: run.goal),
      ),
    );
    await _loadAll();
  }

  Future<void> _createSchedule() async {
    final form = await _scheduleForm();
    if (form == null) return;
    try {
      await _repo.createSchedule(
        name: form.$1,
        prompt: form.$2,
        scheduleKind: form.$3,
        intervalMinutes: form.$4,
      );
      await _loadAll();
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    }
  }

  /// Bật/tắt lịch: cập nhật lạc quan để công tắc phản hồi tức thì; lỗi mạng
  /// hay lỗi validate (vd vượt giới hạn số lịch bật) thì trả trạng thái cũ và
  /// báo đúng lý do cho người dùng.
  Future<void> _toggleSchedule(ScheduledTaskModel task, {required bool enable}) async {
    if (task.enabled == enable) return;
    setState(() {
      _schedules = [
        for (final item in _schedules)
          if (item.id == task.id)
            ScheduledTaskModel(
              id: item.id,
              name: item.name,
              prompt: item.prompt,
              scheduleKind: item.scheduleKind,
              enabled: enable,
              nextRunUtc: item.nextRunUtc,
              runMode: item.runMode,
              intervalMinutes: item.intervalMinutes,
              timeOfDayMinutes: item.timeOfDayMinutes,
              dayOfWeek: item.dayOfWeek,
              lastStatus: item.lastStatus,
              lastError: item.lastError,
            )
          else
            item,
      ];
    });
    try {
      await _repo.toggleSchedule(task.id);
      await _loadAll();
    } catch (error) {
      if (!mounted) return;
      setState(() {
        _schedules = [
          for (final item in _schedules)
            if (item.id == task.id) task else item,
        ];
        _error = _message(error);
      });
    }
  }

  Future<void> _runResearch() async {
    final query = _researchQuery.text.trim();
    if (query.isEmpty || _researching) return;
    _researchCancel?.cancel();
    final token = CancelToken();
    _researchCancel = token;
    setState(() {
      _researching = true;
      _research = '';
      _error = null;
    });
    try {
      await _repo.runResearch(
        query: query,
        maxSources: 3,
        cancelToken: token,
        onEvent: (event) {
          if (!mounted) return;
          setState(() {
            if (event.type == 'thinking') {
              _research += '• ${event.value}\n';
            }
            if (event.type == 'content') {
              _research += event.value;
            }
            if (event.type == 'saved') {
              _research += '\nĐã lưu Canvas: ${event.value}\n';
            }
            if (event.type == 'error') {
              _error = event.value;
            }
          });
        },
      );
    } catch (error) {
      if (!token.isCancelled && mounted) {
        setState(() => _error = _message(error));
      }
    } finally {
      if (mounted) setState(() => _researching = false);
    }
  }

  Future<void> _decide(String id, bool approve) async {
    try {
      if (approve) {
        await _repo.approve(id);
      } else {
        await _repo.reject(id);
      }
      await _loadAll();
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppTopBar(
      // Tiêu đề là **tên trang đang mở**, không phải tên nhóm "AI Studio":
      // người dùng bấm "Deep Research" trong menu chức năng thì header phải đọc
      // đúng "Deep Research" — nếu không, thanh trên cùng lại nói một đằng còn
      // màn đang mở nói một nẻo, đúng thứ đã phải đi sửa.
      title: ListenableBuilder(
        listenable: _tabs,
        builder: (context, _) => Text(_pageTitles[_tabs.index]),
      ),
      actions: [
        AppIconButton(
          icon: Icons.refresh,
          tooltip: 'Làm mới',
          onPressed: _loadAll,
        ),
      ],
      bottom: TabBar(
        controller: _tabs,
        isScrollable: true,
        tabs: const [
          Tab(text: 'Agents'),
          Tab(text: 'Lịch'),
          Tab(text: 'Runs'),
          Tab(text: 'Research'),
          Tab(text: 'Phê duyệt'),
          Tab(text: 'Tạo nội dung'),
        ],
      ),
    ),
    body: Column(
      children: [
        if (_error != null)
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 0),
            child: AppAlert(
              message: _error!,
              isError: true,
              onRetry: () => setState(() => _error = null),
            ),
          ),
        Expanded(
          child: RefreshIndicator(
            onRefresh: _loadAll,
            child: TabBarView(
              controller: _tabs,
              children: [
                _agentTab(),
                _scheduleTab(),
                _runsTab(),
                _researchTab(),
                _approvalTab(),
                _contentTab(),
              ],
            ),
          ),
        ),
      ],
    ),
  );

  Widget _header(String title, String subtitle, VoidCallback? add) => Padding(
    padding: const EdgeInsets.fromLTRB(16, 16, 16, 8),
    child: Row(
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(title, style: Theme.of(context).textTheme.titleLarge),
              Text(
                subtitle,
                style: Theme.of(
                  context,
                ).textTheme.bodyMedium?.copyWith(color: AppTheme.textMuted),
              ),
            ],
          ),
        ),
        if (add != null)
          AppIconButton(icon: Icons.add, tooltip: 'Thêm mới', onPressed: add),
      ],
    ),
  );

  Widget _agentTab() => ListView(
    padding: const EdgeInsets.only(bottom: 24),
    children: [
      _header(
        'Custom Agents',
        'Persona, bộ công cụ được phép gọi và tài liệu ghim.',
        () => _openAgentForm(),
      ),
      if (_loading)
        const Center(
          child: Padding(
            padding: EdgeInsets.all(32),
            child: CircularProgressIndicator(),
          ),
        ),
      if (!_loading && _agents.isEmpty)
        const _Empty(
          icon: Icons.smart_toy_outlined,
          text: 'Chưa có custom agent.',
          hint:
              'Tạo agent riêng để ghim persona, công cụ và tài liệu cho từng nghiệp vụ.',
        ),
      for (final agent in _agents)
        AppTile(
          prefix: CircleAvatar(
            child: Text(agent.icon?.isNotEmpty == true ? agent.icon! : '🤖'),
          ),
          title: Text(agent.name),
          subtitle: Text(
            '${agent.description ?? agent.personaPrompt ?? 'Không có mô tả.'}\n'
            '${agent.allowedTools == null ? 'Công cụ mặc định' : '${agent.allowedTools!.length} công cụ'}'
            '${agent.knowledgeDocumentIds.isEmpty ? '' : ' · ${agent.knowledgeDocumentIds.length} tài liệu ghim'}'
            '${agent.isSharedWithTenant ? ' · chia sẻ tenant' : ''}',
          ),
          onTap: agent.isOwner ? () => _openAgentForm(existing: agent) : null,
          suffix: agent.isOwner
              ? AppMenuButton(
                  tooltip: 'Tuỳ chọn',
                  items: [
                    AppMenuItem('Chat với agent', () => _chatWithAgent(agent)),
                    AppMenuItem('Sửa', () => _openAgentForm(existing: agent)),
                    AppMenuItem(
                      'Xoá',
                      () => _deleteAgent(agent),
                      destructive: true,
                    ),
                  ],
                )
              : AppIconButton(
                  tooltip: 'Chat với agent',
                  onPressed: () => _chatWithAgent(agent),
                  icon: Icons.chat_bubble_outline,
                ),
        ),
    ],
  );

  Widget _scheduleTab() => ListView(
    padding: const EdgeInsets.only(bottom: 24),
    children: [
      _header(
        'Task Scheduler',
        'Tự động chạy prompt theo lịch.',
        _createSchedule,
      ),
      if (_schedules.isEmpty)
        const _Empty(
          icon: Icons.schedule_outlined,
          text: 'Chưa có lịch tự động.',
          hint:
              'Lịch tự động chạy một prompt theo giờ hoặc theo ngày bạn chọn.',
        ),
      for (final task in _schedules)
        AppTile(
          title: Text(task.name),
          subtitle: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              // Loại lịch mô tả bằng tiếng Việt — người dùng đọc là hiểu.
              Text(
                _scheduleKindLabel(task),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
              Text(
                task.prompt,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
              ),
              // Mốc hẹn kế tiếp / kết quả lần cuối — biết lịch còn sống hay đã
              // ngừng mà không cần bấm vào.
              Text(
                task.enabled && task.nextRunUtc != null
                    ? 'Lần chạy kế: ${_fmtNextRun(task.nextRunUtc!)}'
                    : task.lastStatus == null
                        ? 'Chưa chạy lần nào'
                        : 'Lần cuối: ${_lastStatusLabel(task.lastStatus!)}${task.lastError == null ? '' : ' — ${task.lastError}'}',
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: AppTheme.textMuted,
                ),
              ),
            ],
          ),
          prefix: Icon(
            task.enabled ? Icons.schedule : Icons.pause_circle_outline,
            color: task.enabled ? AppTheme.success : AppTheme.textMuted,
          ),
          // Công tắc gọn trong hàng: FSwitch trực tiếp (AppSwitchTile là
          // FTile.raw lồng FTile — làm vỡ layout, mất tiêu đề — đã gặp thật).
          suffix: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                task.enabled ? 'Đang chạy' : 'Đã tắt',
                style: Theme.of(context).textTheme.labelMedium?.copyWith(
                  color: task.enabled ? AppTheme.success : AppTheme.textMuted,
                  fontWeight: FontWeight.w600,
                ),
              ),
              const SizedBox(width: 8),
              FSwitch(
                value: task.enabled,
                onChange: (value) => _toggleSchedule(task, enable: value),
              ),
            ],
          ),
        ),
    ],
  );

  /// Mô tả loại lịch bằng tiếng Việt, kèm chi tiết (khoảng cách / giờ hẹn /
  /// ngày trong tuần) — chỉ nhìn tile là biết lịch hoạt động thế nào.
  String _scheduleKindLabel(ScheduledTaskModel task) {
    const days = [
      'Thứ 2', 'Thứ 3', 'Thứ 4', 'Thứ 5', 'Thứ 6', 'Thứ 7', 'Chủ nhật',
    ];
    String hhmm(int minutes) =>
        '${(minutes ~/ 60).toString().padLeft(2, '0')}:${(minutes % 60).toString().padLeft(2, '0')}';
    return switch (task.scheduleKind) {
      'interval' when task.intervalMinutes != null =>
        'Lặp mỗi ${task.intervalMinutes} phút',
      'daily' when task.timeOfDayMinutes != null =>
        'Hằng ngày lúc ${hhmm(task.timeOfDayMinutes!)}',
      'weekly' when task.timeOfDayMinutes != null =>
        'Hàng tuần: ${task.dayOfWeek != null && task.dayOfWeek! >= 1 && task.dayOfWeek! <= 7 ? days[task.dayOfWeek! - 1] : '?'} lúc ${hhmm(task.timeOfDayMinutes!)}',
      'once' => 'Chạy một lần',
      _ => task.scheduleKind,
    };
  }

  /// Mốc hẹn kế tiếp dạng thân thiện (giờ địa phương), ví dụ "14:05 ngày 19/09".
  String _fmtNextRun(DateTime utc) {
    final local = utc.toLocal();
    final now = DateTime.now();
    final today = DateTime(now.year, now.month, now.day);
    final day = DateTime(local.year, local.month, local.day);
    final diffDays = day.difference(today).inDays;
    final hhmm =
        '${local.hour.toString().padLeft(2, '0')}:${local.minute.toString().padLeft(2, '0')}';
    if (diffDays == 0) return 'hôm nay $hhmm';
    if (diffDays == 1) return 'ngày mai $hhmm';
    return '$hhmm ngày ${local.day}/${local.month}';
  }

  /// Nhãn kết quả lần chạy gần nhất cho tile lịch.
  String _lastStatusLabel(String status) => switch (status) {
    'Succeeded' => 'thành công',
    'Failed' => 'thất bại',
    'Running' => 'đang chạy',
    'AwaitingApproval' => 'chờ duyệt',
    _ => status,
  };

  Widget _runsTab() => ListView(
    padding: const EdgeInsets.only(bottom: 24),
    children: [
      _header('Agent Runs', 'Theo dõi các mục tiêu agent tự hành.', null),
      Padding(
        padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Ô nhập và ô chọn của hệ thiết kế (Forui), không dùng widget
            // Material: cùng thang chữ, cùng viền với phần còn lại của app.
            AppTextField(
              controller: _runGoal,
              label: 'Mục tiêu cho agent',
              hint: 'Ví dụ: Tổng hợp hợp đồng tháng 9 và liệt kê rủi ro.',
              maxLines: 4,
              enabled: !_startingRun,
            ),
            AppSelectTile<String?>(
              label: 'Custom agent (không bắt buộc)',
              icon: Icons.smart_toy_outlined,
              value: _runAgentId,
              items: [null, for (final agent in _agents) agent.id],
              labelOf: (id) => id == null
                  ? 'Agent mặc định'
                  : _agents
                        .firstWhere(
                          (agent) => agent.id == id,
                          orElse: () => CustomAgentModel(
                            id: id,
                            name: 'Agent đã xoá',
                          ),
                        )
                        .name,
              subtitleOf: (id) => id == null
                  ? 'Dùng trợ lý mặc định của đơn vị.'
                  : 'Chạy đúng persona và công cụ của agent này.',
              enabled: !_startingRun,
              onChanged: (value) => setState(() => _runAgentId = value),
            ),
            const SizedBox(height: 8),
            Row(
              children: [
                Expanded(
                  child: AppPrimaryButton(
                    label: _startingRun ? 'Agent đang chạy…' : 'Chạy',
                    icon: Icons.play_arrow,
                    busy: _startingRun,
                    onPressed: _startRun,
                  ),
                ),
                if (_startingRun) ...[
                  const SizedBox(width: 8),
                  AppIconButton(
                    icon: Icons.stop,
                    tooltip: 'Dừng theo dõi',
                    onPressed: () => _runCancel?.cancel(),
                  ),
                ],
              ],
            ),
            if (_runOutput.isNotEmpty)
              Padding(
                padding: const EdgeInsets.only(top: 12),
                child: AppCard(
                  child: Padding(
                    padding: const EdgeInsets.all(12),
                    child: FormattedMessage(text: _runOutput),
                  ),
                ),
              ),
          ],
        ),
      ),
      if (_runs.isEmpty)
        const _Empty(
          icon: Icons.play_circle_outline,
          text: 'Chưa có agent run.',
          hint: 'Tác vụ tự hành chạy theo mục tiêu và ghi lại từng bước ở đây.',
        ),
      for (final run in _runs)
        AppTile(
          title: Text(run.goal, maxLines: 2, overflow: TextOverflow.ellipsis),
          subtitle: Text('${run.status} • ${run.stepCount} bước'),
          onTap: () => _openRun(run),
          suffix:
              run.status.toLowerCase() == 'running' ||
                  run.status.toLowerCase() == 'pending'
              ? AppIconButton(
                  tooltip: 'Dừng tác vụ đang chạy',
                  onPressed: () async {
                    await _repo.cancelAgentRun(run.id);
                    _loadAll();
                  },
                  icon: Icons.stop_circle_outlined,
                )
              : null,
        ),
    ],
  );

  Widget _researchTab() => ListView(
    padding: const EdgeInsets.all(16),
    children: [
      Text('Deep Research', style: Theme.of(context).textTheme.titleLarge),
      const SizedBox(height: 6),
      const Text('Tìm kiếm nhiều nguồn và tổng hợp báo cáo.'),
      const SizedBox(height: 16),
      AppTextField(
        controller: _researchQuery,
        label: 'Chủ đề nghiên cứu',
        hint: 'Nhập câu hỏi cần nghiên cứu...',
        maxLines: 6,
        enabled: !_researching,
      ),
      const SizedBox(height: 12),
      Row(
        children: [
          Expanded(
            // `expand: true`: nút phải nhận bề ngang có hạn để nhãn dài tự cắt,
            // nếu để nút co theo nội dung và bọc trong `Expanded` thì ở cỡ chữ hệ
            // thống lớn nhãn sẽ tràn ra ngoài (đã bắt được ở test 1.3×).
            child: AppPrimaryButton(
              label: _researching ? 'Đang nghiên cứu...' : 'Bắt đầu',
              icon: Icons.explore,
              onPressed: _researching ? null : _runResearch,
            ),
          ),
          if (_researching)
            AppIconButton(
              icon: Icons.stop,
              tooltip: 'Dừng nghiên cứu',
              onPressed: () => _researchCancel?.cancel(),
            ),
        ],
      ),
      if (_research.isNotEmpty)
        Padding(
          padding: const EdgeInsets.only(top: 16),
          child: AppCard(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: FormattedMessage(text: _research),
            ),
          ),
        ),
    ],
  );

  Widget _contentTab() => ContentCreationTab(
    repo: _repo,
    onError: (message) => setState(() => _error = message),
  );

  Widget _approvalTab() => ListView(
    padding: const EdgeInsets.only(bottom: 24),
    children: [
      _header(
        'Phê duyệt tác vụ',
        'Các hành động cần xác nhận trước khi chạy.',
        null,
      ),
      if (_approvals.isEmpty)
        const _Empty(
          icon: Icons.verified_user_outlined,
          text: 'Không có tác vụ chờ phê duyệt.',
          hint:
              'Khi agent cần bạn cho phép chạy hành động tiếp theo, mục sẽ hiện ở đây.',
        ),
      for (final item in _approvals)
        AppCard(
          child: Padding(
            padding: const EdgeInsets.all(12),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  item['actionName']?.toString() ?? 'Tác vụ',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                if (item['details']?.toString().isNotEmpty == true)
                  Padding(
                    padding: const EdgeInsets.only(top: 8),
                    child: Text(item['details'].toString()),
                  ),
                // `Wrap` chứ không phải `Row`: ở cỡ chữ hệ thống lớn (1.3×) hai
                // nhãn này cộng lại vượt bề ngang thẻ ở máy 320dp và tràn ra
                // ngoài (đã đo được 64px). Xuống dòng thì luôn còn bấm được.
                Wrap(
                  alignment: WrapAlignment.end,
                  spacing: 8,
                  runSpacing: 4,
                  children: [
                    AppDestructiveButton(
                      label: 'Từ chối',
                      onPressed: () => _decide(item['id'].toString(), false),
                    ),
                    AppPrimaryButton(
                      label: 'Phê duyệt',
                      onPressed: () => _decide(item['id'].toString(), true),
                      expand: false,
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      _header('Thông báo', 'Cập nhật từ tài liệu và tác vụ tự động.', null),
      for (final item in _notifications)
        AppTile(
          prefix: Icon(
            item['isRead'] == true
                ? Icons.notifications_none
                : Icons.notifications_active,
          ),
          title: Text(item['title']?.toString() ?? ''),
          subtitle: Text(item['body']?.toString() ?? ''),
          onTap: () async {
            if (item['isRead'] != true) {
              await _repo.markNotificationRead(item['id'].toString());
            }
            _loadAll();
          },
        ),
    ],
  );

  Future<(String, String, String, int?)?> _scheduleForm() async {
    final name = TextEditingController(),
        prompt = TextEditingController(),
        interval = TextEditingController(text: '60');
    // Loại lịch người dùng chọn; trước đây ô chọn bị bỏ qua và luôn gửi
    // `interval`, nên chọn "Chạy một lần" xong vẫn tạo lịch theo khoảng thời gian.
    var kind = 'interval';
    // Lưu ý: nội dung hộp thoại **không được** dùng widget cần tổ tiên
    // `Material` (`TextField`, `DropdownButtonFormField`…). `FDialog` của Forui
    // không có `Material`, nên những widget đó ném lỗi "No Material widget
    // found" và cả form trắng xoá — dùng widget của hệ thiết kế ở đây.
    final result = await showAppDialog<(String, String, String, int?)>(
      context,
      builder: (dialogContext) => AppDialog(
        title: 'Tạo lịch tự động',
        content: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            AppTextField(
              controller: name,
              label: 'Tên',
              autofocus: true,
            ),
            AppTextField(controller: prompt, label: 'Prompt', maxLines: 4),
            AppSelectTile<String>(
              label: 'Loại lịch',
              icon: Icons.event_repeat,
              value: kind,
              items: const ['interval', 'once'],
              labelOf: (value) => value == 'interval'
                  ? 'Theo khoảng thời gian'
                  : 'Chạy một lần',
              subtitleOf: (value) => value == 'interval'
                  ? 'Chạy lại sau mỗi số phút bên dưới.'
                  : 'Chỉ chạy một lần ở lần kích hoạt kế tiếp.',
              onChanged: (value) => kind = value,
            ),
            AppTextField(
              controller: interval,
              label: 'Khoảng phút',
              hint: 'Dùng khi chạy theo khoảng thời gian',
              keyboardType: TextInputType.number,
            ),
          ],
        ),
        actions: [
          AppSecondaryButton(
            label: 'Huỷ',
            onPressed: () => Navigator.pop(dialogContext),
          ),
          const SizedBox(width: 10),
          AppPrimaryButton(
            label: 'Tạo',
            onPressed: () => Navigator.pop(dialogContext, (
              name.text,
              prompt.text,
              kind,
              int.tryParse(interval.text),
            )),
            expand: false,
          ),
        ],
      ),
    );
    name.dispose();
    prompt.dispose();
    interval.dispose();
    return result;
  }
}

class _Empty extends StatelessWidget {
  const _Empty({
    required this.text,
    this.icon = Icons.inbox_outlined,
    this.hint,
  });

  final String text;
  final IconData icon;
  final String? hint;

  @override
  Widget build(BuildContext context) =>
      AppEmptyState(message: text, icon: icon, hint: hint);
}
