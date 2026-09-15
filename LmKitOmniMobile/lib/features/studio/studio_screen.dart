import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

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
    appBar: AppBar(
      title: const Text('AI Studio'),
      actions: [
        IconButton(
          tooltip: 'Làm mới',
          onPressed: _loadAll,
          icon: const Icon(Icons.refresh),
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
          MaterialBanner(
            content: Text(_error!),
            backgroundColor: Theme.of(context).colorScheme.errorContainer,
            actions: [
              TextButton(
                onPressed: () => setState(() => _error = null),
                child: const Text('Đóng'),
              ),
            ],
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
          IconButton(
            tooltip: 'Thêm mới',
            onPressed: add,
            icon: const Icon(Icons.add),
          ),
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
        AppCard(
          padding: EdgeInsets.zero,
          child: ListTile(
            leading: CircleAvatar(
              child: Text(agent.icon?.isNotEmpty == true ? agent.icon! : '🤖'),
            ),
            title: Text(agent.name),
            subtitle: Text(
              '${agent.description ?? agent.personaPrompt ?? 'Không có mô tả.'}\n'
              '${agent.allowedTools == null ? 'Công cụ mặc định' : '${agent.allowedTools!.length} công cụ'}'
              '${agent.knowledgeDocumentIds.isEmpty ? '' : ' · ${agent.knowledgeDocumentIds.length} tài liệu ghim'}'
              '${agent.isSharedWithTenant ? ' · chia sẻ tenant' : ''}',
            ),
            isThreeLine: true,
            onTap: agent.isOwner ? () => _openAgentForm(existing: agent) : null,
            trailing: agent.isOwner
                ? PopupMenuButton<String>(
                    tooltip: 'Tuỳ chọn',
                    onSelected: (value) => switch (value) {
                      'chat' => _chatWithAgent(agent),
                      'edit' => _openAgentForm(existing: agent),
                      _ => _deleteAgent(agent),
                    },
                    itemBuilder: (_) => const [
                      PopupMenuItem(
                        value: 'chat',
                        child: Text('Chat với agent'),
                      ),
                      PopupMenuItem(value: 'edit', child: Text('Sửa')),
                      PopupMenuItem(value: 'delete', child: Text('Xoá')),
                    ],
                  )
                : IconButton(
                    tooltip: 'Chat với agent',
                    onPressed: () => _chatWithAgent(agent),
                    icon: const Icon(Icons.chat_bubble_outline),
                  ),
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
        Card(
          child: ListTile(
            title: Text(task.name),
            subtitle: Text(
              '${task.scheduleKind} • ${task.prompt}',
              maxLines: 3,
              overflow: TextOverflow.ellipsis,
            ),
            isThreeLine: true,
            leading: Icon(
              task.enabled ? Icons.schedule : Icons.pause_circle_outline,
              color: task.enabled ? AppTheme.success : AppTheme.textMuted,
            ),
            trailing: PopupMenuButton<String>(
              onSelected: (value) async {
                if (value == 'toggle') await _repo.toggleSchedule(task.id);
                if (value == 'delete') await _repo.deleteSchedule(task.id);
                _loadAll();
              },
              itemBuilder: (_) => const [
                PopupMenuItem(value: 'toggle', child: Text('Bật / tắt')),
                PopupMenuItem(value: 'delete', child: Text('Xóa')),
              ],
            ),
          ),
        ),
    ],
  );

  Widget _runsTab() => ListView(
    padding: const EdgeInsets.only(bottom: 24),
    children: [
      _header('Agent Runs', 'Theo dõi các mục tiêu agent tự hành.', null),
      Padding(
        padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            TextField(
              controller: _runGoal,
              minLines: 2,
              maxLines: 4,
              enabled: !_startingRun,
              decoration: const InputDecoration(
                labelText: 'Mục tiêu cho agent',
                hintText: 'Ví dụ: Tổng hợp hợp đồng tháng 9 và liệt kê rủi ro.',
              ),
            ),
            const SizedBox(height: 8),
            DropdownButtonFormField<String?>(
              initialValue: _runAgentId,
              isExpanded: true,
              decoration: const InputDecoration(
                labelText: 'Custom agent (không bắt buộc)',
              ),
              items: [
                const DropdownMenuItem<String?>(
                  value: null,
                  child: Text('Agent mặc định'),
                ),
                for (final agent in _agents)
                  DropdownMenuItem<String?>(
                    value: agent.id,
                    child: Text(agent.name),
                  ),
              ],
              onChanged: _startingRun
                  ? null
                  : (value) => setState(() => _runAgentId = value),
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
                  IconButton(
                    tooltip: 'Dừng theo dõi',
                    onPressed: () => _runCancel?.cancel(),
                    icon: const Icon(Icons.stop),
                  ),
                ],
              ],
            ),
            if (_runOutput.isNotEmpty)
              Card(
                margin: const EdgeInsets.only(top: 12),
                child: Padding(
                  padding: const EdgeInsets.all(12),
                  child: FormattedMessage(text: _runOutput),
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
        AppCard(
          padding: EdgeInsets.zero,
          child: ListTile(
            title: Text(run.goal, maxLines: 2, overflow: TextOverflow.ellipsis),
            subtitle: Text('${run.status} • ${run.stepCount} bước'),
            onTap: () => _openRun(run),
            trailing:
                run.status.toLowerCase() == 'running' ||
                    run.status.toLowerCase() == 'pending'
                ? IconButton(
                    tooltip: 'Dừng tác vụ đang chạy',
                    icon: const Icon(Icons.stop_circle_outlined),
                    onPressed: () async {
                      await _repo.cancelAgentRun(run.id);
                      _loadAll();
                    },
                  )
                : null,
          ),
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
      TextField(
        controller: _researchQuery,
        minLines: 3,
        maxLines: 6,
        enabled: !_researching,
        decoration: const InputDecoration(
          labelText: 'Chủ đề nghiên cứu',
          hintText: 'Nhập câu hỏi cần nghiên cứu...',
        ),
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
            IconButton(
              tooltip: 'Dừng nghiên cứu',
              onPressed: () => _researchCancel?.cancel(),
              icon: const Icon(Icons.stop),
            ),
        ],
      ),
      if (_research.isNotEmpty)
        Card(
          margin: const EdgeInsets.only(top: 16),
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: FormattedMessage(text: _research),
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
        Card(
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
                Row(
                  mainAxisAlignment: MainAxisAlignment.end,
                  children: [
                    TextButton(
                      onPressed: () => _decide(item['id'].toString(), false),
                      child: const Text('Từ chối'),
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
        ListTile(
          leading: Icon(
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
    final result = await showDialog<(String, String, String, int?)>(
      context: context,
      builder: (_) => AlertDialog(
        title: const Text('Tạo lịch tự động'),
        content: SingleChildScrollView(
          child: Column(
            children: [
              TextField(
                controller: name,
                decoration: const InputDecoration(labelText: 'Tên'),
              ),
              TextField(
                controller: prompt,
                maxLines: 4,
                decoration: const InputDecoration(labelText: 'Prompt'),
              ),
              DropdownButtonFormField<String>(
                initialValue: 'interval',
                isExpanded: true,
                items: const [
                  DropdownMenuItem(
                    value: 'interval',
                    child: Text('Theo khoảng thời gian'),
                  ),
                  DropdownMenuItem(value: 'once', child: Text('Chạy một lần')),
                ],
                onChanged: (_) {},
                decoration: const InputDecoration(labelText: 'Loại lịch'),
              ),
              TextField(
                controller: interval,
                keyboardType: TextInputType.number,
                decoration: const InputDecoration(labelText: 'Khoảng phút'),
              ),
            ],
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Huỷ'),
          ),
          AppPrimaryButton(
            label: 'Tạo',
            onPressed: () => Navigator.pop(context, (
              name.text,
              prompt.text,
              'interval',
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
