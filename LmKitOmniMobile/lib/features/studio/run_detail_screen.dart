import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/ui/app_controls.dart';
import '../../core/network/api_exception.dart';
import 'studio_models.dart';
import 'studio_provider.dart';

/// Chi tiết một agent run: trạng thái, kết quả và log từng bước.
class RunDetailScreen extends ConsumerStatefulWidget {
  const RunDetailScreen({super.key, required this.runId, this.goal});

  final String runId;
  final String? goal;

  @override
  ConsumerState<RunDetailScreen> createState() => _RunDetailScreenState();
}

/// Giờ:phút:giây theo giờ địa phương.
String _time(DateTime value) {
  final local = value.toLocal();
  return '${local.hour.toString().padLeft(2, '0')}:'
      '${local.minute.toString().padLeft(2, '0')}:'
      '${local.second.toString().padLeft(2, '0')}';
}

class _RunDetailScreenState extends ConsumerState<RunDetailScreen> {
  AgentRunDetailModel? _run;
  bool _loading = true;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() {
      _loading = _run == null;
      _error = null;
    });
    try {
      final run = await ref
          .read(studioRepositoryProvider)
          .agentRun(widget.runId);
      if (mounted) setState(() => _run = run);
    } catch (error) {
      if (mounted) {
        setState(
          () =>
              _error = error is ApiException ? error.message : error.toString(),
        );
      }
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _cancel() async {
    final confirmed = await confirmAppAction(
      context,
      title: 'Dừng agent run',
      message: 'Agent sẽ dừng ở bước tiếp theo và không hoàn tất mục tiêu.',
      confirmLabel: 'Dừng',
    );
    if (!confirmed) return;
    try {
      await ref.read(studioRepositoryProvider).cancelAgentRun(widget.runId);
      await _load();
      if (mounted) showAppSnack(context, 'Đã gửi yêu cầu dừng.');
    } catch (error) {
      if (mounted) {
        showAppSnack(
          context,
          error is ApiException ? error.message : error.toString(),
        );
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final run = _run;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Chi tiết Agent Run'),
        actions: [
          IconButton(
            tooltip: 'Làm mới',
            onPressed: _load,
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: _loading
          ? const Center(child: CircularProgressIndicator())
          : RefreshIndicator(
              onRefresh: _load,
              child: ListView(
                padding: const EdgeInsets.all(16),
                children: [
                  if (_error != null)
                    AppAlert(message: _error!, isError: true, onRetry: _load),
                  if (run == null && _error == null)
                    const Text('Không tìm thấy agent run.'),
                  if (run != null) ...[
                    AppCard(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          AppSectionTitle(title: 'Mục tiêu'),
                          Text(run.goal),
                          const SizedBox(height: 12),
                          Wrap(
                            spacing: 8,
                            runSpacing: 8,
                            children: [
                              Chip(
                                avatar: const Icon(Icons.bolt, size: 16),
                                label: Text(run.status),
                              ),
                              Chip(
                                avatar: const Icon(Icons.list_alt, size: 16),
                                label: Text('${run.steps.length} bước'),
                              ),
                            ],
                          ),
                          const SizedBox(height: 8),
                          if (run.createdAtUtc != null)
                            Text(
                              'Bắt đầu: ${run.createdAtUtc!.toLocal()}',
                              style: Theme.of(context).textTheme.bodySmall,
                            ),
                          if (run.completedAtUtc != null)
                            Text(
                              'Kết thúc: ${run.completedAtUtc!.toLocal()}',
                              style: Theme.of(context).textTheme.bodySmall,
                            ),
                          if (run.isRunning) ...[
                            const SizedBox(height: 12),
                            AppDestructiveButton(
                              label: 'Dừng agent run',
                              icon: Icons.stop_circle_outlined,
                              onPressed: _cancel,
                            ),
                          ],
                        ],
                      ),
                    ),
                    if (run.error?.isNotEmpty == true)
                      AppCard(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            const AppSectionTitle(title: 'Lỗi'),
                            SelectableText(run.error!),
                          ],
                        ),
                      ),
                    if (run.result?.isNotEmpty == true)
                      AppCard(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            const AppSectionTitle(title: 'Kết quả'),
                            SelectableText(run.result!),
                          ],
                        ),
                      ),
                    AppCard(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          const AppSectionTitle(
                            title: 'Log từng bước',
                            subtitle:
                                'Hành động, đầu vào và quan sát của agent.',
                          ),
                          if (run.steps.isEmpty)
                            const AppEmptyState(
                              icon: Icons.timeline,
                              message: 'Chưa có bước nào được ghi nhận.',
                              hint:
                                  'Các bước hiện dần khi agent chạy; mở lại màn này '
                                  'sau khi tác vụ bắt đầu.',
                            )
                          else
                            for (final step in run.steps)
                              Padding(
                                padding: const EdgeInsets.only(bottom: 12),
                                child: Column(
                                  crossAxisAlignment: CrossAxisAlignment.start,
                                  children: [
                                    Row(
                                      children: [
                                        CircleAvatar(
                                          radius: 12,
                                          child: Text(
                                            '${step.ordinal}',
                                            style: Theme.of(
                                              context,
                                            ).textTheme.labelSmall,
                                          ),
                                        ),
                                        const SizedBox(width: 8),
                                        Expanded(
                                          child: Text(
                                            step.action.isEmpty
                                                ? 'Bước ${step.ordinal}'
                                                : step.action,
                                            style: const TextStyle(
                                              fontWeight: FontWeight.w600,
                                            ),
                                          ),
                                        ),
                                        if (step.createdAtUtc != null)
                                          Text(
                                            _time(step.createdAtUtc!),
                                            style: Theme.of(
                                              context,
                                            ).textTheme.bodySmall,
                                          ),
                                      ],
                                    ),
                                    if (step.input.isNotEmpty)
                                      Padding(
                                        padding: const EdgeInsets.only(
                                          top: 6,
                                          left: 32,
                                        ),
                                        child: SelectableText(
                                          'Đầu vào: ${step.input}',
                                          style: Theme.of(
                                            context,
                                          ).textTheme.bodySmall,
                                        ),
                                      ),
                                    if (step.observation.isNotEmpty)
                                      Padding(
                                        padding: const EdgeInsets.only(
                                          top: 4,
                                          left: 32,
                                        ),
                                        child: SelectableText(
                                          'Quan sát: ${step.observation}',
                                          style: Theme.of(
                                            context,
                                          ).textTheme.bodySmall,
                                        ),
                                      ),
                                  ],
                                ),
                              ),
                        ],
                      ),
                    ),
                  ],
                ],
              ),
            ),
    );
  }
}
