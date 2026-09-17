import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_provider.dart';
import '../../core/network/api_exception.dart';
import 'chat_models.dart';
import 'chat_provider.dart';
import '../../app/ui/app_controls.dart';
import '../../app/ui/tenant_logo.dart';

/// Lịch sử phiên chat: tìm kiếm phía server, đổi tên và xoá.
class ChatSessionDrawer extends ConsumerStatefulWidget {
  const ChatSessionDrawer({
    super.key,
    required this.currentSessionId,
    required this.onSelect,
    required this.onNewChat,
  });

  final String? currentSessionId;
  final ValueChanged<ChatSessionModel> onSelect;
  final VoidCallback onNewChat;

  @override
  ConsumerState<ChatSessionDrawer> createState() => _ChatSessionDrawerState();
}

class _ChatSessionDrawerState extends ConsumerState<ChatSessionDrawer> {
  final _search = TextEditingController();
  Timer? _debounce;
  String _query = '';
  String? _error;

  @override
  void dispose() {
    _debounce?.cancel();
    _search.dispose();
    super.dispose();
  }

  void _onQueryChanged(String value) {
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 350), () {
      if (mounted) setState(() => _query = value);
    });
  }

  Future<void> _rename(ChatSessionModel session) async {
    final controller = TextEditingController(text: session.title ?? '');
    final title = await showAppDialog<String>(
      context,
      title: 'Đổi tên đoạn chat',
      content: AppTextField(
        controller: controller,
        autofocus: true,
        label: 'Tiêu đề',
      ),
      actions: [
        AppSecondaryButton(
          label: 'Huỷ',
          onPressed: () => Navigator.pop(context),
        ),
        const SizedBox(width: 10),
        AppPrimaryButton(
          label: 'Lưu',
          onPressed: () => Navigator.pop(context, controller.text.trim()),
          expand: false,
        ),
      ],
    );
    controller.dispose();
    if (title == null || title.isEmpty) return;

    try {
      await ref.read(chatRepositoryProvider).renameSession(session.id, title);
      ref.invalidate(chatSessionSearchProvider);
      ref.invalidate(chatSessionsProvider);
    } catch (error) {
      if (mounted) setState(() => _error = _messageFor(error));
    }
  }

  Future<void> _delete(ChatSessionModel session) async {
    final confirmed = await confirmAppAction(
      context,
      title: 'Xoá đoạn chat',
      message: 'Toàn bộ tin nhắn trong đoạn chat này sẽ bị xoá.',
      confirmLabel: 'Xoá',
    );
    if (confirmed != true) return;

    try {
      await ref.read(chatRepositoryProvider).deleteSession(session.id);
      ref.invalidate(chatSessionSearchProvider);
      ref.invalidate(chatSessionsProvider);
    } catch (error) {
      if (mounted) setState(() => _error = _messageFor(error));
    }
  }

  String _messageFor(Object error) =>
      error is ApiException ? error.message : error.toString();

  @override
  Widget build(BuildContext context) {
    final sessions = ref.watch(chatSessionSearchProvider(_query));
    final user = ref.watch(authControllerProvider).asData?.value?.user;
    final tenantName = user?.tenant?.name.trim() ?? '';

    return Drawer(
      child: SafeArea(
        child: Column(
          children: [
            // Đầu ngăn kéo là **danh tính đơn vị** (logo + tên đơn vị + tên trợ
            // lý) — đúng chỗ sidebar web đặt nó. Header chính chỉ đọc tên trang
            // nên khối này giữ lại dấu hiệu "đang dùng hệ thống của cơ quan
            // nào", mà không phải nhồi tên đơn vị vào thanh tiêu đề rồi cắt
            // giữa từ.
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 16, 12, 6),
              child: Row(
                children: [
                  TenantLogo(path: user?.tenant?.logoUrl, size: 34),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        // Tên đơn vị để **nguyên vẹn**, xuống dòng bao nhiêu
                        // cũng được. Tên cơ quan nhà nước thường 60–80 ký tự và
                        // khác nhau ở *cuối* câu ("…môi trường quốc gia"), nên
                        // cắt bằng `…` là cắt đúng phần phân biệt đơn vị này với
                        // đơn vị khác — người dùng chỉ thấy các đơn vị na ná
                        // nhau. Bề ngang ngăn kéo có hạn nhưng đây là chỗ duy
                        // nhất trong app nói rõ đang ở hệ thống của ai.
                        Text(
                          tenantName.isEmpty
                              ? 'Trợ lý ảo - CILA AI'
                              : tenantName,
                          style: Theme.of(context).textTheme.titleSmall,
                        ),
                        // Tên trợ lý hiện **nguyên văn**, không thêm tiền tố "Trợ
                        // lý:": tên do đơn vị đặt ("Trợ lý CILA", "CILA - AI
                        // Agent") nên ghép thêm là ra "Trợ lý: Trợ lý CILA". Web
                        // cũng chỉ in `agentName`.
                        Text(
                          user?.agentName ?? '',
                          style: Theme.of(context).textTheme.bodySmall,
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
            const Divider(height: 1),
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 12, 8, 4),
              child: Row(
                children: [
                  Expanded(
                    child: Text(
                      'Lịch sử chat',
                      // 14 đậm: cùng thang với tiêu đề phiên bên dưới, để tiêu đề
                      // ngăn kéo không chiếm hơn nửa bề ngang màn 320dp.
                      style: Theme.of(context).textTheme.titleSmall,
                    ),
                  ),
                  AppIconButton(
                    icon: Icons.add_comment_outlined,
                    tooltip: 'Chat mới',
                    onPressed: widget.onNewChat,
                  ),
                ],
              ),
            ),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12),
              child: AppTextField(
                controller: _search,
                label: 'Tìm đoạn chat',
                hint: 'Theo tiêu đề hoặc nội dung',
                icon: Icons.search,
                onChanged: _onQueryChanged,
              ),
            ),
            if (_error != null)
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 0),
                child: Text(
                  _error!,
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
              ),
            const SizedBox(height: 8),
            const Divider(height: 1),
            Expanded(
              child: sessions.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (error, _) => Center(
                  child: Padding(
                    padding: const EdgeInsets.all(24),
                    child: Text(
                      _messageFor(error),
                      textAlign: TextAlign.center,
                    ),
                  ),
                ),
                data: (items) => items.isEmpty
                    ? AppEmptyState(
                        icon: _query.isEmpty
                            ? Icons.chat_bubble_outline
                            : Icons.search_off,
                        message: _query.isEmpty
                            ? 'Chưa có đoạn chat nào.'
                            : 'Không tìm thấy đoạn chat phù hợp.',
                        hint: _query.isEmpty
                            ? 'Gửi tin nhắn đầu tiên để bắt đầu lịch sử trò chuyện.'
                            : null,
                      )
                    : ListView.builder(
                        itemCount: items.length,
                        itemBuilder: (context, index) {
                          final session = items[index];
                          return AppTile(
                            // `FTile` mặc định lấy tiêu đề ở `typography.body.sm`
                            // (16 khi bật `touch`) và phụ đề 12 — tiêu đề 16 làm
                            // danh sách phiên nặng hơn hẳn phần còn lại của ngăn
                            // kéo. Ép về đúng thang chữ của app (14/12) bằng
                            // style tường minh trên `Text`.
                            prefix: const Icon(Icons.chat_bubble_outline),
                            title: Text(
                              session.title?.trim().isNotEmpty == true
                                  ? session.title!
                                  : 'Đoạn chat mới',
                              maxLines: 2,
                              overflow: TextOverflow.ellipsis,
                              style: Theme.of(context).textTheme.bodyMedium,
                            ),
                            subtitle: Text(
                              _formatDate(session.createdAt),
                              style: Theme.of(context).textTheme.bodySmall,
                            ),
                            onTap: () => widget.onSelect(session),
                            suffix: AppMenuButton(
                              tooltip: 'Tuỳ chọn',
                              items: [
                                AppMenuItem('Đổi tên', () => _rename(session)),
                                AppMenuItem(
                                  'Xoá',
                                  () => _delete(session),
                                  destructive: true,
                                ),
                              ],
                            ),
                          );
                        },
                      ),
              ),
            ),
          ],
        ),
      ),
    );
  }

  static String _formatDate(DateTime? value) {
    if (value == null) return '';
    final local = value.toLocal();
    final now = DateTime.now();
    final today = DateTime(now.year, now.month, now.day);
    final day = DateTime(local.year, local.month, local.day);
    final difference = today.difference(day).inDays;
    final time =
        '${local.hour.toString().padLeft(2, '0')}:'
        '${local.minute.toString().padLeft(2, '0')}';
    if (difference == 0) return 'Hôm nay $time';
    if (difference == 1) return 'Hôm qua $time';
    return '${local.day.toString().padLeft(2, '0')}/'
        '${local.month.toString().padLeft(2, '0')}/${local.year} $time';
  }
}
