import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../../core/network/api_exception.dart';
import '../chat/chat_models.dart';
import '../chat/chat_provider.dart';
import 'workspace_models.dart';
import 'workspace_provider.dart';
import '../../app/ui/app_controls.dart';

class ProjectsScreen extends ConsumerStatefulWidget {
  const ProjectsScreen({super.key, this.onOpenSession});

  /// Mở một đoạn chat của dự án trong tab AI Chat.
  final void Function(String sessionId)? onOpenSession;

  @override
  ConsumerState<ProjectsScreen> createState() => _ProjectsScreenState();
}

class _ProjectsScreenState extends ConsumerState<ProjectsScreen> {
  List<ProjectModel> _projects = const [];
  bool _loading = true;
  String? _error;
  final _search = TextEditingController();

  /// Chỉ một dự án được bung tại một thời điểm (giống bản desktop).
  String? _expandedId;
  final _sessions = <String, List<ChatSessionModel>>{};
  String? _sessionsError;
  bool _sessionsLoading = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    setState(() => _loading = true);
    try {
      final result = await ref
          .read(workspaceRepositoryProvider)
          .projects(search: _search.text);
      if (mounted) setState(() => _projects = result);
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _create() async {
    final data = await _showProjectForm();
    if (data == null) return;
    try {
      await ref
          .read(workspaceRepositoryProvider)
          .createProject(
            name: data.name,
            description: data.description,
            icon: data.icon,
            instructions: data.instructions,
          );
      await _load();
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    }
  }

  /// Sửa dự án: cùng form với tạo mới nhưng điền sẵn giá trị hiện có.
  Future<void> _edit(ProjectModel project) async {
    final data = await _showProjectForm(existing: project);
    if (data == null) return;
    try {
      await ref
          .read(workspaceRepositoryProvider)
          .updateProject(
            project.id,
            name: data.name,
            description: data.description,
            icon: data.icon,
            instructions: data.instructions,
          );
      await _load();
      if (mounted) showAppSnack(context, 'Đã lưu dự án.');
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    }
  }

  /// Tạo đoạn chat mới nằm trong dự án rồi mở luôn trong tab AI Chat.
  Future<void> _newChatInProject(ProjectModel project) async {
    try {
      final session = await ref
          .read(chatSessionsProvider.notifier)
          .create(projectId: project.id);
      if (!mounted) return;
      setState(
        () => _sessions[project.id] = [session, ...?_sessions[project.id]],
      );
      final open = widget.onOpenSession;
      if (open == null) {
        showAppSnack(
          context,
          'Đã tạo đoạn chat trong “${project.name}”. Mở tab AI Chat để tiếp tục.',
        );
        return;
      }
      open(session.id);
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    }
  }

  Future<void> _delete(ProjectModel project) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Xóa dự án'),
        content: Text('Xóa “${project.name}”? Các đoạn chat sẽ được giữ lại.'),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Huỷ'),
          ),
          AppPrimaryButton(
            label: 'Xóa',
            onPressed: () => Navigator.pop(context, true),
            expand: false,
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    try {
      await ref.read(workspaceRepositoryProvider).deleteProject(project.id);
      await _load();
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    }
  }

  String _message(Object error) =>
      error is ApiException ? error.message : error.toString();

  /// Bung/thu gọn danh sách đoạn chat của một dự án.
  Future<void> _toggleSessions(ProjectModel project) async {
    if (_expandedId == project.id) {
      setState(() => _expandedId = null);
      return;
    }
    setState(() {
      _expandedId = project.id;
      _sessionsError = null;
      _sessionsLoading = !_sessions.containsKey(project.id);
    });
    if (_sessions.containsKey(project.id)) return;
    try {
      final sessions = await ref
          .read(workspaceRepositoryProvider)
          .projectSessions(project.id);
      if (!mounted || _expandedId != project.id) return;
      setState(() => _sessions[project.id] = sessions);
    } catch (error) {
      if (mounted) setState(() => _sessionsError = _message(error));
    } finally {
      if (mounted) setState(() => _sessionsLoading = false);
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: const Text('Projects'),
      actions: [
        IconButton(
          onPressed: _create,
          tooltip: 'Tạo dự án',
          icon: const Icon(Icons.add),
        ),
      ],
    ),
    body: RefreshIndicator(
      onRefresh: _load,
      child: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          TextField(
            controller: _search,
            onSubmitted: (_) => _load(),
            decoration: const InputDecoration(
              prefixIcon: Icon(Icons.search),
              hintText: 'Tìm dự án...',
            ),
          ),
          const SizedBox(height: 16),
          if (_error != null)
            _ErrorCard(
              message: _error!,
              onClose: () => setState(() => _error = null),
            ),
          if (_loading)
            const Padding(
              padding: EdgeInsets.all(40),
              child: Center(child: CircularProgressIndicator()),
            )
          else if (_projects.isEmpty)
            const _EmptyCard()
          else ...[
            for (final project in _projects) _projectCard(project),
          ],
        ],
      ),
    ),
    floatingActionButton: FloatingActionButton(
      onPressed: _create,
      child: const Icon(Icons.add),
    ),
  );

  Widget _projectCard(ProjectModel project) {
    final expanded = _expandedId == project.id;
    return Card(
      child: Column(
        children: [
          ListTile(
            contentPadding: const EdgeInsets.all(12),
            leading: CircleAvatar(
              child: Text(
                project.icon?.trim().isNotEmpty == true ? project.icon! : '📁',
              ),
            ),
            // Tiêu đề mục: 16/600 (`text-base font-semibold` của web) — tránh
            // tự đặt w700 rồi lệch khỏi các danh sách khác.
            title: Text(
              project.name,
              style: Theme.of(context).textTheme.titleMedium,
            ),
            subtitle: Text(
              '${project.sessionCount} đoạn chat'
              '${project.instructions?.trim().isNotEmpty == true ? ' · có hướng dẫn riêng' : ''}'
              '\n${project.description ?? 'Chưa có mô tả.'}',
              maxLines: 3,
              overflow: TextOverflow.ellipsis,
            ),
            isThreeLine: true,
            trailing: PopupMenuButton<String>(
              onSelected: (value) => switch (value) {
                'sessions' => _toggleSessions(project),
                'chat' => _newChatInProject(project),
                'edit' => _edit(project),
                _ => _delete(project),
              },
              itemBuilder: (_) => [
                PopupMenuItem(
                  value: 'sessions',
                  child: Text(expanded ? 'Ẩn đoạn chat' : 'Xem đoạn chat'),
                ),
                const PopupMenuItem(
                  value: 'chat',
                  child: Text('Chat mới trong dự án'),
                ),
                const PopupMenuItem(value: 'edit', child: Text('Sửa dự án')),
                const PopupMenuItem(value: 'delete', child: Text('Xóa dự án')),
              ],
            ),
            onTap: () => _toggleSessions(project),
          ),
          if (expanded)
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 12),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Divider(),
                  if (_sessionsError != null)
                    AppAlert(message: _sessionsError!, isError: true),
                  if (_sessionsLoading)
                    const Padding(
                      padding: EdgeInsets.all(12),
                      child: Center(
                        child: CircularProgressIndicator(strokeWidth: 2),
                      ),
                    ),
                  if (!_sessionsLoading &&
                      (_sessions[project.id]?.isEmpty ?? false))
                    const Padding(
                      padding: EdgeInsets.symmetric(vertical: 8),
                      child: Text('Dự án chưa có đoạn chat nào.'),
                    ),
                  for (final session
                      in _sessions[project.id] ?? const <ChatSessionModel>[])
                    ListTile(
                      contentPadding: EdgeInsets.zero,
                      dense: true,
                      leading: const Icon(Icons.chat_bubble_outline, size: 18),
                      title: Text(
                        session.title?.trim().isNotEmpty == true
                            ? session.title!
                            : '(chưa có tiêu đề)',
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      ),
                      subtitle: Text(
                        [
                          if (session.agentName?.isNotEmpty == true)
                            session.agentName!,
                          if (session.createdAt != null)
                            _formatDate(session.createdAt!),
                          if (session.isEphemeral) 'tạm thời',
                        ].join(' • '),
                      ),
                      trailing: const Icon(Icons.chevron_right),
                      onTap: widget.onOpenSession == null
                          ? null
                          : () => widget.onOpenSession!(session.id),
                    ),
                ],
              ),
            ),
        ],
      ),
    );
  }

  static String _formatDate(DateTime value) {
    final local = value.toLocal();
    String two(int number) => number.toString().padLeft(2, '0');
    return '${two(local.day)}/${two(local.month)}/${local.year} '
        '${two(local.hour)}:${two(local.minute)}';
  }

  Future<_ProjectForm?> _showProjectForm({ProjectModel? existing}) async {
    final name = TextEditingController(text: existing?.name ?? '');
    final description = TextEditingController(
      text: existing?.description ?? '',
    );
    final icon = TextEditingController(text: existing?.icon ?? '');
    final instructions = TextEditingController(
      text: existing?.instructions ?? '',
    );
    final result = await showDialog<_ProjectForm>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(existing == null ? 'Tạo dự án mới' : 'Sửa dự án'),
        content: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextField(
                controller: name,
                decoration: const InputDecoration(labelText: 'Tên dự án'),
              ),
              TextField(
                controller: icon,
                decoration: const InputDecoration(labelText: 'Biểu tượng'),
              ),
              TextField(
                controller: description,
                decoration: const InputDecoration(labelText: 'Mô tả'),
              ),
              TextField(
                controller: instructions,
                maxLines: 4,
                decoration: const InputDecoration(
                  labelText: 'Hướng dẫn cho trợ lý',
                ),
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
            label: existing == null ? 'Tạo' : 'Lưu',
            onPressed: () => Navigator.pop(
              context,
              _ProjectForm(
                name.text,
                description.text,
                icon.text,
                instructions.text,
              ),
            ),
            expand: false,
          ),
        ],
      ),
    );
    name.dispose();
    description.dispose();
    icon.dispose();
    instructions.dispose();
    return result;
  }
}

class _ProjectForm {
  const _ProjectForm(this.name, this.description, this.icon, this.instructions);
  final String name;
  final String description;
  final String icon;
  final String instructions;
}

class _EmptyCard extends StatelessWidget {
  const _EmptyCard();

  @override
  Widget build(BuildContext context) => const AppEmptyState(
    icon: Icons.folder_outlined,
    message: 'Chưa có dự án nào.',
    hint:
        'Dự án gom các đoạn chat liên quan và giữ hướng dẫn riêng cho trợ lý.',
  );
}

class _ErrorCard extends StatelessWidget {
  const _ErrorCard({required this.message, required this.onClose});
  final String message;
  final VoidCallback onClose;
  @override
  Widget build(BuildContext context) =>
      AppErrorBanner(message: message, onDismiss: onClose);
}
