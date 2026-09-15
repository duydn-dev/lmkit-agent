import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_exception.dart';
import 'studio_models.dart';
import 'studio_repository.dart';
import 'studio_provider.dart';
import 'content_creation_tab.dart';

class StudioScreen extends ConsumerStatefulWidget {
  const StudioScreen({super.key});

  @override
  ConsumerState<StudioScreen> createState() => _StudioScreenState();
}

class _StudioScreenState extends ConsumerState<StudioScreen>
    with SingleTickerProviderStateMixin {
  late final TabController _tabs = TabController(length: 6, vsync: this);
  final _search = TextEditingController();
  List<CustomAgentModel> _agents = const [];
  List<ScheduledTaskModel> _schedules = const [];
  List<AgentRunModel> _runs = const [];
  List<Map<String, dynamic>> _approvals = const [], _notifications = const [];
  String _research = '';
  final _researchQuery = TextEditingController();
  bool _loading = false, _researching = false;
  CancelToken? _researchCancel;
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

  Future<void> _createAgent() async {
    final form = await _agentForm();
    if (form == null) return;
    try {
      await _repo.createCustomAgent(
        name: form.$1,
        personaPrompt: form.$2,
        description: form.$3,
      );
      await _loadAll();
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    }
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
        IconButton(onPressed: _loadAll, icon: const Icon(Icons.refresh)),
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
              Text(
                title,
                style: const TextStyle(
                  fontSize: 20,
                  fontWeight: FontWeight.w700,
                ),
              ),
              Text(subtitle, style: Theme.of(context).textTheme.bodySmall),
            ],
          ),
        ),
        if (add != null)
          IconButton(onPressed: add, icon: const Icon(Icons.add)),
      ],
    ),
  );

  Widget _agentTab() => ListView(
    padding: const EdgeInsets.only(bottom: 24),
    children: [
      _header(
        'Custom Agents',
        'Persona và bộ công cụ tùy chỉnh.',
        _createAgent,
      ),
      if (_loading)
        const Center(
          child: Padding(
            padding: EdgeInsets.all(32),
            child: CircularProgressIndicator(),
          ),
        ),
      if (!_loading && _agents.isEmpty)
        const _Empty(text: 'Chưa có custom agent.'),
      for (final agent in _agents)
        Card(
          child: ListTile(
            leading: CircleAvatar(
              child: Text(agent.icon?.isNotEmpty == true ? agent.icon! : '🤖'),
            ),
            title: Text(agent.name),
            subtitle: Text(
              agent.description ?? agent.personaPrompt ?? 'Không có mô tả.',
            ),
            isThreeLine: true,
            trailing: agent.isOwner
                ? IconButton(
                    icon: const Icon(Icons.delete_outline),
                    onPressed: () async {
                      await _repo.deleteCustomAgent(agent.id);
                      _loadAll();
                    },
                  )
                : const Icon(Icons.people_outline),
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
      if (_schedules.isEmpty) const _Empty(text: 'Chưa có lịch tự động.'),
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
              color: task.enabled ? Colors.green : Colors.grey,
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
      if (_runs.isEmpty) const _Empty(text: 'Chưa có agent run.'),
      for (final run in _runs)
        Card(
          child: ListTile(
            title: Text(run.goal, maxLines: 2, overflow: TextOverflow.ellipsis),
            subtitle: Text('${run.status} • ${run.stepCount} bước'),
            trailing:
                run.status.toLowerCase() == 'running' ||
                    run.status.toLowerCase() == 'pending'
                ? IconButton(
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
      const Text(
        'Deep Research',
        style: TextStyle(fontSize: 20, fontWeight: FontWeight.w700),
      ),
      const SizedBox(height: 4),
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
            child: FilledButton.icon(
              onPressed: _researching ? null : _runResearch,
              icon: const Icon(Icons.explore),
              label: Text(_researching ? 'Đang nghiên cứu...' : 'Bắt đầu'),
            ),
          ),
          if (_researching)
            IconButton(
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
            child: SelectableText(_research),
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
        const _Empty(text: 'Không có tác vụ chờ phê duyệt.'),
      for (final item in _approvals)
        Card(
          child: Padding(
            padding: const EdgeInsets.all(12),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  item['actionName']?.toString() ?? 'Tác vụ',
                  style: const TextStyle(fontWeight: FontWeight.w700),
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
                    FilledButton(
                      onPressed: () => _decide(item['id'].toString(), true),
                      child: const Text('Phê duyệt'),
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

  Future<(String, String, String)?> _agentForm() async {
    final name = TextEditingController(),
        prompt = TextEditingController(),
        description = TextEditingController();
    final result = await showDialog<(String, String, String)>(
      context: context,
      builder: (_) => AlertDialog(
        title: const Text('Tạo Custom Agent'),
        content: SingleChildScrollView(
          child: Column(
            children: [
              TextField(
                controller: name,
                decoration: const InputDecoration(labelText: 'Tên'),
              ),
              TextField(
                controller: description,
                decoration: const InputDecoration(labelText: 'Mô tả'),
              ),
              TextField(
                controller: prompt,
                maxLines: 5,
                decoration: const InputDecoration(labelText: 'Persona prompt'),
              ),
            ],
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Hủy'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, (
              name.text,
              prompt.text,
              description.text,
            )),
            child: const Text('Tạo'),
          ),
        ],
      ),
    );
    name.dispose();
    prompt.dispose();
    description.dispose();
    return result;
  }

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
            child: const Text('Hủy'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, (
              name.text,
              prompt.text,
              'interval',
              int.tryParse(interval.text),
            )),
            child: const Text('Tạo'),
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
  const _Empty({required this.text});
  final String text;
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.all(40),
    child: Center(child: Text(text)),
  );
}
