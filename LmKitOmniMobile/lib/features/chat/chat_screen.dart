import 'dart:async';
import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../app/theme.dart';
import '../../app/ui/app_controls.dart';
import '../../app/ui/tenant_logo.dart';
import '../../core/auth/auth_provider.dart';
import '../../core/config/app_config_provider.dart';
import '../../core/network/api_exception.dart';
import '../canvas/canvas_panel_screen.dart';
import '../more/function_menu.dart';
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

  /// Bộ ghi âm đang mở, giữ lại để [dispose] không phải đọc provider.
  ///
  /// `ref` chỉ dùng được khi widget còn sống: gọi `ref.read` trong `dispose`
  /// ném "Using ref when a widget is about to or has been unmounted is unsafe"
  /// (lỗi này lộ ra ngay khi có test đi qua nhánh rời màn lúc đang ghi âm).
  VoiceTranscriber? _activeRecorder;

  @override
  void dispose() {
    _composer.dispose();
    _scrollController.dispose();
    _cancelToken?.cancel('chat screen disposed');
    // Rời màn khi đang ghi âm: huỷ bản ghi và xoá file tạm.
    _activeRecorder?.cancel();
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

  Future<void> _newChat() async {
    setState(() {
      _sessionId = null;
      _messages.clear();
      _attachments.clear();
      _editingIndex = null;
      _error = null;
      // Web cũng trả chế độ tạm thời về tắt khi mở đoạn chat mới.
      _ephemeral = false;
    });
  }

  /// Bật/tắt chế độ chat tạm thời — cùng hành vi với `toggleEphemeral` của web:
  /// đổi chế độ thì mở luôn một cuộc trò chuyện mới, để chế độ mới áp cho phiên
  /// sạch chứ không thay đổi ngược phiên đang mở.
  void _toggleEphemeral() {
    setState(() {
      _ephemeral = !_ephemeral;
      _sessionId = null;
      _messages.clear();
      _attachments.clear();
      _editingIndex = null;
      _error = null;
      _composer.clear();
    });
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
      _activeRecorder = recorder;
      if (!await recorder.hasPermission()) {
        if (mounted) {
          showAppSnack(context, 'Cần quyền micro để ghi âm tin nhắn thoại.');
        }
        _activeRecorder = null;
        return;
      }
      await recorder.start();
      if (mounted) setState(() => _recording = true);
    } catch (error) {
      _activeRecorder = null;
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
      _activeRecorder = null;
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
      final recorder = _activeRecorder;
      if (recorder != null) {
        await recorder.cancel();
      }
    } catch (_) {
      // Huỷ ghi âm là best-effort.
    }
    // Bộ ghi âm đang mở đã được huỷ; không giữ tham chiếu sau khi rời trạng thái.
    _activeRecorder = null;
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

  /// Cuộn xuống cuối cuộc trò chuyện.
  ///
  /// Chiều cao nội dung còn đổi **sau** frame đang chạy (biểu đồ, thẻ tệp,
  /// khối suy luận mở/thu), nên không thể tin một con số `maxScrollExtent` đo
  /// tại một thời điểm: `animateTo` không kẹp theo biên, nội dung ngắn lại là
  /// vị trí cuộn dừng ở ngoài đáy và người dùng nhìn thấy khung chat trống.
  /// Vì vậy sau khi cuộn (và cả ngay sau frame kế tiếp) luôn kẹp về đáy thật.
  void _scrollToBottom() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || !_scrollController.hasClients) return;
      final target = _scrollController.position.maxScrollExtent;
      if ((_scrollController.position.pixels - target).abs() < 1) return;
      _scrollController
          .animateTo(
            target,
            duration: const Duration(milliseconds: 180),
            curve: Curves.easeOut,
          )
          .whenComplete(_clampToBottom);
      WidgetsBinding.instance.addPostFrameCallback((_) => _clampToBottom());
    });
  }

  /// Kẹp vị trí cuộn về đáy hiện tại nếu nội dung đã ngắn lại.
  void _clampToBottom() {
    if (!mounted || !_scrollController.hasClients) return;
    final position = _scrollController.position;
    if (position.pixels > position.maxScrollExtent) {
      _scrollController.jumpTo(position.maxScrollExtent);
    }
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
    final user = ref.watch(authControllerProvider).asData?.value?.user;
    final agentName = user?.agentName ?? 'CILA - AI Agent';

    return Scaffold(
      // **Một header duy nhất.** Trước đây shell có một AppBar xanh (tên đơn
      // vị + chuông) rồi màn chat lồng thêm một AppBar trắng ("AI Chat") — hai
      // thanh xếp chồng, và thanh dưới lại trùng thông tin với tên đơn vị.
      // Giờ màn chat là màn gốc nên nó sở hữu luôn header: nút mở lịch sử
      // phiên (ngăn kéo), tên đơn vị, và ba hành động của cuộc trò chuyện.
      appBar: AppTopBar(
        // **Tiêu đề = tên trang đang mở.** Mọi màn đẩy sang đều có AppBar riêng
        // đọc đúng tên trang (Projects, RAG Documents, Deep Research…), nên màn
        // gốc cũng phải đọc tên trang của nó thay vì ghép thêm một dòng tên đơn
        // vị: trước đây đơn vị nằm ở thanh trên rồi thanh dưới mới ghi tên trang
        // — hai tiêu đề xếp chồng và tên đơn vị dài bị cắt giữa từ.
        // Tên đơn vị + tên trợ lý chuyển xuống đầu ngăn kéo (như sidebar web).
        title: const Text('AI Chat'),
        // Không khai báo kiểu cho các nút trên header: `AppBar` tự bọc
        // `leading`/`actions` trong `IconButtonTheme` sinh từ `appBarTheme.iconTheme`
        // (trắng), nên mọi nút trên chrome navy — kể cả ở các màn khác — dùng
        // chung một kiểu nước chạm.
        leading: Builder(
          builder: (context) => IconButton(
            tooltip: 'Lịch sử chat',
            onPressed: Scaffold.of(context).openDrawer,
            icon: const Icon(Icons.menu),
          ),
        ),
        // Ba hành động: Canvas, Thoại, và nút ba chấm mở **toàn bộ** danh sách
        // chức năng.
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
          IconButton(
            tooltip: 'Danh sách chức năng',
            onPressed: () => showFunctionMenu(
              context,
              onNewChat: _newChat,
              onShare: _share,
            ),
            icon: const Icon(Icons.more_vert),
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
            ephemeral: _ephemeral,
            editing: _editingIndex != null,
            recording: _recording,
            transcribing: _transcribing,
            agentName: agentName,
            onWebSearchChanged: (value) => setState(() => _webSearch = value),
            onSaveToKnowledgeChanged: (value) =>
                setState(() => _saveToKnowledge = value),
            onToggleEphemeral: _toggleEphemeral,
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

/// Khối chào khi cuộc trò chuyện chưa có tin nhắn nào.
///
/// Dấu nhận diện ở đây là **logo của đơn vị** (mặc định Quốc huy) chứ không phải
/// một biểu tượng AI chung chung: người dùng đang làm việc trong hệ thống của cơ
/// quan nào thì màn hình đầu tiên cũng nên nói đúng điều đó, và đơn vị vẫn đổi
/// được logo theo tenant ở màn quản trị.
class _ChatEmptyState extends ConsumerWidget {
  const _ChatEmptyState();

  @override
  // Cuộn được: ở màn thấp (320×640, bàn phím mở) khối này từng tràn 6px.
  Widget build(BuildContext context, WidgetRef ref) => Center(
    child: SingleChildScrollView(
      padding: const EdgeInsets.all(32),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          TenantLogo(
            path: ref
                .watch(authControllerProvider)
                .asData
                ?.value
                ?.user
                .tenant
                ?.logoUrl,
            size: 64,
            // Quốc huy tự có đĩa xanh của nó; thêm viền xám bọc ngoài thành hai
            // vòng tròn lồng nhau.
            border: false,
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

/// Chiều cao/cạnh vùng chạm của nút công cụ trong khối soạn tin.
///
/// Web dùng `min-w-10 min-h-10` (40px) cho các nút tròn này và `w-11 h-11`
/// (44px) cho nút gửi — giữ đúng hai con số đó để thanh soạn tin có cùng nhịp
/// với bản web.
const _toolSize = 40.0;
const _sendSize = 44.0;

/// Ba sắc thái "đang bật" của nút công cụ: tìm kiếm web (xanh thông tin),
/// chat tạm thời (vàng cảnh báo), ghi âm (đỏ nguy hiểm).
enum _ToggleTone { info, warning, danger }

class _Composer extends StatelessWidget {
  const _Composer({
    required this.controller,
    required this.sending,
    required this.webSearch,
    required this.attachments,
    required this.saveToKnowledge,
    required this.ephemeral,
    required this.editing,
    required this.recording,
    required this.transcribing,
    required this.agentName,
    required this.onWebSearchChanged,
    required this.onSaveToKnowledgeChanged,
    required this.onToggleEphemeral,
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
  final bool ephemeral;
  final bool editing;
  final bool recording;
  final bool transcribing;

  /// Tên trợ lý của đơn vị — web đưa vào gợi ý ô nhập và dòng nhắc trách nhiệm.
  final String agentName;
  final ValueChanged<bool> onWebSearchChanged;
  final ValueChanged<bool> onSaveToKnowledgeChanged;
  final VoidCallback onToggleEphemeral;
  final VoidCallback onAttach;
  final ValueChanged<int> onRemoveAttachment;
  final VoidCallback onCancelEditing;
  final VoidCallback onStartRecording;
  final VoidCallback onStopRecording;
  final VoidCallback onCancelRecording;
  final VoidCallback onSend;
  final VoidCallback onStop;

  @override
  Widget build(BuildContext context) {
    final texts = Theme.of(context).textTheme;

    return DecoratedBox(
      // Web không dùng bóng đổ cho cả thanh: composer ngăn cách bằng một đường
      // viền trên, nền trắng, phẳng — cùng ngôn ngữ với thẻ và header.
      decoration: const BoxDecoration(
        color: Colors.white,
        border: Border(top: BorderSide(color: AppTheme.border)),
      ),
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(12, 8, 12, 8),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // Cảnh báo chế độ tạm thời nằm ngay trên khối soạn tin, đúng chỗ
              // web đặt nó (`isEphemeral` banner trong composer overlay).
              if (ephemeral)
                const _ComposerNotice(
                  icon: Icons.visibility_off_outlined,
                  message:
                      'Chat tạm thời — đoạn chat này sẽ không được lưu vào '
                      'lịch sử.',
                  // Web vẽ băng này bằng tông hổ phách (`amber-200/50/800`);
                  // app đã có sẵn bộ token "cảnh báo" dùng cho thẻ HITL nên
                  // lấy luôn bộ đó thay vì nở thêm một sắc vàng thứ hai gần
                  // giống hệt.
                  surface: AppTheme.warningSurface,
                  border: AppTheme.warningBorder,
                  foreground: AppTheme.warningText,
                ),
              if (editing)
                _ComposerNotice(
                  icon: Icons.edit_outlined,
                  message:
                      'Đang sửa tin nhắn cuối — gửi để thay thế cặp hỏi đáp '
                      'trước.',
                  surface: AppTheme.infoSurface,
                  border: AppTheme.infoBorder,
                  foreground: AppTheme.infoText,
                  action: TextButton(
                    onPressed: onCancelEditing,
                    style: TextButton.styleFrom(
                      foregroundColor: AppTheme.infoText,
                      padding: const EdgeInsets.symmetric(horizontal: 12),
                    ),
                    child: const Text('Huỷ'),
                  ),
                ),
              // Một khối bo tròn chứa cả ô nhập lẫn thanh công cụ — đúng
              // `rounded-[28px] p-2` của web. Trước đây ô nhập và hàng nút là
              // hai khối rời nên thanh soạn tin trông như hai dải xếp chồng.
              Container(
                decoration: BoxDecoration(
                  color: Colors.white,
                  border: Border.all(color: AppTheme.border),
                  borderRadius: BorderRadius.circular(28),
                  boxShadow: const [
                    BoxShadow(
                      color: Color(0x140F172A),
                      blurRadius: 3,
                      offset: Offset(0, 1),
                    ),
                  ],
                ),
                padding: const EdgeInsets.all(8),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    if (attachments.isNotEmpty) _attachments(context),
                    if (!recording)
                      Padding(
                        // Web: ô nhập nằm trong khối với `px-3 pt-2`.
                        //
                        // Nút micro nằm **trong chính ô nhập** (bên phải, sát đáy)
                        // chứ không ở hàng công cụ bên dưới: micro là một cách nhập
                        // liệu, cùng nhóm với ô nhập; hàng dưới chỉ còn các chế độ
                        // (web, tạm thời, đính kèm) và nút gửi.
                        //
                        // Có micro: lề phải là 2 (chứ không phải 4 như lề trái) để
                        // **tâm nút micro trùng cột với tâm nút gửi ở hàng dưới**
                        // (2 + 40/2 = 8 + 44/2 — cả hai cách mép phải màn hình 20),
                        // vì hai nút tròn xếp dọc mà lệch tâm thì nhìn ra ngay.
                        // Không có micro (web — không ghi âm được) thì trả lại lề
                        // 12 cho chữ khỏi dính mép khối.
                        padding: EdgeInsets.fromLTRB(
                          12,
                          4,
                          VoiceRecorder.isSupported ? 2 : 12,
                          0,
                        ),
                        child: Row(
                          crossAxisAlignment: CrossAxisAlignment.end,
                          children: [
                            Expanded(
                              child: Padding(
                                // Hàng cao đúng bằng nút micro (40). Đẩy chữ lên 8px
                                // thì tâm dòng chữ trùng tâm nút; khi ô nhập cao lên
                                // (tối đa 6 dòng) nút vẫn ở đáy và thẳng hàng với
                                // dòng cuối.
                                padding: const EdgeInsets.only(bottom: 8),
                                child: TextField(
                                  controller: controller,
                                  minLines: 1,
                                  // Web: `max-h-48` (192px) ≈ 6 dòng ở cỡ chữ 16.
                                  maxLines: 6,
                                  textInputAction: TextInputAction.newline,
                                  onSubmitted: (_) => onSend(),
                                  decoration: InputDecoration(
                                    hintText: 'Nhắn tin cho $agentName...',
                                    // Khối ngoài đã có viền và nền: giữ thêm
                                    // viền/nền mặc định của theme là hai khung lồng
                                    // vào nhau.
                                    filled: false,
                                    isDense: true,
                                    contentPadding: EdgeInsets.zero,
                                    border: InputBorder.none,
                                    enabledBorder: InputBorder.none,
                                    focusedBorder: InputBorder.none,
                                  ),
                                ),
                              ),
                            ),
                            // Web không ghi được tệp tạm để gửi lên server nên nút
                            // micro chỉ hiện ở nền tảng ghi âm được.
                            if (VoiceRecorder.isSupported) ...[
                              const SizedBox(width: 4),
                              _ComposerToggle(
                                icon: Icons.mic_none,
                                tooltip: 'Nhập bằng giọng nói',
                                onPressed: sending || editing || transcribing
                                    ? null
                                    : onStartRecording,
                              ),
                            ],
                          ],
                        ),
                      ),
                    // Đang ghi âm: **khối soạn tin thu lại còn một hàng** — đồng
                    // hồ sóng + thời lượng + hai nút, thay cho cả ô nhập lẫn hàng
                    // công cụ. Giữ ô nhập bên trên thì vừa thừa chỗ vừa mời người
                    // dùng gõ tay trong lúc micro đang mở.
                    if (recording)
                      _RecordingStrip(
                        transcribing: transcribing,
                        onCancel: onCancelRecording,
                        onStop: onStopRecording,
                      )
                    else ...[
                      const SizedBox(height: 4),
                      _tools(context),
                    ],
                  ],
                ),
              ),
              const SizedBox(height: 10),
              // Web: dòng nhắc trách nhiệm nằm ngay dưới khối soạn tin
              // (`text-center text-xs text-gray-500`).
              Text(
                '$agentName có thể mắc sai lầm. Vui lòng kiểm tra lại các '
                'thông tin quan trọng.',
                textAlign: TextAlign.center,
                style: texts.bodySmall,
              ),
            ],
          ),
        ),
      ),
    );
  }

  /// File đính kèm (web: chip `bg-blue-50 border-blue-100 text-blue-700`) kèm
  /// lựa chọn lưu nội dung vào kho tri thức.
  ///
  /// Ô chọn chỉ hiện khi đã có file — đúng như web, và cũng đúng ngữ nghĩa: cờ
  /// này chỉ có tác dụng cho lần gửi kèm file.
  Widget _attachments(BuildContext context) => Padding(
    padding: const EdgeInsets.fromLTRB(4, 4, 4, 4),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            for (final (index, attachment) in attachments.indexed)
              _AttachmentChip(
                attachment: attachment,
                onRemove: () => onRemoveAttachment(index),
              ),
          ],
        ),
        Row(
          children: [
            Checkbox(
              value: saveToKnowledge,
              visualDensity: VisualDensity.compact,
              materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
              onChanged: (value) => onSaveToKnowledgeChanged(value ?? false),
            ),
            const SizedBox(width: 4),
            Flexible(
              child: Text(
                'Lưu nội dung file vào kho tri thức',
                style: Theme.of(context).textTheme.bodySmall,
              ),
            ),
          ],
        ),
      ],
    ),
  );

  /// Hàng công cụ dưới ô nhập: bên trái là các nút bật/tắt và đính kèm, bên
  /// phải là nút gửi — đúng `justify-between` của web. Nút micro **không** nằm
  /// ở đây mà ở trong ô nhập (xem `_composer`), vì nó là cách nhập liệu chứ
  /// không phải một chế độ của cuộc trò chuyện.
  Widget _tools(BuildContext context) => Row(
    children: [
      _ComposerToggle(
        icon: webSearch ? Icons.public : Icons.public_off,
        tooltip: webSearch
            ? 'Tìm kiếm web: đang bật'
            : 'Tìm kiếm web: đang tắt',
        tone: webSearch ? _ToggleTone.info : null,
        onPressed: () => onWebSearchChanged(!webSearch),
      ),
      _ComposerToggle(
        icon: Icons.visibility_off_outlined,
        tooltip: ephemeral
            ? 'Chat tạm thời: đang bật'
            : 'Chat tạm thời — không lưu vào lịch sử',
        tone: ephemeral ? _ToggleTone.warning : null,
        onPressed: onToggleEphemeral,
      ),
      _ComposerToggle(
        icon: Icons.attach_file,
        tooltip: 'Đính kèm tài liệu',
        onPressed: sending || editing || recording ? null : onAttach,
      ),
      const Spacer(),
      _SendButton(
        controller: controller,
        hasAttachments: attachments.isNotEmpty,
        sending: sending,
        onSend: onSend,
        onStop: onStop,
      ),
    ],
  );
}

/// Hàng ghi âm: thay chỗ ô nhập khi micro đang mở.
///
/// Gọn trong **một hàng** (chấm đỏ nhấp nháy · thời lượng · đồng hồ sóng · Huỷ ·
/// Xong) để khối soạn tin không cao thêm khi bắt đầu nói, và để trạng thái ghi
/// âm nhìn ra ngay là của **app** — trước đây chỉ là một dòng chữ nhỏ nằm dưới
/// ô nhập, nên nút micro của bàn phím hệ thống cạnh đó trông chẳng khác gì và
/// người dùng bấm nhầm sang bản ghi âm của Google.
///
/// Đồng hồ sóng lấy mức âm lượng thật từ micro (xem `VoiceRecorder.levels`): có
/// phản hồi theo giọng nói là cách rõ nhất để biết app đang nghe.
class _RecordingStrip extends ConsumerStatefulWidget {
  const _RecordingStrip({
    required this.transcribing,
    required this.onCancel,
    required this.onStop,
  });

  final bool transcribing;
  final VoidCallback onCancel;
  final VoidCallback onStop;

  @override
  ConsumerState<_RecordingStrip> createState() => _RecordingStripState();
}

class _RecordingStripState extends ConsumerState<_RecordingStrip>
    with SingleTickerProviderStateMixin {
  /// Số cột sóng. Đủ để nhìn thành "dải" mà vẫn nhẹ khi vẽ lại 8 lần/giây.
  static const _barCount = 22;
  static const _barWidth = 3.0;
  static const _barGap = 3.0;
  static const _minBarHeight = 5.0;
  static const _maxBarHeight = 26.0;

  /// Mức mới nhất nằm cuối; cột cũ trôi dần sang trái như sóng chạy sang phải.
  ///
  /// `growable: true` là bắt buộc: danh sách cố định độ dài thì `removeAt`
  /// ném `Unsupported operation` ngay khi có mức âm lượng đầu tiên.
  final _levels = List<double>.filled(_barCount, 0, growable: true);

  late final AnimationController _pulse;
  Timer? _ticker;
  StreamSubscription<double>? _levelsSubscription;
  int _seconds = 0;

  @override
  void initState() {
    super.initState();
    _pulse = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 900),
    )..repeat(reverse: true);
    _ticker = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted) setState(() => _seconds++);
    });
    _listenToLevels();
  }

  @override
  void didUpdateWidget(_RecordingStrip oldWidget) {
    super.didUpdateWidget(oldWidget);
    // Phiên âm xong thì không còn mức âm lượng để vẽ nữa.
    if (widget.transcribing && !oldWidget.transcribing) {
      _levelsSubscription?.cancel();
      _levelsSubscription = null;
      _ticker?.cancel();
      _ticker = null;
    }
  }

  void _listenToLevels() {
    if (widget.transcribing) return;
    _levelsSubscription = ref
        .read(voiceRecorderProvider)
        .levels()
        .listen((level) {
          if (!mounted) return;
          setState(() {
            _levels.removeAt(0);
            _levels.add(level);
          });
        });
  }

  @override
  void dispose() {
    _levelsSubscription?.cancel();
    _ticker?.cancel();
    _pulse.dispose();
    super.dispose();
  }

  /// `m:ss` — đủ để biết đã nói bao lâu, không cần giờ.
  String get _elapsed {
    final minutes = _seconds ~/ 60;
    final seconds = (_seconds % 60).toString().padLeft(2, '0');
    return '$minutes:$seconds';
  }

  @override
  Widget build(BuildContext context) {
    final texts = Theme.of(context).textTheme;
    return Padding(
      // Lề khớp hàng ô nhập khi không ghi âm, để khối không nhảy khi đổi trạng
      // thái: trái 12 như chỗ chữ, phải 2 như lề nút micro.
      padding: const EdgeInsets.fromLTRB(12, 4, 2, 4),
      child: Row(
        children: [
          FadeTransition(
            opacity: Tween<double>(
              begin: 0.35,
              end: 1,
            ).animate(_pulse),
            child: Container(
              width: 10,
              height: 10,
              decoration: const BoxDecoration(
                color: AppTheme.govRed,
                shape: BoxShape.circle,
              ),
            ),
          ),
          const SizedBox(width: 10),
          Text(
            _elapsed,
            style: texts.titleSmall?.copyWith(
              // Số phải đứng yên khi đồng hồ chạy: chữ số cùng bề ngang.
              fontFeatures: const [FontFeature.tabularFigures()],
              color: AppTheme.textPrimary,
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: widget.transcribing
                ? Row(
                    children: [
                      const SizedBox(
                        width: 14,
                        height: 14,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      ),
                      const SizedBox(width: 8),
                      // Nhãn co theo chỗ còn lại: ở khổ hẹp hoặc cỡ chữ lớn, nhãn
                      // dài hơn ô là chuyện bình thường, không được để tràn.
                      Flexible(
                        child: Text(
                          'Đang phiên âm...',
                          overflow: TextOverflow.ellipsis,
                          style: texts.bodySmall,
                        ),
                      ),
                    ],
                  )
                : _meter(),
          ),
          const SizedBox(width: 8),
          _ComposerToggle(
            icon: Icons.close,
            tooltip: 'Huỷ bản ghi',
            onPressed: widget.transcribing ? null : widget.onCancel,
          ),
          const SizedBox(width: 4),
          _RoundAction(
            icon: Icons.stop,
            tooltip: 'Phiên âm bản ghi',
            background: AppTheme.govBlueDark,
            onPressed: widget.transcribing ? null : widget.onStop,
          ),
        ],
      ),
    );
  }

  /// Đồng hồ sóng: cột cao theo mức âm lượng, gần đây nhất ở bên phải.
  Widget _meter() => SizedBox(
    height: _maxBarHeight,
    child: Row(
      mainAxisAlignment: MainAxisAlignment.end,
      crossAxisAlignment: CrossAxisAlignment.center,
      children: [
        for (final level in _levels)
          Padding(
            padding: const EdgeInsets.only(left: _barGap),
            child: Container(
              width: _barWidth,
              height:
                  _minBarHeight + level * (_maxBarHeight - _minBarHeight),
              decoration: BoxDecoration(
                color: AppTheme.govRed.withValues(alpha: 0.30 + level * 0.70),
                borderRadius: BorderRadius.circular(_barWidth),
              ),
            ),
          ),
      ],
    ),
  );
}

/// Băng nhắc nằm ngay trên khối soạn tin, đúng kiểu web (`rounded-lg border
/// px-4 py-2 text-sm` của cả băng chế độ tạm thời lẫn băng đang sửa tin).
class _ComposerNotice extends StatelessWidget {
  const _ComposerNotice({
    required this.icon,
    required this.message,
    required this.surface,
    required this.border,
    required this.foreground,
    this.action,
  });

  final IconData icon;
  final String message;
  final Color surface;
  final Color border;
  final Color foreground;
  final Widget? action;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 8),
    child: Container(
      padding: EdgeInsets.fromLTRB(14, 6, action == null ? 14 : 4, 6),
      decoration: BoxDecoration(
        color: surface,
        border: Border.all(color: border),
        borderRadius: BorderRadius.circular(AppTheme.radiusSmall),
      ),
      child: Row(
        children: [
          Icon(icon, size: 16, color: foreground),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              message,
              style: Theme.of(
                context,
              ).textTheme.bodyMedium?.copyWith(color: foreground),
            ),
          ),
          ?action,
        ],
      ),
    ),
  );
}

/// Nút công cụ tròn trong khối soạn tin.
///
/// Trạng thái nghỉ là viền trong suốt + icon xám; khi bật thì đổi sang một bộ
/// ba màu (nền/viền/chữ) theo [tone] — đúng cách web tô nút đang bật.
class _ComposerToggle extends StatelessWidget {
  const _ComposerToggle({
    required this.icon,
    required this.tooltip,
    required this.onPressed,
    this.tone,
  });

  final IconData icon;
  final String tooltip;
  final VoidCallback? onPressed;
  final _ToggleTone? tone;

  @override
  Widget build(BuildContext context) {
    final (surface, border, foreground) = switch (tone) {
      _ToggleTone.info => (
        AppTheme.infoSurface,
        AppTheme.infoBorder,
        AppTheme.infoText,
      ),
      _ToggleTone.warning => (
        AppTheme.warningSurface,
        AppTheme.warningBorder,
        AppTheme.warningText,
      ),
      _ToggleTone.danger => (
        AppTheme.dangerSurface,
        AppTheme.dangerBorder,
        AppTheme.dangerText,
      ),
      null => (Colors.transparent, Colors.transparent, AppTheme.textMuted),
    };
    final enabled = onPressed != null;

    return Tooltip(
      message: tooltip,
      child: Material(
        color: surface,
        shape: StadiumBorder(side: BorderSide(color: border)),
        child: InkWell(
          customBorder: const StadiumBorder(),
          onTap: onPressed,
          child: SizedBox(
            width: _toolSize,
            height: _toolSize,
            child: Icon(
              icon,
              size: 18,
              // Nút bị khoá (đang tạo trả lời, đang sửa tin) phải nhạt đi, nếu
              // không người dùng bấm mà không hiểu vì sao không có gì xảy ra.
              color: enabled ? foreground : foreground.withValues(alpha: 0.4),
            ),
          ),
        ),
      ),
    );
  }
}

/// Nút gửi/dừng ở góc phải khối soạn tin.
///
/// Web khoá nút gửi khi ô nhập trống và không có file (`:disabled` của
/// PrimeVue): nút xám nói rõ "chưa có gì để gửi" tốt hơn một nút bấm vào không
/// có tác dụng. Vì vậy nút phải nghe chính controller chứ không chỉ state của
/// màn — gõ chữ không dựng lại cả màn chat.
class _SendButton extends StatelessWidget {
  const _SendButton({
    required this.controller,
    required this.hasAttachments,
    required this.sending,
    required this.onSend,
    required this.onStop,
  });

  final TextEditingController controller;
  final bool hasAttachments;
  final bool sending;
  final VoidCallback onSend;
  final VoidCallback onStop;

  @override
  Widget build(BuildContext context) =>
      ValueListenableBuilder<TextEditingValue>(
        valueListenable: controller,
        builder: (context, value, _) {
          if (sending) {
            return _RoundAction(
              icon: Icons.stop,
              tooltip: 'Dừng tạo trả lời',
              background: AppTheme.govRed,
              onPressed: onStop,
            );
          }
          final canSend = value.text.trim().isNotEmpty || hasAttachments;
          return _RoundAction(
            icon: Icons.arrow_upward,
            tooltip: 'Gửi tin nhắn',
            background: AppTheme.govBlueDark,
            onPressed: canSend ? onSend : null,
          );
        },
      );
}

/// Nút tròn 44px (web: `!w-11 !h-11 rounded`).
class _RoundAction extends StatelessWidget {
  const _RoundAction({
    required this.icon,
    required this.tooltip,
    required this.background,
    required this.onPressed,
  });

  final IconData icon;
  final String tooltip;
  final Color background;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    final enabled = onPressed != null;
    return Tooltip(
      message: tooltip,
      child: Material(
        color: enabled ? background : AppTheme.surfaceMuted,
        shape: const CircleBorder(),
        child: InkWell(
          customBorder: const CircleBorder(),
          onTap: onPressed,
          child: SizedBox(
            width: _sendSize,
            height: _sendSize,
            child: Icon(
              icon,
              size: 20,
              color: enabled ? Colors.white : AppTheme.textMuted,
            ),
          ),
        ),
      ),
    );
  }
}

/// Chip tệp đính kèm trong khối soạn tin (web: `bg-blue-50 border-blue-100
/// text-blue-700`, tên file cắt ở 120px, kèm cỡ file).
class _AttachmentChip extends StatelessWidget {
  const _AttachmentChip({required this.attachment, required this.onRemove});

  final ChatAttachmentModel attachment;
  final VoidCallback onRemove;

  @override
  Widget build(BuildContext context) {
    final texts = Theme.of(context).textTheme;
    return Container(
      padding: const EdgeInsets.fromLTRB(10, 2, 2, 2),
      decoration: BoxDecoration(
        color: AppTheme.infoSurface,
        border: Border.all(color: AppTheme.infoBorder),
        borderRadius: BorderRadius.circular(AppTheme.radius),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Icon(Icons.attach_file, size: 14, color: AppTheme.infoText),
          const SizedBox(width: 6),
          ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 140),
            child: Text(
              attachment.name,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: texts.labelMedium?.copyWith(color: AppTheme.infoText),
            ),
          ),
          const SizedBox(width: 4),
          Text(
            '(${attachment.displaySize})',
            style: texts.labelSmall?.copyWith(color: AppTheme.infoText),
          ),
          IconButton(
            tooltip: 'Bỏ file ${attachment.name}',
            onPressed: onRemove,
            iconSize: 16,
            visualDensity: VisualDensity.compact,
            icon: const Icon(Icons.close, color: AppTheme.infoText),
          ),
        ],
      ),
    );
  }
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
