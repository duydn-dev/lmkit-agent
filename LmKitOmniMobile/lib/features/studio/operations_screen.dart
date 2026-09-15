import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_exception.dart';
import 'studio_provider.dart';

class OperationsScreen extends ConsumerStatefulWidget {
  const OperationsScreen({super.key});
  @override
  ConsumerState<OperationsScreen> createState() => _OperationsScreenState();
}

class _OperationsScreenState extends ConsumerState<OperationsScreen>
    with SingleTickerProviderStateMixin {
  late final TabController _tabs = TabController(length: 3, vsync: this);
  List<Map<String, dynamic>> _keys = const [];
  List<Map<String, dynamic>> _users = const [];
  List<Map<String, dynamic>> _audit = const [];
  bool _loading = true;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _tabs.dispose();
    super.dispose();
  }

  String _message(Object error) =>
      error is ApiException ? error.message : error.toString();

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final repo = ref.read(studioRepositoryProvider);
      final keys = await repo.apiKeys();
      if (!mounted) return;
      setState(() => _keys = keys);
      try {
        final users = await repo.adminUsers();
        final audit = await repo.adminAudit();
        final auditRows = audit['items'];
        if (mounted) {
          setState(() {
            _users = users;
            _audit = auditRows is List
                ? auditRows
                      .whereType<Map>()
                      .map((row) => Map<String, dynamic>.from(row))
                      .toList()
                : const [];
          });
        }
      } catch (_) {
        // Admin-only tabs remain empty for regular users.
      }
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _createKey() async {
    final controller = TextEditingController();
    final name = await showDialog<String>(
      context: context,
      builder: (_) => AlertDialog(
        title: const Text('Tạo API key'),
        content: TextField(
          controller: controller,
          autofocus: true,
          decoration: const InputDecoration(labelText: 'Tên key'),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Hủy'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, controller.text),
            child: const Text('Tạo'),
          ),
        ],
      ),
    );
    controller.dispose();
    if (name == null || name.trim().isEmpty) return;
    try {
      final key = await ref
          .read(studioRepositoryProvider)
          .createApiKey(name: name);
      if (!mounted) return;
      await showDialog<void>(
        context: context,
        builder: (_) => AlertDialog(
          title: const Text('Lưu API key ngay'),
          content: SelectableText(
            key['rawKey']?.toString() ?? 'Không nhận được key.',
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(context),
              child: const Text('Đã lưu'),
            ),
          ],
        ),
      );
      await _load();
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: const Text('Operations'),
      actions: [IconButton(onPressed: _load, icon: const Icon(Icons.refresh))],
      bottom: TabBar(
        controller: _tabs,
        tabs: const [
          Tab(text: 'API Keys'),
          Tab(text: 'Users'),
          Tab(text: 'Audit'),
        ],
      ),
    ),
    body: Column(
      children: [
        if (_error != null)
          MaterialBanner(
            content: Text(_error!),
            actions: [
              TextButton(
                onPressed: () => setState(() => _error = null),
                child: const Text('Đóng'),
              ),
            ],
          ),
        Expanded(
          child: TabBarView(
            controller: _tabs,
            children: [_keysTab(), _usersTab(), _auditTab()],
          ),
        ),
      ],
    ),
  );

  Widget _keysTab() => ListView(
    padding: const EdgeInsets.all(16),
    children: [
      Row(
        children: [
          const Expanded(
            child: Text(
              'Khóa API',
              style: TextStyle(fontSize: 20, fontWeight: FontWeight.w700),
            ),
          ),
          IconButton(onPressed: _createKey, icon: const Icon(Icons.add)),
        ],
      ),
      const Text('Raw key chỉ hiển thị một lần khi tạo.'),
      if (_loading)
        const Center(
          child: Padding(
            padding: EdgeInsets.all(32),
            child: CircularProgressIndicator(),
          ),
        ),
      if (!_loading && _keys.isEmpty) const _EmptyOperation('Chưa có API key.'),
      for (final key in _keys)
        Card(
          child: ListTile(
            title: Text(key['name']?.toString() ?? ''),
            subtitle: Text(
              'Đã dùng ${key['usedRequests'] ?? 0} / ${key['maxRequests'] ?? 0} • hết hạn ${key['expiresAtUtc'] ?? ''}',
            ),
            trailing: IconButton(
              icon: const Icon(Icons.delete_outline),
              onPressed: () async {
                await ref
                    .read(studioRepositoryProvider)
                    .revokeApiKey(key['id'].toString());
                await _load();
              },
            ),
          ),
        ),
    ],
  );

  Widget _usersTab() => ListView(
    padding: const EdgeInsets.all(16),
    children: [
      const Text(
        'Người dùng trong tenant',
        style: TextStyle(fontSize: 20, fontWeight: FontWeight.w700),
      ),
      if (_users.isEmpty)
        const _EmptyOperation('Không có dữ liệu hoặc cần quyền Admin.'),
      for (final user in _users)
        Card(
          child: ListTile(
            title: Text(user['fullName']?.toString() ?? ''),
            subtitle: Text('${user['email'] ?? ''} • ${user['role'] ?? ''}'),
            trailing: Icon(
              user['isActive'] == true ? Icons.check_circle : Icons.block,
              color: user['isActive'] == true ? Colors.green : Colors.grey,
            ),
          ),
        ),
    ],
  );

  Widget _auditTab() => ListView(
    padding: const EdgeInsets.all(16),
    children: [
      const Text(
        'Audit log',
        style: TextStyle(fontSize: 20, fontWeight: FontWeight.w700),
      ),
      if (_audit.isEmpty)
        const _EmptyOperation('Không có dữ liệu audit hoặc cần quyền Admin.'),
      for (final item in _audit)
        Card(
          child: ListTile(
            title: Text(item['action']?.toString() ?? ''),
            subtitle: Text(
              '${item['actorType'] ?? ''} • ${item['entityType'] ?? ''}\n${item['createdAtUtc'] ?? ''}',
            ),
          ),
        ),
    ],
  );
}

class _EmptyOperation extends StatelessWidget {
  const _EmptyOperation(this.text);
  final String text;
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.all(32),
    child: Center(child: Text(text)),
  );
}
