import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../app/theme.dart';
import '../../app/ui/app_controls.dart';
import '../../core/config/app_config_provider.dart';
import '../../core/network/api_exception.dart';
import '../canvas/canvas_panel_screen.dart';
import 'chat_message_view.dart';
import 'chat_models.dart';
import 'chat_navigation.dart';
import 'chat_provider.dart';
import 'chat_session_drawer.dart';
import 'voice_input.dart';
import 'voice_room.dart';

class ChatScreen extends ConsumerStatefulWidget {
  const ChatScreen({super.key});

  @override
  ConsumerState<ChatScreen> createState() => _ChatScreenState();
}

class _ChatScreenState extends ConsumerState<ChatScreen> {
  final _composer = TextEditingController();
  final _scrollController = ScrollController();
  final _messages = <ChatMessageModel>[];
  final _attachments = <ChatAttachmentModel>[];
  final _shareLinks = <String, ShareLinkModel>{};

  CancelToken? _cancelToken;
  String? _sessionId;
  int? _editingIndex;
  bool _loadingMessages = false;
  bool _sending = false;
  bool _webSearch = true;
  bool _saveToKnowledge = false;
  bool _ephemeral = false;
  bool _recording = false;
  bool _transcribing = false;
  String? _error;

  /// Chiều cao bàn phím ở lần dựng trước, để biết lúc nào bàn phím vừa mở.
  double _keyboardInset = 0;

  /// Khi bàn phím mở, vùng danh sách bị thu lại và tin nhắn cuối bị che — cuộn
  /// xuống đáy sau khi layout xong để người dùng vẫn thấy chỗ đang gõ.
  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final inset = MediaQuery.viewInsetsOf(context).bottom;
    if (inset == _keyboardInset) return;
    final opening = inset > _keyboardInset;
    _keyboardInset = inset;
    if (!opening) return;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _scrollToBottom();
    });
  }

  @override
  void initState() {
    super.initState();
    // Màn khác (Projects) có thể đã yêu cầu mở sẵn một phiên trước khi tab Chat
    // được dựng, nên phải đọc giá trị hiện có chứ không chỉ nghe thay đổi.
    final requested = ref.read(pendingChatSessionProvider);
    if (requested != null) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _openSessionById(requested);
      });
    }
  }

  @override
  void dispose() {
    _composer.dispose();
    _scrollController.dispose();
    _cancelToken?.cancel('chat screen disposed');
    // Rời màn khi đang ghi âm: huỷ bản ghi và xoá file tạm.
    if (_recording) {
      ref.read(voiceRecorderProvider).cancel();
    }
    super.dispose();
  }

  // ---------------------------------------------------------------- sessions

  Future<void> _selectSession(ChatSessionModel session) async {
    setState(() {
      _sessionId = session.id;
      _loadingMessages = true;
      _error = null;
      _editingIndex = null;
      _attachments.clear();
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

  /// Mở phiên theo id: dùng model đầy đủ nếu đã có trong danh sách, nếu không
  /// vẫn tải được tin nhắn vì API chỉ cần session id.
  Future<void> _openSessionById(String sessionId) async {
    ref.read(pendingChatSessionProvider.notifier).consume();
    ChatSessionModel? match;
    for (final item
        in ref.read(chatSessionsProvider).asData?.value ??
            const <ChatSessionModel>[]) {
      if (item.id == sessionId) {
        match = item;
        break;
      }
    }
    await _selectSession(
      match ?? ChatSessionModel(id: sessionId, title: null, createdAt: null),
    );
  }

  Future<void> _newChat({bool ephemeral = false}) async {
    setState(() {
      _sessionId = null;
      _messages.clear();
      _attachments.clear();
      _editingIndex = null;
      _error = null;
      _ephemeral = ephemeral;
    });
    if (!ephemeral) return;
    try {
      final session = await ref
          .read(chatSessionsProvider.notifier)
          .create(ephemeral: true);
      if (mounted) setState(() => _sessionId = session.id);
    } catch (error) {
      if (mounted) setState(() => _error = _messageFor(error));
    }
  }

  Future<String> _ensureSession() async {
    if (_sessionId != null) return _sessionId!;
    final session = await ref
        .read(chatSessionsProvider.notifier)
        .create(ephemeral: _ephemeral);
    if (mounted) setState(() => _sessionId = session.id);
    return session.id;
  }

  // ------------------------------------------------------------- attachments

  Future<void> _pickAttachments() async {
    try {
      final files = await FilePicker.pickFiles(
        type: FileType.custom,
        allowedExtensions: ChatAttachmentModel.allowedExtensions.toList(),
      );
      if (files.isEmpty || !mounted) return;
      await _addAttachments(files);
    } catch (error) {
      if (mounted) setState(() => _error = _messageFor(error));
    }
  }

  Future<void> _addAttachments(List<PlatformFile> files) async {
    final working = List<ChatAttachmentModel>.from(_attachments);
    final rejected = <String>[];
    for (final file in files) {
      final path = file.path;
      if (path == null) {
        rejected.add('Không đọc được "${file.name}".');
        continue;
      }
      // `lengthSync` là kích thước picker đã biết; chỉ đọc file khi cần.
      final size = file.lengthSync() ?? await file.length() ?? 0;
      final candidate = ChatAttachmentModel(
        path: path,
        name: file.name,
        size: size,
      );
      final problem = ChatAttachmentModel.validateAdd(working, candidate);
      if (problem != null) {
        rejected.add(problem);
        continue;
      }
      working.add(candidate);
    }
    if (!mounted) return;
    setState(() {
      _attachments
        ..clear()
        ..addAll(working);
      _error = rejected.isEmpty ? null : rejected.join('\n');
    });
  }

  void _removeAttachment(int index) =>
      setState(() => _attachments.removeAt(index));

  // ----------------------------------------------------------------- sending

  Future<void> _send() async {
    final text = _composer.text.trim();
    if ((text.isEmpty && _attachments.isEmpty) || _sending) return;

    final editingIndex = _editingIndex;
    final replaceLast = editingIndex != null;
    final attachments = List<ChatAttachmentModel>.from(_attachments);
    _composer.clear();

    setState(() {
      _error = null;
      _sending = true;
      _attachments.clear();
      if (replaceLast) {
        _messages.removeRange(editingIndex + 1, _messages.length);
        _messages[editingIndex] = ChatMessageModel(
          id: _messages[editingIndex].id,
          role: 'user',
          content: text,
          createdAt: _messages[editingIndex].createdAt,
        );
        _editingIndex = null;
      } else {
        _messages.add(ChatMessageModel(role: 'user', content: text));
      }
      _messages.add(
        ChatMessageModel(role: 'assistant', content: '', isTyping: true),
      );
    });
    _scrollToBottom();

    _cancelToken = CancelToken();
    try {
      final sessionId = await _ensureSession();
      final assistant = _messages.last;
      final repository = ref.read(chatRepositoryProvider);
      void onEvent(ChatStreamEvent event) {
        if (!mounted) return;
        _applyEvent(assistant, event);
        _scrollToBottom();
      }

      if (attachments.isNotEmpty && !replaceLast) {
        await repository.streamWithFiles(
          sessionId: sessionId,
          message: text,
          attachments: attachments,
          saveToKnowledge: _saveToKnowledge,
          enableWebSearch: _webSearch,
          cancelToken: _cancelToken,
          onEvent: onEvent,
        );
      } else {
        await repository.streamMessage(
          sessionId: sessionId,
          message: text,
          replaceLastExchange: replaceLast,
          enableWebSearch: _webSearch,
          cancelToken: _cancelToken,
          onEvent: onEvent,
        );
      }
      if (mounted) {
        setState(() => assistant.isTyping = false);
        ref.invalidate(chatSessionsProvider);
      }
    } catch (error) {
      if (mounted && !_isCancelled(error)) {
        setState(() {
          _messages.last.isTyping = false;
          _error = _messageFor(error);
        });
      }
    } finally {
      if (mounted) setState(() => _sending = false);
    }
  }

  /// Chạy lại câu trả lời cuối: server bỏ qua `message` khi `regenerate = true`.
  Future<void> _regenerate() async {
    final sessionId = _sessionId;
    if (_sending || sessionId == null || _messages.isEmpty) return;

    setState(() {
      _error = null;
      _sending = true;
      if (_messages.last.isAssistant) _messages.removeLast();
      _messages.add(
        ChatMessageModel(role: 'assistant', content: '', isTyping: true),
      );
    });
    _scrollToBottom();

    _cancelToken = CancelToken();
    try {
      final assistant = _messages.last;
      await ref
          .read(chatRepositoryProvider)
          .streamMessage(
            sessionId: sessionId,
            message: '',
            regenerate: true,
            enableWebSearch: _webSearch,
            cancelToken: _cancelToken,
            onEvent: (event) {
              if (!mounted) return;
              _applyEvent(assistant, event);
              _scrollToBottom();
            },
          );
      if (mounted) {
        setState(() => assistant.isTyping = false);
        ref.invalidate(chatSessionsProvider);
      }
    } catch (error) {
      if (mounted && !_isCancelled(error)) {
        setState(() {
          _messages.last.isTyping = false;
          _error = _messageFor(error);
        });
      }
    } finally {
      if (mounted) setState(() => _sending = false);
    }
  }

  void _startEditing(int index) {
    setState(() {
      _editingIndex = index;
      _composer.text = _messages[index].content;
      _attachments.clear();
    });
  }

  void _cancelEditing() {
    setState(() {
      _editingIndex = null;
      _composer.clear();
    });
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
        final file = _parseProducedFile(event.value);
        if (file != null) assistant.producedFiles.add(file);
      case 'error':
        assistant.content = 'Lỗi: ${event.value}';
        assistant.isTyping = false;
      case 'done':
        assistant.isTyping = false;
      case 'agent-log':
        break;
      case 'saved':
        // Backend báo đã lưu artifact/research vào Canvas cho phiên này.
        assistant.thinkingSteps.add('Đã lưu vào Canvas: ${event.value}');
    }
    if (mounted) setState(() {});
  }

  ProducedFileModel? _parseProducedFile(String raw) {
    try {
      final decoded = jsonDecode(raw);
      if (decoded is! Map) return null;
      final file = ProducedFileModel.fromJson(
        Map<String, dynamic>.from(decoded),
      );
      return file.id.isEmpty ? null : file;
    } catch (_) {
      return null;
    }
  }

  void _stop() {
    _cancelToken?.cancel('user stopped generation');
    if (mounted) setState(() => _sending = false);
  }

  // ------------------------------------------------------------------- voice

  Future<void> _startRecording() async {
    try {
      final recorder = ref.read(voiceRecorderProvider);
      if (!await recorder.hasPermission()) {
        if (mounted) {
          showAppSnack(context, 'Cần quyền micro để ghi âm tin nhắn thoại.');
        }
        return;
      }
      await recorder.start();
      if (mounted) setState(() => _recording = true);
    } catch (error) {
      if (mounted) setState(() => _error = _messageFor(error));
    }
  }

  Future<void> _stopRecording() async {
    setState(() => _transcribing = true);
    try {
      final text = await ref.read(voiceRecorderProvider).stopAndTranscribe();
      if (!mounted) return;
      if (text.isEmpty) {
        showAppSnack(context, 'Không nhận được nội dung từ bản ghi.');
      } else {
        setState(() {
          final current = _composer.text.trim();
          _composer.text = current.isEmpty ? text : '$current $text';
          _composer.selection = TextSelection.collapsed(
            offset: _composer.text.length,
          );
        });
      }
    } catch (error) {
      if (mounted) setState(() => _error = _messageFor(error));
    } finally {
      if (mounted) {
        setState(() {
          _recording = false;
          _transcribing = false;
        });
      }
    }
  }

  Future<void> _cancelRecording() async {
    try {
      await ref.read(voiceRecorderProvider).cancel();
    } catch (_) {
      // Huỷ ghi âm là best-effort.
    }
    if (mounted) setState(() => _recording = false);
  }

  // ------------------------------------------------------------------ canvas

  Future<void> _openCanvas() async {
    final sessionId = _sessionId;
    await Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => CanvasPanelScreen(
          sessionId: sessionId,
          draft: _composer.text,
          onInsert: (content) {
            if (!mounted) return;
            setState(() {
              _composer.text = content;
              _composer.selection = TextSelection.collapsed(
                offset: content.length,
              );
            });
          },
        ),
      ),
    );
  }

  // ------------------------------------------------------------------- share

  Future<void> _share() async {
    final sessionId = _sessionId;
    if (sessionId == null) {
      _snack('Hãy gửi tin nhắn đầu tiên trước khi chia sẻ đoạn chat.');
      return;
    }
    await showDialog<void>(
      context: context,
      builder: (context) => _ShareDialog(
        sessionId: sessionId,
        initialLink: _shareLinks[sessionId],
        onLinkChanged: (link) => link == null
            ? _shareLinks.remove(sessionId)
            : _shareLinks[sessionId] = link,
      ),
    );
  }

  void _snack(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(
      context,
    ).showSnackBar(SnackBar(content: Text(message)));
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

  bool _isCancelled(Object error) =>
      error is DioException && error.type == DioExceptionType.cancel;

  bool _isSafeUrl(String value) {
    final uri = Uri.tryParse(value);
    return uri != null && (uri.scheme == 'http' || uri.scheme == 'https');
  }

  String _messageFor(Object error) =>
      error is ApiException ? error.message : error.toString();

  @override
  Widget build(BuildContext context) {
    // Yêu cầu mở phiên đến sau khi màn đã dựng (người dùng đang ở tab khác).
    ref.listen(pendingChatSessionProvider, (previous, next) {
      if (next != null) _openSessionById(next);
    });

    final lastUserIndex = _messages.lastIndexWhere((m) => m.isUser);
    final lastAssistantIndex = _messages.lastIndexWhere((m) => m.isAssistant);

    return Scaffold(
      appBar: AppBar(
        title: const Text('AI Chat'),
        // Chỉ giữ hai hành động chính (Canvas, Thoại) trên AppBar: nhồi 5 icon
        // sẽ chật và lệch trên máy hẹp 320–360dp. Các hành động theo đoạn chat
        // nằm trong menu trội, còn bật/tắt tìm kiếm web đã có sẵn trong composer.
        actions: [
          IconButton(
            tooltip: 'Canvas artifact',
            onPressed: _openCanvas,
            icon: const Icon(Icons.dashboard_customize_outlined),
          ),
          IconButton(
            tooltip: 'Thoại thời gian thực',
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(builder: (_) => const VoiceRoomScreen()),
            ),
            icon: Icon(
              ref.watch(voiceRoomProvider).isConnected
                  ? Icons.settings_voice
                  : Icons.settings_voice_outlined,
            ),
          ),
          PopupMenuButton<String>(
            tooltip: 'Tuỳ chọn đoạn chat',
            onSelected: (value) => switch (value) {
              'share' => _share(),
              'ephemeral' => _newChat(ephemeral: true),
              _ => _newChat(),
            },
            itemBuilder: (context) => const [
              PopupMenuItem(value: 'new', child: Text('Đoạn chat mới')),
              PopupMenuItem(
                value: 'ephemeral',
                child: Text('Đoạn chat tạm thời (không lưu)'),
              ),
              PopupMenuDivider(),
              PopupMenuItem(value: 'share', child: Text('Chia sẻ đoạn chat')),
            ],
          ),
        ],
      ),
      drawer: Builder(
        builder: (context) => ChatSessionDrawer(
          currentSessionId: _sessionId,
          onSelect: (session) {
            Navigator.of(context).pop();
            _selectSession(session);
          },
          onNewChat: () {
            Navigator.of(context).pop();
            _newChat();
          },
        ),
      ),
      body: Column(
        children: [
          if (_error != null)
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 0),
              child: AppErrorBanner(
                message: _error!,
                onDismiss: () => setState(() => _error = null),
              ),
            ),
          if (_ephemeral)
            // Băng nhắc nhở: xanh thông tin của web (`bg-blue-50 border-blue-100`)
            // thay cho `tertiaryContainer` (tông tím của Material).
            Container(
              width: double.infinity,
              decoration: const BoxDecoration(
                color: AppTheme.infoSurface,
                border: Border(bottom: BorderSide(color: AppTheme.infoBorder)),
              ),
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
              child: Text(
                'Đoạn chat tạm thời: nội dung sẽ không được lưu vào lịch sử.',
                style: Theme.of(
                  context,
                ).textTheme.bodySmall?.copyWith(color: AppTheme.infoText),
              ),
            ),
          Expanded(
            child: _loadingMessages
                ? const Center(child: CircularProgressIndicator())
                : _messages.isEmpty
                ? const _ChatEmptyState()
                : ListView.builder(
                    controller: _scrollController,
                    padding: const EdgeInsets.fromLTRB(16, 20, 16, 24),
                    itemCount: _messages.length,
                    itemBuilder: (context, index) => ChatMessageView(
                      message: _messages[index],
                      onEdit: index == lastUserIndex && !_sending
                          ? () => _startEditing(index)
                          : null,
                      onRegenerate:
                          index == lastAssistantIndex &&
                              index == _messages.length - 1 &&
                              !_sending &&
                              _sessionId != null
                          ? _regenerate
                          : null,
                    ),
                  ),
          ),
          _Composer(
            controller: _composer,
            sending: _sending,
            webSearch: _webSearch,
            attachments: _attachments,
            saveToKnowledge: _saveToKnowledge,
            editing: _editingIndex != null,
            recording: _recording,
            transcribing: _transcribing,
            onWebSearchChanged: (value) => setState(() => _webSearch = value),
            onSaveToKnowledgeChanged: (value) =>
                setState(() => _saveToKnowledge = value),
            onAttach: _pickAttachments,
            onRemoveAttachment: _removeAttachment,
            onCancelEditing: _cancelEditing,
            onStartRecording: _startRecording,
            onStopRecording: _stopRecording,
            onCancelRecording: _cancelRecording,
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
  // Cuộn được: ở màn thấp (320×640, bàn phím mở) khối này từng tràn 6px.
  Widget build(BuildContext context) => Center(
    child: SingleChildScrollView(
      padding: const EdgeInsets.all(32),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Web: huy hiệu tròn 64px nền `gov-blue-dark` kèm đổ bóng, chữ trắng.
          Container(
            width: 64,
            height: 64,
            decoration: const BoxDecoration(
              color: AppTheme.govBlueDark,
              shape: BoxShape.circle,
              boxShadow: [
                BoxShadow(
                  color: Color(0x331E3A8A),
                  blurRadius: 12,
                  offset: Offset(0, 4),
                ),
              ],
            ),
            child: const Icon(
              Icons.auto_awesome,
              size: 28,
              color: Colors.white,
            ),
          ),
          const SizedBox(height: 20),
          Text(
            'Hôm nay tôi có thể giúp gì cho bạn?',
            // Web: `text-3xl font-bold`; trên 320dp lấy bước 22 để không xuống
            // quá nhiều dòng, vẫn trên tiêu đề trang (20).
            style: Theme.of(
              context,
            ).textTheme.headlineSmall?.copyWith(fontSize: 22),
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 8),
          Text(
            'Chat streaming, RAG, phân tích tài liệu và các công cụ AI an toàn.',
            textAlign: TextAlign.center,
            style: Theme.of(
              context,
            ).textTheme.bodyMedium?.copyWith(color: AppTheme.textMuted),
          ),
        ],
      ),
    ),
  );
}

class _Composer extends StatelessWidget {
  const _Composer({
    required this.controller,
    required this.sending,
    required this.webSearch,
    required this.attachments,
    required this.saveToKnowledge,
    required this.editing,
    required this.recording,
    required this.transcribing,
    required this.onWebSearchChanged,
    required this.onSaveToKnowledgeChanged,
    required this.onAttach,
    required this.onRemoveAttachment,
    required this.onCancelEditing,
    required this.onStartRecording,
    required this.onStopRecording,
    required this.onCancelRecording,
    required this.onSend,
    required this.onStop,
  });

  final TextEditingController controller;
  final bool sending;
  final bool webSearch;
  final List<ChatAttachmentModel> attachments;
  final bool saveToKnowledge;
  final bool editing;
  final bool recording;
  final bool transcribing;
  final ValueChanged<bool> onWebSearchChanged;
  final ValueChanged<bool> onSaveToKnowledgeChanged;
  final VoidCallback onAttach;
  final ValueChanged<int> onRemoveAttachment;
  final VoidCallback onCancelEditing;
  final VoidCallback onStartRecording;
  final VoidCallback onStopRecording;
  final VoidCallback onCancelRecording;
  final VoidCallback onSend;
  final VoidCallback onStop;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    // Web không dùng bóng đổ: composer chỉ ngăn cách bằng một đường viền trên,
    // nền trắng, phẳng — cùng ngôn ngữ với thẻ và header.
    decoration: const BoxDecoration(
      color: Colors.white,
      border: Border(top: BorderSide(color: AppTheme.border)),
    ),
    child: SafeArea(
      top: false,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (editing)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Row(
                  children: [
                    const Icon(Icons.edit_outlined, size: 16),
                    const SizedBox(width: 6),
                    Expanded(
                      child: Text(
                        'Đang sửa tin nhắn cuối — gửi để thay thế cặp hỏi đáp cuối.',
                        style: Theme.of(context).textTheme.bodySmall,
                      ),
                    ),
                    IconButton(
                      tooltip: 'Huỷ sửa',
                      iconSize: 18,
                      onPressed: onCancelEditing,
                      icon: const Icon(Icons.close),
                    ),
                  ],
                ),
              ),
            if (attachments.isNotEmpty)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Wrap(
                  spacing: 6,
                  runSpacing: 6,
                  children: [
                    for (final (index, attachment) in attachments.indexed)
                      InputChip(
                        label: Text(
                          '${attachment.name} · ${attachment.displaySize}',
                          style: Theme.of(context).textTheme.labelMedium,
                        ),
                        onDeleted: () => onRemoveAttachment(index),
                      ),
                  ],
                ),
              ),
            Row(
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                IconButton(
                  tooltip: 'Đính kèm tài liệu',
                  onPressed: sending || editing || recording ? null : onAttach,
                  icon: const Icon(Icons.attach_file),
                ),
                IconButton(
                  tooltip: recording ? 'Đang ghi âm' : 'Nhập bằng giọng nói',
                  onPressed: sending || editing || transcribing
                      ? null
                      : (recording ? onStopRecording : onStartRecording),
                  icon: Icon(
                    recording ? Icons.mic : Icons.mic_none,
                    color: recording
                        ? Theme.of(context).colorScheme.error
                        : null,
                  ),
                ),
                Expanded(
                  child: TextField(
                    controller: controller,
                    minLines: 1,
                    maxLines: 5,
                    textInputAction: TextInputAction.newline,
                    onSubmitted: (_) => onSend(),
                    // Không tự đặt border: dùng bán kính/viền chung của theme
                    // để ô nhập khớp với mọi ô nhập khác trong app.
                    decoration: const InputDecoration(
                      hintText: 'Nhắn tin cho CILA AI...',
                    ),
                  ),
                ),
                const SizedBox(width: 8),
                IconButton(
                  tooltip: sending ? 'Dừng tạo trả lời' : 'Gửi tin nhắn',
                  onPressed: sending ? onStop : onSend,
                  icon: Icon(
                    sending ? Icons.stop_circle_outlined : Icons.arrow_upward,
                  ),
                ),
              ],
            ),
            if (recording)
              Row(
                children: [
                  Icon(
                    Icons.fiber_manual_record,
                    size: 14,
                    color: Theme.of(context).colorScheme.error,
                  ),
                  const SizedBox(width: 6),
                  Expanded(
                    child: Text(
                      transcribing
                          ? 'Đang phiên âm...'
                          : 'Đang ghi âm — bấm Xong để phiên âm.',
                      style: Theme.of(context).textTheme.bodySmall,
                    ),
                  ),
                  TextButton(
                    onPressed: transcribing ? null : onCancelRecording,
                    child: const Text('Huỷ'),
                  ),
                  const SizedBox(width: 4),
                  AppPrimaryButton(
                    label: 'Xong',
                    icon: Icons.stop,
                    onPressed: transcribing ? null : onStopRecording,
                    expand: false,
                  ),
                ],
              )
            else
              // Wrap chứ không Row: hai nhãn dài cộng lại vượt 320dp và từng
              // tràn 67px trên máy nhỏ.
              Padding(
                padding: const EdgeInsets.only(left: 6),
                child: Wrap(
                  crossAxisAlignment: WrapCrossAlignment.center,
                  children: [
                    TextButton.icon(
                      onPressed: () => onWebSearchChanged(!webSearch),
                      icon: Icon(
                        webSearch ? Icons.public : Icons.public_off,
                        size: 16,
                      ),
                      label: Text(
                        webSearch ? 'Web: bật' : 'Web: tắt',
                        style: Theme.of(context).textTheme.labelMedium,
                      ),
                    ),
                    TextButton.icon(
                      onPressed: () =>
                          onSaveToKnowledgeChanged(!saveToKnowledge),
                      icon: Icon(
                        saveToKnowledge
                            ? Icons.library_add_check_outlined
                            : Icons.library_add_outlined,
                        size: 16,
                      ),
                      label: Text(
                        saveToKnowledge
                            ? 'Lưu vào kho tri thức'
                            : 'Không lưu file',
                        style: Theme.of(context).textTheme.labelMedium,
                      ),
                    ),
                  ],
                ),
              ),
          ],
        ),
      ),
    ),
  );
}

class _ShareDialog extends ConsumerStatefulWidget {
  const _ShareDialog({
    required this.sessionId,
    required this.initialLink,
    required this.onLinkChanged,
  });

  final String sessionId;
  final ShareLinkModel? initialLink;
  final ValueChanged<ShareLinkModel?> onLinkChanged;

  @override
  ConsumerState<_ShareDialog> createState() => _ShareDialogState();
}

class _ShareDialogState extends ConsumerState<_ShareDialog> {
  ShareLinkModel? _link;
  bool _busy = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _link = widget.initialLink;
  }

  Future<void> _create() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final link = await ref
          .read(chatRepositoryProvider)
          .createShareLink(widget.sessionId);
      widget.onLinkChanged(link);
      if (mounted) setState(() => _link = link);
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

  Future<void> _revoke() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await ref.read(chatRepositoryProvider).revokeShareLink(widget.sessionId);
      widget.onLinkChanged(null);
      if (mounted) setState(() => _link = null);
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
    final link = _link;
    final url = link == null
        ? null
        : ref.watch(appConfigProvider).shareUrlFor(link.token);

    return AlertDialog(
      title: const Text('Chia sẻ đoạn chat'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (url == null)
            const Text(
              'Tạo link công khai (chỉ đọc) cho đoạn chat này. Tạo link mới sẽ '
              'thu hồi mọi link cũ của đoạn chat.',
            )
          else ...[
            SelectableText(url),
            const SizedBox(height: 8),
            Text(
              link!.expiresAtUtc == null
                  ? 'Link không đặt hạn.'
                  : 'Hết hạn: ${link.expiresAtUtc!.toLocal()}',
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ],
          if (_error != null) ...[
            const SizedBox(height: 12),
            Text(
              _error!,
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          ],
        ],
      ),
      actions: [
        TextButton(
          onPressed: _busy ? null : () => Navigator.pop(context),
          child: const Text('Đóng'),
        ),
        if (url != null) ...[
          TextButton.icon(
            onPressed: _busy
                ? null
                : () async {
                    await Clipboard.setData(ClipboardData(text: url));
                    if (context.mounted) {
                      ScaffoldMessenger.of(context).showSnackBar(
                        const SnackBar(content: Text('Đã sao chép link.')),
                      );
                    }
                  },
            icon: const Icon(Icons.copy, size: 18),
            label: const Text('Sao chép'),
          ),
          TextButton.icon(
            onPressed: _busy
                ? null
                : () => launchUrl(
                    Uri.parse(url),
                    mode: LaunchMode.externalApplication,
                  ),
            icon: const Icon(Icons.open_in_new, size: 18),
            label: const Text('Mở'),
          ),
          TextButton.icon(
            onPressed: _busy ? null : _revoke,
            // Thu hồi link là hành động không hoàn tác: tô đỏ để không bị bấm nhầm.
            style: TextButton.styleFrom(foregroundColor: AppTheme.govRed),
            icon: const Icon(Icons.link_off, size: 18),
            label: const Text('Thu hồi'),
          ),
        ] else
          AppPrimaryButton(
            label: 'Tạo link chia sẻ',
            onPressed: _busy ? null : _create,
            expand: false,
          ),
      ],
    );
  }
}
