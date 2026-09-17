import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/ui/app_controls.dart';
import '../chat/chat_message_view.dart';
import '../chat/chat_models.dart';
import 'share_repository.dart';
import 'shared_chat_models.dart';

/// Màn đọc công khai cho link chia sẻ (`/share/:token` bên web).
///
/// Không cần đăng nhập. Nếu mở từ trong app mà chưa biết token, người dùng dán
/// link hoặc token vào ô nhập.
class SharedChatScreen extends ConsumerStatefulWidget {
  const SharedChatScreen({super.key, this.initialToken});

  final String? initialToken;

  @override
  ConsumerState<SharedChatScreen> createState() => _SharedChatScreenState();
}

class _SharedChatScreenState extends ConsumerState<SharedChatScreen> {
  final _input = TextEditingController();

  SharedChatResult? _result;
  bool _loading = false;

  @override
  void initState() {
    super.initState();
    final token = widget.initialToken;
    if (token != null && token.trim().isNotEmpty) {
      _input.text = token;
      WidgetsBinding.instance.addPostFrameCallback((_) => _load());
    }
  }

  @override
  void dispose() {
    _input.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    final token = ShareRepository.extractToken(_input.text);
    if (token == null || token.isEmpty) {
      showAppSnack(context, 'Hãy dán link hoặc token chia sẻ.');
      return;
    }
    setState(() {
      _loading = true;
      _result = null;
    });
    try {
      final result = await ref.read(shareRepositoryProvider).fetch(token);
      if (mounted) setState(() => _result = result);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final result = _result;

    return Scaffold(
      appBar: AppTopBar(
        title: const Text('Đoạn chat được chia sẻ'),
        actions: [
          AppIconButton(
            icon: Icons.refresh,
            tooltip: 'Tải lại',
            onPressed: _loading ? null : _load,
          ),
        ],
      ),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          AppTextField(
            controller: _input,
            label: 'Link hoặc token chia sẻ',
            hint: 'https://.../share/abc123',
            onSubmitted: (_) => _load(),
          ),
          AppPrimaryButton(
            label: 'Mở đoạn chat',
            icon: Icons.open_in_new,
            busy: _loading,
            onPressed: _load,
          ),
          const SizedBox(height: 20),
          if (result != null) _resultView(context, result),
        ],
      ),
    );
  }

  Widget _resultView(BuildContext context, SharedChatResult result) =>
      switch (result.status) {
        SharedChatStatus.ok => Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            AppCard(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  AppSectionTitle(
                    title: result.chat?.title.trim().isNotEmpty == true
                        ? result.chat!.title
                        : 'Đoạn chat được chia sẻ',
                    subtitle: result.chat?.createdAt == null
                        ? 'Chỉ đọc'
                        : 'Chỉ đọc · ${result.chat!.createdAt!.toLocal()}',
                  ),
                  Text(
                    '${result.chat?.messages.length ?? 0} tin nhắn',
                    style: Theme.of(context).textTheme.bodySmall,
                  ),
                ],
              ),
            ),
            // Dùng chung `ChatMessageView` với màn Chat: bong bóng, biểu đồ
            // sinh tự động và citation vì vậy không bị lệch giữa hai màn.
            for (final message in result.chat?.messages ?? const [])
              ChatMessageView(
                message: ChatMessageModel(
                  role: message.role,
                  content: message.content,
                  createdAt: message.createdAt,
                ),
              ),
          ],
        ),
        SharedChatStatus.notFound => const AppAlert(
          title: 'Không tìm thấy đoạn chat',
          message: 'Link không tồn tại hoặc đã bị xoá.',
          isError: true,
        ),
        SharedChatStatus.revoked => AppAlert(
          title: 'Link đã bị thu hồi',
          message: result.refusedAtUtc == null
              ? 'Chủ đoạn chat đã thu hồi link chia sẻ này.'
              : 'Chủ đoạn chat đã thu hồi link này lúc '
                    '${result.refusedAtUtc!.toLocal()}.',
          isError: true,
        ),
        SharedChatStatus.expired => AppAlert(
          title: 'Link đã hết hạn',
          message: result.refusedAtUtc == null
              ? 'Link chia sẻ này đã hết hạn.'
              : 'Link hết hạn lúc ${result.refusedAtUtc!.toLocal()}.',
          isError: true,
        ),
        SharedChatStatus.failed => AppAlert(
          title: 'Không tải được',
          message: result.message ?? 'Lỗi không xác định.',
          isError: true,
          onRetry: _load,
        ),
      };
}
