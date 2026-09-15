import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';

import '../../core/network/api_exception.dart';
import 'workspace_models.dart';
import 'workspace_provider.dart';
import '../../app/ui/app_controls.dart';

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
            child: const Text('Huỷ'),
          ),
          AppPrimaryButton(
            label: 'Quên',
            onPressed: () => Navigator.pop(context, true),
            expand: false,
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
            AppErrorBanner(
              message: _error!,
              onDismiss: () => setState(() => _error = null),
            ),
          if (_loading)
            const Padding(
              padding: EdgeInsets.all(40),
              child: Center(child: CircularProgressIndicator()),
            )
          else if (_memories.isEmpty)
            const AppEmptyState(
              icon: Icons.psychology_alt_outlined,
              message: 'Trợ lý chưa lưu thông tin nào.',
              hint:
                  'Memory được ghi khi trợ lý rút ra điều cần nhớ lâu dài từ cuộc trò '
                  'chuyện; bạn xác nhận hoặc xoá ở đây.',
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
                        style: Theme.of(context).textTheme.titleMedium,
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
                                color: AppTheme.success,
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
