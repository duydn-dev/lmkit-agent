import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../app/theme.dart';
import '../../app/ui/tenant_logo.dart';
import '../../core/auth/auth_provider.dart';
import '../../core/network/api_exception.dart';
import '../studio/studio_provider.dart';
import 'chat_models.dart';
import 'chat_provider.dart';
import 'generative_ui.dart';
import 'message_format.dart';
import '../../app/ui/app_controls.dart';

/// Một bong bóng tin nhắn, gồm reasoning, citation, file agent tạo ra, thẻ phê
/// duyệt HITL và các hành động trên tin nhắn cuối.
class ChatMessageView extends ConsumerWidget {
  const ChatMessageView({
    super.key,
    required this.message,
    this.onRegenerate,
    this.onEdit,
  });

  final ChatMessageModel message;

  /// Chỉ có ở tin nhắn trợ lý cuối cùng.
  final VoidCallback? onRegenerate;

  /// Chỉ có ở tin nhắn người dùng cuối cùng.
  final VoidCallback? onEdit;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final isUser = message.isUser;
    final session = ref.watch(authControllerProvider).asData?.value;
    // Trợ lý có thể nhúng biểu đồ bằng khối <chart>{json}</chart>; phần chữ được
    // tách ra để vẽ như bình thường.
    final generative = isUser
        ? GenerativeContent(text: message.content)
        : parseGenerativeContent(message.content);
    // Bong bóng theo đúng `ChatView.vue`: tin nhắn của người dùng là thẻ trắng
    // (0 80% bề ngang, `rounded-3xl rounded-tr-sm`, `shadow-sm`), còn trả lời của
    // trợ lý nằm trực tiếp trên nền trang, không có nền/hộp riêng.
    final bubble = Container(
      constraints: BoxConstraints(
        maxWidth: isUser
            ? (MediaQuery.sizeOf(context).width * 0.8).clamp(200.0, 720.0)
            : 720,
      ),
      margin: const EdgeInsets.only(bottom: 6),
      padding: isUser
          ? const EdgeInsets.symmetric(horizontal: 20, vertical: 12)
          : EdgeInsets.zero,
      decoration: isUser
          ? const BoxDecoration(
              color: Colors.white,
              borderRadius: BorderRadius.only(
                topLeft: Radius.circular(24),
                topRight: Radius.circular(4),
                bottomLeft: Radius.circular(24),
                bottomRight: Radius.circular(24),
              ),
              boxShadow: [
                BoxShadow(
                  color: Color(0x14111827),
                  blurRadius: 6,
                  offset: Offset(0, 2),
                ),
              ],
            )
          : null,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (message.thinkingSteps.isNotEmpty)
            _Collapsible(
              title: 'Quá trình suy luận',
              body: message.thinkingSteps.join('\n'),
            ),
          if (message.reasoning.isNotEmpty)
            _Collapsible(
              title: 'Suy luận của mô hình',
              body: message.reasoning,
            ),
          if (generative.text.isNotEmpty)
            FormattedMessage(
              text: generative.text,
              // Web: người dùng `text-base font-medium` (16/500), trợ lý
              // `text-base` (16) với dòng thoáng — không dùng cỡ 14 mặc định.
              style: TextStyle(
                fontSize: 16,
                color: AppTheme.textPrimary,
                fontWeight: isUser ? FontWeight.w500 : FontWeight.w400,
                height: isUser ? 1.5 : 1.6,
              ),
            ),
          for (final chart in generative.charts) GenerativeChart(spec: chart),
          if (message.webUrls.isNotEmpty) ...[
            const SizedBox(height: 8),
            _CitationList(urls: message.webUrls),
          ],
          if (message.producedFiles.isNotEmpty) ...[
            const SizedBox(height: 8),
            _ProducedFiles(files: message.producedFiles),
          ],
          if (message.approvalId != null) ...[
            const SizedBox(height: 12),
            _ApprovalPanel(approvalId: message.approvalId!),
          ],
          if (message.isTyping && message.content.isEmpty)
            const Padding(
              padding: EdgeInsets.only(top: 8),
              child: LinearProgressIndicator(),
            ),
        ],
      ),
    );

    final body = Column(
      crossAxisAlignment: isUser
          ? CrossAxisAlignment.end
          : CrossAxisAlignment.start,
      children: [
        Align(
          alignment: isUser ? Alignment.centerRight : Alignment.centerLeft,
          child: bubble,
        ),
        Padding(
          padding: const EdgeInsets.only(bottom: 10, left: 4, right: 4),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (message.content.isNotEmpty)
                _ActionButton(
                  icon: Icons.copy_all_outlined,
                  tooltip: 'Sao chép nội dung',
                  onPressed: () async {
                    await Clipboard.setData(
                      ClipboardData(text: message.content),
                    );
                    if (context.mounted) {
                      ScaffoldMessenger.of(context).showSnackBar(
                        const SnackBar(content: Text('Đã sao chép tin nhắn.')),
                      );
                    }
                  },
                ),
              if (onEdit != null)
                _ActionButton(
                  icon: Icons.edit_outlined,
                  tooltip: 'Sửa và gửi lại tin nhắn này',
                  onPressed: onEdit!,
                ),
              if (onRegenerate != null)
                _ActionButton(
                  icon: Icons.refresh,
                  tooltip: 'Tạo lại câu trả lời',
                  onPressed: onRegenerate!,
                ),
            ],
          ),
        ),
      ],
    );

    // Tin nhắn người dùng không có danh tính kèm theo (đúng web: khối người dùng
    // chỉ có bong bóng, canh phải).
    if (isUser) return body;

    // Web: mỗi câu trả lời mở đầu bằng avatar tròn 32px của tenant và tên trợ lý
    // (`font-semibold text-sm text-gray-700`) — thiếu hai thứ này thì không biết
    // đang trả lời bằng trợ lý nào.
    return Padding(
      padding: const EdgeInsets.only(bottom: 4),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          TenantLogo(path: session?.user.tenant?.logoUrl, size: 32),
          const SizedBox(width: 8),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  session?.user.agentName ?? 'CILA - AI Agent',
                  style: Theme.of(
                    context,
                  ).textTheme.titleSmall?.copyWith(color: AppTheme.textMuted),
                ),
                const SizedBox(height: 6),
                body,
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _ActionButton extends StatelessWidget {
  const _ActionButton({
    required this.icon,
    required this.tooltip,
    required this.onPressed,
  });

  final IconData icon;
  final String tooltip;
  final VoidCallback onPressed;

  /// 44×44 như `w-11 h-11` của web: dưới ngưỡng này thì vùng chạm quá nhỏ cho
  /// ngón tay, đặc biệt với các nút nằm sát nhau dưới mỗi tin nhắn.
  @override
  Widget build(BuildContext context) => SizedBox(
    height: 44,
    width: 44,
    child: IconButton(
      padding: EdgeInsets.zero,
      iconSize: 18,
      tooltip: tooltip,
      onPressed: onPressed,
      icon: Icon(icon),
    ),
  );
}

class _Collapsible extends StatelessWidget {
  const _Collapsible({required this.title, required this.body});

  final String title;
  final String body;

  // Web bọc khối suy luận trong `bg-gray-50 border border-gray-200 rounded-lg
  // p-3` — trước đây tile trần nằm trực tiếp trên nền trang nên trông lạc lõng
  // giữa các khối có viền khác.
  @override
  Widget build(BuildContext context) => Container(
    width: double.infinity,
    margin: const EdgeInsets.only(bottom: 8),
    // `Material` là bắt buộc: `ExpansionTile` dựng `ListTile` bên trong và
    // `ListTile` cần tổ tiên Material — không có thì lỗi khi tin nhắn được vẽ
    // ngoài Scaffold (test, màn chia sẻ).
    child: Material(
      color: AppTheme.surfaceMuted,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(AppTheme.radiusSmall),
        side: const BorderSide(color: AppTheme.border),
      ),
      clipBehavior: Clip.antiAlias,
      child: Theme(
        data: Theme.of(context).copyWith(dividerColor: Colors.transparent),
        child: ExpansionTile(
          tilePadding: const EdgeInsets.symmetric(horizontal: 12),
          childrenPadding: const EdgeInsets.fromLTRB(12, 0, 12, 8),
          collapsedIconColor: AppTheme.textMuted,
          iconColor: AppTheme.textMuted,
          title: Text(
            // Web: `text-[11px] font-semibold uppercase tracking-wider`.
            title.toUpperCase(),
            style: Theme.of(context).textTheme.labelSmall,
          ),
          children: [
            Align(
              alignment: Alignment.centerLeft,
              child: Text(
                body,
                style: Theme.of(
                  context,
                ).textTheme.bodySmall?.copyWith(height: 1.45),
              ),
            ),
          ],
        ),
      ),
    ),
  );
}

class _CitationList extends StatelessWidget {
  const _CitationList({required this.urls});

  final List<String> urls;

  @override
  Widget build(BuildContext context) => Wrap(
    spacing: 6,
    runSpacing: 6,
    children: [
      for (final (index, url) in urls.indexed)
        // Web: `px-3 py-1.5 rounded-xl bg-blue-50 border-blue-100 text-blue-700`.
        // Dùng `Material`+`InkWell` để vệt chạm vẽ trên chính nền chip.
        Material(
          color: AppTheme.infoSurface,
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(AppTheme.radius),
            side: const BorderSide(color: AppTheme.infoBorder),
          ),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: () =>
                launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication),
            // Trình đọc màn hình cần biết đây là liên kết mở ra ngoài và URL thật
            // là gì — web có `title` tương ứng cho từng chip nguồn.
            child: Semantics(
              button: true,
              link: true,
              label: 'Mở nguồn ${index + 1}: $url',
              child: Padding(
                padding: const EdgeInsets.symmetric(
                  horizontal: 12,
                  vertical: 8,
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    const Icon(Icons.link, size: 16, color: AppTheme.infoText),
                    const SizedBox(width: 6),
                    Text(
                      'Nguồn ${index + 1}',
                      style: Theme.of(context).textTheme.labelMedium?.copyWith(
                        color: AppTheme.infoText,
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
    ],
  );
}

class _ProducedFiles extends ConsumerStatefulWidget {
  const _ProducedFiles({required this.files});

  final List<ProducedFileModel> files;

  @override
  ConsumerState<_ProducedFiles> createState() => _ProducedFilesState();
}

class _ProducedFilesState extends ConsumerState<_ProducedFiles> {
  String? _downloadingId;

  Future<void> _download(ProducedFileModel file) async {
    setState(() => _downloadingId = file.id);
    try {
      final path = await ref
          .read(chatRepositoryProvider)
          .downloadProducedFile(file);
      if (!mounted) return;
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text('Đã lưu: $path')));
    } catch (error) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            error is ApiException ? error.message : error.toString(),
          ),
        ),
      );
    } finally {
      if (mounted) setState(() => _downloadingId = null);
    }
  }

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
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
            IconButton(
              tooltip: 'Tải về thiết bị',
              iconSize: 18,
              onPressed: _downloadingId == null ? () => _download(file) : null,
              icon: _downloadingId == file.id
                  ? const SizedBox(
                      height: 14,
                      width: 14,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.download_outlined),
            ),
          ],
        ),
    ],
  );
}

/// Thẻ phê duyệt HITL: agent đang chờ người dùng quyết định trước khi chạy tiếp.
class _ApprovalPanel extends ConsumerStatefulWidget {
  const _ApprovalPanel({required this.approvalId});

  final String approvalId;

  @override
  ConsumerState<_ApprovalPanel> createState() => _ApprovalPanelState();
}

class _ApprovalPanelState extends ConsumerState<_ApprovalPanel> {
  bool _busy = false;
  String? _decision;
  String? _error;

  Future<void> _decide({required bool approve}) async {
    var comment = '';
    if (!approve) {
      final controller = TextEditingController();
      final confirmed = await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
          title: const Text('Từ chối yêu cầu'),
          content: TextField(
            controller: controller,
            autofocus: true,
            maxLines: 3,
            decoration: const InputDecoration(
              labelText: 'Lý do (không bắt buộc)',
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(context, false),
              child: const Text('Huỷ'),
            ),
            AppPrimaryButton(
              label: 'Từ chối',
              onPressed: () => Navigator.pop(context, true),
              expand: false,
            ),
          ],
        ),
      );
      comment = controller.text.trim();
      controller.dispose();
      if (confirmed != true) return;
    }

    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final repository = ref.read(studioRepositoryProvider);
      if (approve) {
        await repository.approve(widget.approvalId);
      } else {
        await repository.reject(
          widget.approvalId,
          comment: comment.isEmpty ? null : comment,
        );
      }
      if (mounted) {
        setState(() => _decision = approve ? 'approved' : 'rejected');
      }
    } catch (error) {
      if (mounted) {
        setState(
          () =>
              _error = error is ApiException ? error.message : error.toString(),
        );
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    // Web: thẻ HITL dùng `bg-orange-50 border border-orange-200` — trước đây lấy
    // `tertiaryContainer` của Material nên ra tông tím, lạc khỏi bảng màu CP.
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: AppTheme.hitlSurface,
        border: Border.all(color: AppTheme.hitlBorder),
        borderRadius: BorderRadius.circular(AppTheme.radius),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(
                Icons.verified_user_outlined,
                size: 18,
                color: AppTheme.hitlText,
              ),
              const SizedBox(width: 8),
              Text(
                'Cần bạn phê duyệt',
                style: Theme.of(
                  context,
                ).textTheme.titleSmall?.copyWith(color: AppTheme.hitlText),
              ),
            ],
          ),
          const SizedBox(height: 6),
          Text(
            _decision == null
                ? 'Agent đang chờ bạn cho phép chạy hành động tiếp theo.'
                : _decision == 'approved'
                ? 'Bạn đã phê duyệt yêu cầu này.'
                : 'Bạn đã từ chối yêu cầu này.',
            style: Theme.of(context).textTheme.bodyMedium,
          ),
          if (_error != null) ...[
            const SizedBox(height: 8),
            Text(
              _error!,
              style: Theme.of(
                context,
              ).textTheme.bodyMedium?.copyWith(color: scheme.error),
            ),
          ],
          if (_decision == null) ...[
            const SizedBox(height: 10),
            Row(
              children: [
                AppPrimaryButton(
                  label: 'Phê duyệt',
                  icon: Icons.check,
                  onPressed: _busy ? null : () => _decide(approve: true),
                  expand: false,
                ),
                const SizedBox(width: 8),
                AppSecondaryButton(
                  label: 'Từ chối',
                  icon: Icons.close,
                  onPressed: _busy ? null : () => _decide(approve: false),
                ),
                if (_busy) ...[
                  const SizedBox(width: 12),
                  const SizedBox(
                    height: 16,
                    width: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  ),
                ],
              ],
            ),
          ],
        ],
      ),
    );
  }
}
