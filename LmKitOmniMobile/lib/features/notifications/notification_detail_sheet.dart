import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/ui/app_controls.dart';
import '../chat/chat_models.dart';
import '../chat/chat_provider.dart';
import '../studio/run_detail_screen.dart';
import '../studio/web_sources_sheet.dart';
import 'notifications_screen.dart';

/// Bottom sheet xem chi tiết một thông báo: nội dung đầy đủ (đã bỏ marker giao
/// thức), file agent tạo ra (tap để tải/xem) và nguồn web đã đọc dạng
/// "Đã đọc N trang web" — đồng bộ với cách web tổ chức nguồn tham khảo.
class NotificationDetailSheet extends ConsumerStatefulWidget {
  const NotificationDetailSheet({super.key, required this.item});

  final NotificationModel item;

  @override
  ConsumerState<NotificationDetailSheet> createState() =>
      _NotificationDetailSheetState();
}

class _NotificationDetailSheetState
    extends ConsumerState<NotificationDetailSheet> {
  bool _downloading = false;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final parsed = parseStoredContent(widget.item.body);

    return SafeArea(
      child: ConstrainedBox(
        constraints: BoxConstraints(
          maxHeight: MediaQuery.sizeOf(context).height * 0.85,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 8, 20, 4),
              child: Text(
                widget.item.title,
                style: theme.textTheme.titleMedium?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
              ),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 0, 20, 8),
              child: Text(
                NotificationItem.relativeTime(widget.item.createdAt),
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
            ),
            Flexible(
              child: SingleChildScrollView(
                padding: const EdgeInsets.fromLTRB(20, 0, 20, 8),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  spacing: 12,
                  children: [
                    if (parsed.content.trim().isNotEmpty)
                      Container(
                        width: double.infinity,
                        padding: const EdgeInsets.all(14),
                        decoration: BoxDecoration(
                          color: theme.colorScheme.surfaceContainerHighest
                              .withValues(alpha: 0.5),
                          borderRadius: BorderRadius.circular(12),
                        ),
                        child: SelectableText(parsed.content.trim()),
                      ),
                    if (parsed.files.isNotEmpty) ...[
                      Text(
                        'Tệp đính kèm',
                        style: theme.textTheme.titleSmall?.copyWith(
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                      for (final file in parsed.files)
                        _FileTile(
                          file: file,
                          downloading: _downloading,
                          onDownload: () => _download(context, file),
                        ),
                    ],
                    if (parsed.webUrls.isNotEmpty)
                      Align(
                        alignment: Alignment.centerLeft,
                        child: WebSourcesChip(
                          title: widget.item.title,
                          sources: [
                            for (final url in parsed.webUrls)
                              (url: url, title: ''),
                          ],
                        ),
                      ),
                  ],
                ),
              ),
            ),
            if (widget.item.agentRunId != null &&
                widget.item.agentRunId!.isNotEmpty)
              Padding(
                padding: const EdgeInsets.fromLTRB(20, 4, 20, 12),
                child: OutlinedButton.icon(
                  onPressed: () {
                    Navigator.of(context).pop();
                    Navigator.of(context).push(
                      MaterialPageRoute<void>(
                        builder: (_) => RunDetailScreen(
                          runId: widget.item.agentRunId!,
                          goal: widget.item.title,
                        ),
                      ),
                    );
                  },
                  icon: const Icon(Icons.timeline),
                  label: const Text('Xem chi tiết lần chạy'),
                ),
              ),
          ],
        ),
      ),
    );
  }

  Future<void> _download(BuildContext context, ProducedFileModel file) async {
    if (_downloading) return;
    setState(() => _downloading = true);
    try {
      final path = await ref
          .read(chatRepositoryProvider)
          .downloadProducedFile(file);
      if (context.mounted) {
        showAppSnack(context, 'Đã tải: $path');
      }
    } catch (error) {
      if (context.mounted) showAppSnack(context, 'Lỗi tải file: $error');
    } finally {
      if (mounted) setState(() => _downloading = false);
    }
  }
}

/// Dòng file đính kèm trong chi tiết thông báo.
class _FileTile extends StatelessWidget {
  const _FileTile({
    required this.file,
    required this.downloading,
    required this.onDownload,
  });

  final ProducedFileModel file;
  final bool downloading;
  final VoidCallback onDownload;

  String get _sizeLabel {
    if (file.size <= 0) return '';
    if (file.size < 1024) return '${file.size} B';
    if (file.size < 1024 * 1024) return '${(file.size / 1024).toStringAsFixed(1)} KB';
    return '${(file.size / (1024 * 1024)).toStringAsFixed(1)} MB';
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    return ListTile(
      contentPadding: const EdgeInsets.symmetric(horizontal: 8),
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: BorderSide(color: scheme.outlineVariant),
      ),
      leading: Icon(
        Icons.insert_drive_file_outlined,
        color: scheme.primary,
      ),
      title: Text(
        file.name,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: theme.textTheme.bodyMedium?.copyWith(
          fontWeight: FontWeight.w600,
        ),
      ),
      subtitle: _sizeLabel.isEmpty ? null : Text(_sizeLabel),
      trailing: downloading
          ? const SizedBox(
              width: 18,
              height: 18,
              child: CircularProgressIndicator(strokeWidth: 2),
            )
          : Icon(Icons.download_outlined, color: scheme.primary),
      onTap: downloading ? null : onDownload,
    );
  }
}

/// Tiện ích thời gian tương đối dùng chung cho sheet chi tiết.
extension NotificationItem on NotificationModel {
  static String relativeTime(DateTime? value) {
    if (value == null) return '';
    final local = value.toLocal();
    final diff = DateTime.now().difference(local);
    if (diff.inMinutes < 1) return 'vừa xong';
    if (diff.inHours < 1) return '${diff.inMinutes} phút trước';
    if (diff.inDays < 1) return '${diff.inHours} giờ trước';
    if (diff.inDays < 7) return '${diff.inDays} ngày trước';
    String two(int number) => number.toString().padLeft(2, '0');
    return '${two(local.day)}/${two(local.month)}/${local.year}';
  }
}
