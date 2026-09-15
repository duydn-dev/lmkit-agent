import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_exception.dart';
import 'workspace_models.dart';
import 'workspace_provider.dart';

class MemoryScreen extends ConsumerStatefulWidget {
  const MemoryScreen({super.key});
  @override
  ConsumerState<MemoryScreen> createState() => _MemoryScreenState();
}

class _MemoryScreenState extends ConsumerState<MemoryScreen> {
  List<MemoryModel> _memories = const [];
  bool _loading = true;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() => _loading = true);
    try {
      final memories = await ref.read(workspaceRepositoryProvider).memories();
      if (mounted) setState(() => _memories = memories);
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _confirm(MemoryModel memory) async {
    try {
      await ref.read(workspaceRepositoryProvider).confirmMemory(memory.id);
      await _load();
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    }
  }

  Future<void> _forget(MemoryModel memory) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Quên thông tin này?'),
        content: Text(memory.memoryKey),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Hủy'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Quên'),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    try {
      await ref.read(workspaceRepositoryProvider).forgetMemory(memory.id);
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
      title: const Text('Agent Memory'),
      actions: [
        IconButton(
          onPressed: _load,
          tooltip: 'Làm mới',
          icon: const Icon(Icons.refresh),
        ),
      ],
    ),
    body: RefreshIndicator(
      onRefresh: _load,
      child: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          if (_error != null)
            Card(
              color: Theme.of(context).colorScheme.errorContainer,
              child: ListTile(
                title: Text(_error!),
                trailing: IconButton(
                  onPressed: () => setState(() => _error = null),
                  icon: const Icon(Icons.close),
                ),
              ),
            ),
          if (_loading)
            const Padding(
              padding: EdgeInsets.all(40),
              child: Center(child: CircularProgressIndicator()),
            )
          else if (_memories.isEmpty)
            const Padding(
              padding: EdgeInsets.all(40),
              child: Center(child: Text('Trợ lý chưa lưu thông tin nào.')),
            )
          else ...[
            for (final memory in _memories)
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(12),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Wrap(
                        spacing: 8,
                        runSpacing: 4,
                        children: [
                          Chip(label: Text(memory.memoryType)),
                          Chip(
                            label: Text(
                              memory.isConfirmed
                                  ? 'Đã xác nhận'
                                  : 'Chờ xác nhận',
                            ),
                          ),
                        ],
                      ),
                      const SizedBox(height: 8),
                      Text(
                        memory.memoryKey,
                        style: const TextStyle(fontWeight: FontWeight.w700),
                      ),
                      const SizedBox(height: 6),
                      Text(memory.memoryValue),
                      const SizedBox(height: 8),
                      Row(
                        mainAxisAlignment: MainAxisAlignment.end,
                        children: [
                          if (!memory.isConfirmed)
                            IconButton(
                              onPressed: () => _confirm(memory),
                              tooltip: 'Xác nhận',
                              icon: const Icon(
                                Icons.check,
                                color: Colors.green,
                              ),
                            ),
                          IconButton(
                            onPressed: () => _forget(memory),
                            tooltip: 'Quên',
                            icon: const Icon(Icons.delete_outline),
                          ),
                        ],
                      ),
                    ],
                  ),
                ),
              ),
          ],
        ],
      ),
    ),
  );
}
