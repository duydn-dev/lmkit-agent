import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

/// Một nguồn web agent đã đọc (marker `[WEB_SEARCH]` của run): đường dẫn +
/// tiêu đề trang (nếu có).
typedef WebSourceEntry = ({String url, String title});

/// Panel trượt từ phải — cùng tương tác với drawer "Nguồn tham khảo" của web:
/// liệt kê từng trang đã đọc kèm favicon, tap một dòng là mở trình duyệt ngoài.
///
/// Dùng chung cho chat, chi tiết lần chạy (Agent Runs) và modal thông báo.
class WebSourcesPanel extends StatelessWidget {
  const WebSourcesPanel({
    super.key,
    required this.title,
    required this.sources,
  });

  final String title;
  final List<WebSourceEntry> sources;

  /// Mở panel nguồn trượt từ phải, có mờ nền phía sau.
  static Future<void> showWebSourcesDrawer(
    BuildContext context, {
    required String title,
    required List<WebSourceEntry> sources,
  }) {
    return showGeneralDialog<void>(
      context: context,
      barrierDismissible: true,
      barrierLabel: 'Đóng danh sách nguồn',
      barrierColor: Colors.black54,
      transitionDuration: const Duration(milliseconds: 220),
      pageBuilder: (dialogContext, _, _) => Align(
        alignment: Alignment.centerRight,
        child: WebSourcesPanel(title: title, sources: sources),
      ),
      transitionBuilder: (dialogContext, animation, _, child) => SlideTransition(
        position: Tween<Offset>(
          begin: const Offset(1, 0),
          end: Offset.zero,
        ).animate(
          CurvedAnimation(parent: animation, curve: Curves.easeOutCubic),
        ),
        child: child,
      ),
    );
  }

  String _host(String url) {
    try {
      return Uri.parse(url).host.replaceFirst(RegExp(r'^www\.'), '');
    } catch (_) {
      return url;
    }
  }

  Future<void> _open(String url) async {
    final uri = Uri.tryParse(url);
    if (uri == null) return;
    await launchUrl(uri, mode: LaunchMode.externalApplication);
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final width = (MediaQuery.sizeOf(context).width * 0.88).clamp(280.0, 380.0);

    return Material(
      color: scheme.surface,
      elevation: 8,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.horizontal(left: Radius.circular(20)),
      ),
      clipBehavior: Clip.antiAlias,
      child: SafeArea(
        left: false,
        child: SizedBox(
          width: width.toDouble(),
          height: double.infinity,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 16, 8, 0),
                child: Row(
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            'Đã đọc ${sources.length} trang web',
                            style: theme.textTheme.titleMedium?.copyWith(
                              fontWeight: FontWeight.w700,
                            ),
                          ),
                          if (title.trim().isNotEmpty)
                            Text(
                              title,
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                              style: theme.textTheme.bodySmall?.copyWith(
                                color: scheme.onSurfaceVariant,
                              ),
                            ),
                        ],
                      ),
                    ),
                    IconButton(
                      tooltip: 'Đóng',
                      icon: const Icon(Icons.close),
                      onPressed: () => Navigator.of(context).pop(),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 4),
              Divider(height: 1, color: scheme.outlineVariant),
              Expanded(
                child: ListView.separated(
                  padding: const EdgeInsets.symmetric(vertical: 6),
                  itemCount: sources.length,
                  separatorBuilder: (_, _) => Divider(
                    height: 1,
                    indent: 64,
                    color: scheme.outlineVariant,
                  ),
                  itemBuilder: (context, index) {
                    final source = sources[index];
                    final host = _host(source.url);
                    return ListTile(
                      leading: CircleAvatar(
                        radius: 16,
                        backgroundColor: scheme.surfaceContainerHighest,
                        child: ClipOval(
                          child: Image.network(
                            'https://www.google.com/s2/favicons?domain=$host&sz=64',
                            width: 22,
                            height: 22,
                            errorBuilder: (_, _, _) => Icon(
                              Icons.link,
                              size: 16,
                              color: scheme.onSurfaceVariant,
                            ),
                          ),
                        ),
                      ),
                      title: Text(
                        source.title.trim().isEmpty
                            ? host
                            : source.title,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: theme.textTheme.bodyMedium?.copyWith(
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                      subtitle: Text(
                        host,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: scheme.onSurfaceVariant,
                        ),
                      ),
                      trailing: Icon(
                        Icons.open_in_new,
                        size: 18,
                        color: scheme.onSurfaceVariant,
                      ),
                      onTap: () => _open(source.url),
                    );
                  },
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Chip "Đã đọc N trang web" kèm favicon chồng lên nhau — mở
/// [WebSourcesPanel.showWebSourcesDrawer] khi chạm. Dùng ở chat và run detail.
class WebSourcesChip extends StatelessWidget {
  const WebSourcesChip({
    super.key,
    required this.sources,
    this.title = '',
  });

  final List<WebSourceEntry> sources;
  final String title;

  String _host(String url) {
    try {
      return Uri.parse(url).host.replaceFirst(RegExp(r'^www\.'), '');
    } catch (_) {
      return url;
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return ActionChip(
      // Avatar phải nằm gọn trong khung chip: 3 favicon chồng nhau 10px,
      // mép phải của icon cuối = 2*10 + 20 = 40px == width — không tràn ra nhãn.
      avatar: SizedBox(
        width: 40,
        height: 22,
        child: Stack(
          clipBehavior: Clip.hardEdge,
          children: [
            for (final (index, entry) in sources.take(3).indexed)
              Positioned(
                left: index * 10.0,
                child: CircleAvatar(
                  radius: 10,
                  backgroundColor: theme.colorScheme.surface,
                  child: ClipOval(
                    child: Image.network(
                      'https://www.google.com/s2/favicons?domain=${_host(entry.url)}&sz=64',
                      width: 15,
                      height: 15,
                      errorBuilder: (_, _, _) => Icon(
                        Icons.link,
                        size: 11,
                        color: theme.colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ),
                ),
              ),
          ],
        ),
      ),
      label: Text('Đã đọc ${sources.length} trang web'),
      onPressed: () => WebSourcesPanel.showWebSourcesDrawer(
        context,
        title: title,
        sources: sources,
      ),
    );
  }
}
