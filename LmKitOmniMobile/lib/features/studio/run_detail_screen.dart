import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/ui/app_controls.dart';
import '../../core/network/api_exception.dart';
import '../chat/chat_models.dart';
import '../chat/chat_provider.dart';
import 'studio_models.dart';
import 'studio_provider.dart';
import 'web_sources_sheet.dart';

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
      appBar: AppTopBar(
        title: const Text('Chi tiết Agent Run'),
        actions: [
          AppIconButton(
            icon: Icons.refresh,
            tooltip: 'Làm mới',
            onPressed: _load,
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
                    // File do run tạo ra — persisted trên run row nên scheduled run
                    // lịch sử vẫn tải được về thiết bị.
                    if (run.producedFiles.isNotEmpty)
                      _RunProducedFiles(files: run.producedFiles),
                    // Nguồn web run đã tra cứu — cùng dữ liệu với chip chat live,
                    // persisted cho scheduled run.
                    if (run.webSources.isNotEmpty)
                      _RunWebSources(urls: run.webSources),
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

/// Nguồn web run đã tra cứu: đếm số trang, mở bottom sheet liệt kê từng URL,
/// tap vào thì mở trình duyệt ngoài — cùng phong cách chip "Đã đọc N trang web" của chat.
class _RunWebSources extends StatelessWidget {
  const _RunWebSources({required this.urls});

  final List<String> urls;

  @override
  Widget build(BuildContext context) => AppCard(
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const AppSectionTitle(
          title: 'Nguồn tham khảo',
          subtitle: 'Trang web agent đã đọc trong lần chạy này.',
        ),
        Padding(
          padding: const EdgeInsets.only(top: 4),
          child: WebSourcesChip(
            title: 'Nguồn tham khảo',
            sources: [for (final url in urls) (url: url, title: '')],
          ),
        ),
      ],
    ),
  );
}

/// File do agent run tạo ra (run_python, soạn thảo Office…): tải về qua
/// /api/files/{id} — cùng đường với file trong chat, dùng chung ChatRepository.
class _RunProducedFiles extends ConsumerStatefulWidget {
  const _RunProducedFiles({required this.files});

  final List<ProducedFileModel> files;

  @override
  ConsumerState<_RunProducedFiles> createState() => _RunProducedFilesState();
}

class _RunProducedFilesState extends ConsumerState<_RunProducedFiles> {
  String? _downloadingId;

  Future<void> _download(ProducedFileModel file) async {
    setState(() => _downloadingId = file.id);
    try {
      final path = await ref
          .read(chatRepositoryProvider)
          .downloadProducedFile(file);
      if (!mounted) return;
      showAppSnack(context, 'Đã lưu: $path');
    } catch (error) {
      if (!mounted) return;
      showAppSnack(
        context,
        error is ApiException ? error.message : error.toString(),
      );
    } finally {
      if (mounted) setState(() => _downloadingId = null);
    }
  }

  @override
  Widget build(BuildContext context) => AppCard(
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const AppSectionTitle(
          title: 'Tệp kết quả',
          subtitle: 'File do agent tạo ra trong lần chạy này.',
        ),
        for (final file in widget.files)
          Row(
            children: [
              const Icon(Icons.insert_drive_file_outlined, size: 18),
              const SizedBox(width: 6),
              Expanded(
                child: Text(
                  file.name,
                  style: Theme.of(context).textTheme.bodyMedium,
                  overflow: TextOverflow.ellipsis,
                ),
              ),
              AppIconButton(
                tooltip: 'Tải về thiết bị',
                icon: Icons.download_outlined,
                busy: _downloadingId == file.id,
                onPressed: _downloadingId == null
                    ? () => _download(file)
                    : null,
              ),
            ],
          ),
      ],
    ),
  );
}
