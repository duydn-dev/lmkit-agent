import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_exception.dart';
import 'chat_models.dart';
import 'chat_provider.dart';
import '../../app/ui/app_controls.dart';

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
    final title = await showDialog<String>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Đổi tên đoạn chat'),
        content: TextField(
          controller: controller,
          autofocus: true,
          maxLength: 100,
          decoration: const InputDecoration(labelText: 'Tiêu đề'),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Huỷ'),
          ),
          AppPrimaryButton(
            label: 'Lưu',
            onPressed: () => Navigator.pop(context, controller.text.trim()),
            expand: false,
          ),
        ],
      ),
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
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Xoá đoạn chat'),
        content: const Text('Toàn bộ tin nhắn trong đoạn chat này sẽ bị xoá.'),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Huỷ'),
          ),
          AppPrimaryButton(
            label: 'Xoá',
            onPressed: () => Navigator.pop(context, true),
            expand: false,
          ),
        ],
      ),
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

    return Drawer(
      child: SafeArea(
        child: Column(
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(12, 12, 12, 4),
              child: Row(
                children: [
                  Expanded(
                    child: Text(
                      'Lịch sử chat',
                      style: Theme.of(context).textTheme.titleLarge,
                    ),
                  ),
                  IconButton(
                    tooltip: 'Chat mới',
                    onPressed: widget.onNewChat,
                    icon: const Icon(Icons.add_comment_outlined),
                  ),
                ],
              ),
            ),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12),
              child: TextField(
                controller: _search,
                onChanged: _onQueryChanged,
                decoration: InputDecoration(
                  isDense: true,
                  hintText: 'Tìm theo tiêu đề hoặc nội dung',
                  prefixIcon: const Icon(Icons.search, size: 20),
                  suffixIcon: _query.isEmpty
                      ? null
                      : IconButton(
                          tooltip: 'Xoá từ khoá',
                          onPressed: () {
                            _search.clear();
                            setState(() => _query = '');
                          },
                          icon: const Icon(Icons.close, size: 18),
                        ),
                  border: const OutlineInputBorder(),
                ),
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
                          return ListTile(
                            selected: session.id == widget.currentSessionId,
                            leading: const Icon(Icons.chat_bubble_outline),
                            title: Text(
                              session.title?.trim().isNotEmpty == true
                                  ? session.title!
                                  : 'Đoạn chat mới',
                              maxLines: 2,
                              overflow: TextOverflow.ellipsis,
                            ),
                            subtitle: Text(_formatDate(session.createdAt)),
                            onTap: () => widget.onSelect(session),
                            trailing: PopupMenuButton<String>(
                              tooltip: 'Tuỳ chọn',
                              onSelected: (value) => value == 'rename'
                                  ? _rename(session)
                                  : _delete(session),
                              itemBuilder: (context) => const [
                                PopupMenuItem(
                                  value: 'rename',
                                  child: Text('Đổi tên'),
                                ),
                                PopupMenuItem(
                                  value: 'delete',
                                  child: Text('Xoá'),
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
