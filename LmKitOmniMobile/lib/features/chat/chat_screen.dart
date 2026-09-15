import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:forui/forui.dart';

import '../../app/theme.dart';
import '../../core/network/api_exception.dart';
import 'chat_models.dart';
import 'chat_provider.dart';

class ChatScreen extends ConsumerStatefulWidget {
  const ChatScreen({super.key});

  @override
  ConsumerState<ChatScreen> createState() => _ChatScreenState();
}

class _ChatScreenState extends ConsumerState<ChatScreen> {
  final _composer = TextEditingController();
  final _scrollController = ScrollController();
  CancelToken? _cancelToken;
  final _messages = <ChatMessageModel>[];
  String? _sessionId;
  bool _loadingMessages = false;
  bool _sending = false;
  bool _webSearch = true;
  String? _error;

  @override
  void dispose() {
    _composer.dispose();
    _scrollController.dispose();
    _cancelToken?.cancel('chat screen disposed');
    super.dispose();
  }

  Future<void> _selectSession(ChatSessionModel session) async {
    setState(() {
      _sessionId = session.id;
      _loadingMessages = true;
      _error = null;
      _messages.clear();
    });
    try {
      final messages = await ref
          .read(chatRepositoryProvider)
          .getMessages(session.id);
      if (!mounted || _sessionId != session.id) return;
      setState(() => _messages.addAll(messages));
      _scrollToBottom();
    } catch (error) {
      if (mounted) setState(() => _error = _messageFor(error));
    } finally {
      if (mounted) setState(() => _loadingMessages = false);
    }
  }

  Future<void> _newChat() async {
    setState(() {
      _sessionId = null;
      _messages.clear();
      _error = null;
    });
  }

  Future<String> _ensureSession() async {
    if (_sessionId != null) return _sessionId!;
    final session = await ref.read(chatSessionsProvider.notifier).create();
    if (mounted) setState(() => _sessionId = session.id);
    return session.id;
  }

  Future<void> _send() async {
    final text = _composer.text.trim();
    if (text.isEmpty || _sending) return;
    _composer.clear();
    setState(() {
      _error = null;
      _sending = true;
      _messages.add(ChatMessageModel(role: 'user', content: text));
      _messages.add(
        ChatMessageModel(role: 'assistant', content: '', isTyping: true),
      );
    });
    _scrollToBottom();

    _cancelToken = CancelToken();
    try {
      final sessionId = await _ensureSession();
      final assistant = _messages.last;
      await ref
          .read(chatRepositoryProvider)
          .streamMessage(
            sessionId: sessionId,
            message: text,
            enableWebSearch: _webSearch,
            cancelToken: _cancelToken,
            onEvent: (event) {
              if (!mounted) return;
              _applyEvent(assistant, event);
              _scrollToBottom();
            },
          );
      if (mounted) {
        setState(() {
          assistant.isTyping = false;
        });
        ref.invalidate(chatSessionsProvider);
      }
    } catch (error) {
      if (mounted &&
          !(error is DioException && error.type == DioExceptionType.cancel)) {
        setState(() {
          _messages.last.isTyping = false;
          _error = _messageFor(error);
        });
      }
    } finally {
      if (mounted) setState(() => _sending = false);
    }
  }

  void _applyEvent(ChatMessageModel assistant, ChatStreamEvent event) {
    switch (event.type) {
      case 'content':
        assistant.content += event.value;
        assistant.isTyping = false;
      case 'thinking':
        assistant.thinkingSteps.add(event.value);
      case 'reasoning':
        assistant.reasoning +=
            '${event.value.replaceAll(RegExp(r'[\r\n]+$'), '')}\n';
      case 'web-search':
        assistant.webUrls
          ..clear()
          ..addAll(event.value.split('|').where(_isSafeUrl));
      case 'approval':
        assistant.approvalId = event.value;
        assistant.isTyping = false;
      case 'file':
        break;
      case 'error':
        assistant.content = 'Lỗi: ${event.value}';
        assistant.isTyping = false;
      case 'done':
        assistant.isTyping = false;
      case 'agent-log':
        break;
    }
    if (mounted) setState(() {});
  }

  bool _isSafeUrl(String value) {
    final uri = Uri.tryParse(value);
    return uri != null && (uri.scheme == 'http' || uri.scheme == 'https');
  }

  void _stop() {
    _cancelToken?.cancel('user stopped generation');
    if (mounted) setState(() => _sending = false);
  }

  void _scrollToBottom() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!_scrollController.hasClients) return;
      _scrollController.animateTo(
        _scrollController.position.maxScrollExtent,
        duration: const Duration(milliseconds: 180),
        curve: Curves.easeOut,
      );
    });
  }

  String _messageFor(Object error) =>
      error is ApiException ? error.message : error.toString();

  @override
  Widget build(BuildContext context) {
    final sessions = ref.watch(chatSessionsProvider);
    return Scaffold(
      appBar: AppBar(
        title: const Text('AI Chat'),
        actions: [
          IconButton(
            tooltip: _webSearch ? 'Tắt tìm kiếm web' : 'Bật tìm kiếm web',
            onPressed: () => setState(() => _webSearch = !_webSearch),
            icon: Icon(_webSearch ? Icons.public : Icons.public_off),
          ),
          IconButton(
            onPressed: _newChat,
            tooltip: 'Chat mới',
            icon: const Icon(Icons.add),
          ),
        ],
      ),
      drawer: Drawer(
        child: SafeArea(
          child: Column(
            children: [
              ListTile(
                title: const Text('Lịch sử chat'),
                trailing: IconButton(
                  tooltip: 'Làm mới',
                  onPressed: () =>
                      ref.read(chatSessionsProvider.notifier).reload(),
                  icon: const Icon(Icons.refresh),
                ),
              ),
              const Divider(height: 1),
              Expanded(
                child: sessions.when(
                  loading: () =>
                      const Center(child: CircularProgressIndicator()),
                  error: (error, _) => Center(child: Text(_messageFor(error))),
                  data: (items) => ListView.builder(
                    itemCount: items.length,
                    itemBuilder: (context, index) {
                      final session = items[index];
                      return ListTile(
                        selected: session.id == _sessionId,
                        leading: const Icon(Icons.chat_bubble_outline),
                        title: Text(
                          session.title?.trim().isNotEmpty == true
                              ? session.title!
                              : 'Đoạn chat mới',
                          maxLines: 2,
                          overflow: TextOverflow.ellipsis,
                        ),
                        onTap: () {
                          Navigator.of(context).pop();
                          _selectSession(session);
                        },
                      );
                    },
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
      body: Column(
        children: [
          if (_error != null)
            MaterialBanner(
              content: Text(_error!),
              backgroundColor: Theme.of(context).colorScheme.errorContainer,
              actions: [
                TextButton(
                  onPressed: () => setState(() => _error = null),
                  child: const Text('Đóng'),
                ),
              ],
            ),
          Expanded(
            child: _loadingMessages
                ? const Center(child: CircularProgressIndicator())
                : _messages.isEmpty
                ? const _ChatEmptyState()
                : ListView.builder(
                    controller: _scrollController,
                    padding: const EdgeInsets.fromLTRB(16, 20, 16, 120),
                    itemCount: _messages.length,
                    itemBuilder: (context, index) =>
                        _MessageBubble(message: _messages[index]),
                  ),
          ),
          _Composer(
            controller: _composer,
            sending: _sending,
            webSearch: _webSearch,
            onWebSearchChanged: (value) => setState(() => _webSearch = value),
            onSend: _send,
            onStop: _stop,
          ),
        ],
      ),
    );
  }
}

class _ChatEmptyState extends StatelessWidget {
  const _ChatEmptyState();

  @override
  Widget build(BuildContext context) => Center(
    child: Padding(
      padding: const EdgeInsets.all(32),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.auto_awesome, size: 56, color: AppTheme.brandBlue),
          const SizedBox(height: 16),
          Text(
            'Hôm nay tôi có thể giúp gì cho bạn?',
            style: Theme.of(context).textTheme.titleLarge,
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 8),
          const Text(
            'Chat streaming, RAG, phân tích tài liệu và các công cụ AI an toàn.',
            textAlign: TextAlign.center,
          ),
        ],
      ),
    ),
  );
}

class _MessageBubble extends StatelessWidget {
  const _MessageBubble({required this.message});

  final ChatMessageModel message;

  @override
  Widget build(BuildContext context) {
    final isUser = message.isUser;
    return Align(
      alignment: isUser ? Alignment.centerRight : Alignment.centerLeft,
      child: Container(
        constraints: const BoxConstraints(maxWidth: 720),
        margin: const EdgeInsets.only(bottom: 14),
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(
          color: isUser
              ? AppTheme.brandBlue
              : Theme.of(context).colorScheme.surfaceContainerHighest,
          borderRadius: BorderRadius.circular(16),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (message.thinkingSteps.isNotEmpty)
              ExpansionTile(
                tilePadding: EdgeInsets.zero,
                childrenPadding: EdgeInsets.zero,
                title: const Text(
                  'Quá trình suy luận',
                  style: TextStyle(fontSize: 12, fontWeight: FontWeight.w600),
                ),
                children: [
                  for (final step in message.thinkingSteps)
                    Align(
                      alignment: Alignment.centerLeft,
                      child: Text(step, style: const TextStyle(fontSize: 12)),
                    ),
                ],
              ),
            if (message.reasoning.isNotEmpty)
              ExpansionTile(
                tilePadding: EdgeInsets.zero,
                childrenPadding: EdgeInsets.zero,
                title: const Text(
                  'Suy luận của mô hình',
                  style: TextStyle(fontSize: 12, fontWeight: FontWeight.w600),
                ),
                children: [
                  Align(
                    alignment: Alignment.centerLeft,
                    child: Text(
                      message.reasoning,
                      style: const TextStyle(fontSize: 12),
                    ),
                  ),
                ],
              ),
            if (message.content.isNotEmpty)
              Text(
                message.content,
                style: TextStyle(
                  color: isUser ? Colors.white : null,
                  height: 1.4,
                ),
              ),
            if (message.approvalId != null)
              Padding(
                padding: const EdgeInsets.only(top: 12),
                child: FButton(
                  onPress: () {},
                  child: const Text('Mở yêu cầu phê duyệt'),
                ),
              ),
            if (message.isTyping)
              const Padding(
                padding: EdgeInsets.only(top: 8),
                child: LinearProgressIndicator(),
              ),
          ],
        ),
      ),
    );
  }
}

class _Composer extends StatelessWidget {
  const _Composer({
    required this.controller,
    required this.sending,
    required this.webSearch,
    required this.onWebSearchChanged,
    required this.onSend,
    required this.onStop,
  });

  final TextEditingController controller;
  final bool sending;
  final bool webSearch;
  final ValueChanged<bool> onWebSearchChanged;
  final VoidCallback onSend;
  final VoidCallback onStop;

  @override
  Widget build(BuildContext context) => Material(
    elevation: 8,
    color: Theme.of(context).scaffoldBackgroundColor,
    child: SafeArea(
      top: false,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 12),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            Expanded(
              child: TextField(
                controller: controller,
                minLines: 1,
                maxLines: 5,
                textInputAction: TextInputAction.newline,
                onSubmitted: (_) => onSend(),
                decoration: const InputDecoration(
                  hintText: 'Nhắn tin cho CILA AI...',
                  border: OutlineInputBorder(),
                ),
              ),
            ),
            const SizedBox(width: 8),
            IconButton(
              tooltip: webSearch ? 'Tắt tìm kiếm web' : 'Bật tìm kiếm web',
              onPressed: () => onWebSearchChanged(!webSearch),
              icon: Icon(webSearch ? Icons.public : Icons.public_off),
            ),
            IconButton(
              tooltip: sending ? 'Dừng tạo trả lời' : 'Gửi tin nhắn',
              onPressed: sending ? onStop : onSend,
              icon: Icon(
                sending ? Icons.stop_circle_outlined : Icons.arrow_upward,
              ),
            ),
          ],
        ),
      ),
    ),
  );
}
