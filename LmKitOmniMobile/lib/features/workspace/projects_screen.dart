import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../../core/network/api_exception.dart';
import 'workspace_models.dart';
import 'workspace_provider.dart';

class ProjectsScreen extends ConsumerStatefulWidget {
  const ProjectsScreen({super.key});

  @override
  ConsumerState<ProjectsScreen> createState() => _ProjectsScreenState();
}

class _ProjectsScreenState extends ConsumerState<ProjectsScreen> {
  List<ProjectModel> _projects = const [];
  bool _loading = true;
  String? _error;
  final _search = TextEditingController();

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

  Future<void> _delete(ProjectModel project) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Xóa dự án'),
        content: Text('Xóa “${project.name}”? Các đoạn chat sẽ được giữ lại.'),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Hủy'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Xóa'),
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
            for (final project in _projects)
              Card(
                child: ListTile(
                  contentPadding: const EdgeInsets.all(12),
                  leading: CircleAvatar(
                    child: Text(
                      project.icon?.trim().isNotEmpty == true
                          ? project.icon!
                          : '📁',
                    ),
                  ),
                  title: Text(
                    project.name,
                    style: const TextStyle(fontWeight: FontWeight.w700),
                  ),
                  subtitle: Text(
                    '${project.sessionCount} đoạn chat\n${project.description ?? 'Chưa có mô tả.'}',
                    maxLines: 3,
                    overflow: TextOverflow.ellipsis,
                  ),
                  isThreeLine: true,
                  trailing: PopupMenuButton<String>(
                    onSelected: (value) {
                      if (value == 'delete') _delete(project);
                    },
                    itemBuilder: (_) => const [
                      PopupMenuItem(value: 'delete', child: Text('Xóa dự án')),
                    ],
                  ),
                ),
              ),
          ],
        ],
      ),
    ),
    floatingActionButton: FloatingActionButton(
      onPressed: _create,
      child: const Icon(Icons.add),
    ),
  );

  Future<_ProjectForm?> _showProjectForm() async {
    final name = TextEditingController();
    final description = TextEditingController();
    final icon = TextEditingController();
    final instructions = TextEditingController();
    final result = await showDialog<_ProjectForm>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Tạo dự án mới'),
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
            child: const Text('Hủy'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(
              context,
              _ProjectForm(
                name.text,
                description.text,
                icon.text,
                instructions.text,
              ),
            ),
            child: const Text('Tạo'),
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
  Widget build(BuildContext context) => const Padding(
    padding: EdgeInsets.all(40),
    child: Center(child: Text('Chưa có dự án nào.')),
  );
}

class _ErrorCard extends StatelessWidget {
  const _ErrorCard({required this.message, required this.onClose});
  final String message;
  final VoidCallback onClose;
  @override
  Widget build(BuildContext context) => Card(
    color: Theme.of(context).colorScheme.errorContainer,
    child: ListTile(
      title: Text(message),
      trailing: IconButton(onPressed: onClose, icon: const Icon(Icons.close)),
    ),
  );
}
