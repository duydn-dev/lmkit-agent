import 'dart:io';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:video_player/video_player.dart';

import '../../app/theme.dart';
import './video_languages.dart';

/// Caption demo cố định — không giả vờ AI đã dịch.
const String videoDemoCaption = 'Xin chào — đây là phụ đề minh họa.';

/// Seam chọn file video để widget test không cần plugin thật.
abstract class VideoPickerService {
  Future<String?> pickVideoPath();
}

class FileVideoPickerService implements VideoPickerService {
  final FilePicker picker;
  FileVideoPickerService({FilePicker? picker})
      : picker = picker ?? FilePicker.platform;

  @override
  Future<String?> pickVideoPath() async {
    final result = await picker.pickFiles(type: FileType.video);
    if (result == null || result.files.isEmpty) return null;
    return result.files.single.path;
  }
}

/// Handle controller preview — production bọc VideoPlayerController,
/// test dùng fake để không cần platform channels.
abstract class VideoControllerHandle {
  Future<void> initialize();
  Future<void> dispose();
  bool get isInitialized;
  String get debugLabel;
  Widget buildPlayer();
}

class VideoPlayerHandle implements VideoControllerHandle {
  final VideoPlayerController controller;

  VideoPlayerHandle._(this.controller);

  factory VideoPlayerHandle.fromFile(String path) {
    return VideoPlayerHandle._(VideoPlayerController.file(File(path)));
  }

  factory VideoPlayerHandle.fromUrl(Uri uri) {
    return VideoPlayerHandle._(VideoPlayerController.networkUrl(uri));
  }

  @override
  Future<void> initialize() => controller.initialize();

  @override
  Future<void> dispose() => controller.dispose();

  @override
  bool get isInitialized => controller.value.isInitialized;

  @override
  String get debugLabel => 'video-player';

  @override
  Widget buildPlayer() => VideoPlayer(controller);
}

abstract class VideoControllerFactory {
  VideoControllerHandle file(String path);
  VideoControllerHandle network(Uri uri);
}

class DefaultVideoControllerFactory implements VideoControllerFactory {
  @override
  VideoControllerHandle file(String path) =>
      VideoPlayerHandle.fromFile(path);

  @override
  VideoControllerHandle network(Uri uri) =>
      VideoPlayerHandle.fromUrl(uri);
}

/// Màn “Dịch video” (UI-first): URL/file local + locale picker + preview 16:9
/// với caption demo bottom-center. Không upload, không gọi backend.
class VideoTranslationScreen extends StatefulWidget {
  final VideoPickerService picker;
  final VideoControllerFactory controllerFactory;

  const VideoTranslationScreen({
    super.key,
    VideoPickerService? picker,
    VideoControllerFactory? controllerFactory,
  })  : picker = picker ?? const _UnsetPicker(),
        controllerFactory = controllerFactory ?? const _UnsetFactory();
  // Thực tế constructor const không gọi được service có state;
  // _Unset* chỉ là sentinel — state sẽ thay bằng production khi null.
  // Để giữ API inject đơn giản cho test, dùng factory redirect dưới.

  @override
  State<VideoTranslationScreen> createState() => _VideoTranslationScreenState();
}

// Sentinel giữ constructor const-hợp lệ; state thay bằng production impl.
class _UnsetPicker implements VideoPickerService {
  const _UnsetPicker();
  @override
  Future<String?> pickVideoPath() => FileVideoPickerService().pickVideoPath();
}

class _UnsetFactory implements VideoControllerFactory {
  const _UnsetFactory();
  @override
  VideoControllerHandle file(String path) =>
      DefaultVideoControllerFactory().file(path);
  @override
  VideoControllerHandle network(Uri uri) =>
      DefaultVideoControllerFactory().network(uri);
}

class _VideoTranslationScreenState extends State<VideoTranslationScreen> {
  final _urlController = TextEditingController();
  final _langQueryController = TextEditingController();
  String _targetLocale = 'vi';
  VideoControllerHandle? _controller;
  String? _sourceLabel;
  String? _error;
  String? _mediaError;
  bool _socialWarning = false;
  bool _loading = false;

  @override
  void dispose() {
    _urlController.dispose();
    _langQueryController.dispose();
    // Không await trong sync lifecycle override — bắt đầu dispose là đủ.
    _controller?.dispose();
    _controller = null;
    super.dispose();
  }

  Future<void> _replaceController(VideoControllerHandle next, String label) async {
    final old = _controller;
    setState(() {
      _loading = true;
      _error = null;
      _mediaError = null;
    });
    if (old != null) {
      await old.dispose();
    }
    try {
      await next.initialize();
    } catch (_) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _mediaError =
            'Không phát được video (codec/container không hỗ trợ hoặc URL không phải media trực tiếp). Hãy thử file MP4 khác hoặc URL media truy cập trực tiếp được.';
      });
      await next.dispose();
      return;
    }
    if (!mounted) {
      await next.dispose();
      return;
    }
    setState(() {
      _controller = next;
      _sourceLabel = label;
      _loading = false;
    });
  }

  void _previewFromUrl() {
    final raw = _urlController.text.trim();
    if (!isAllowedVideoSource(raw)) {
      setState(() {
        _error =
            'URL chưa hợp lệ — cần URL tuyệt đối bắt đầu bằng http:// hoặc https://.';
        _socialWarning = false;
      });
      return;
    }
    setState(() {
      _error = null;
      _mediaError = null;
      _socialWarning = isSocialPageUrl(raw);
    });
    final uri = Uri.parse(raw);
    _replaceController(widget.controllerFactory.network(uri), raw);
  }

  Future<void> _pickFile() async {
    final path = await widget.picker.pickVideoPath();
    if (path == null || path.isEmpty) return;
    if (!mounted) return;
    setState(() {
      _error = null;
      _mediaError = null;
      _socialWarning = false;
    });
    await _replaceController(widget.controllerFactory.file(path), path);
  }

  Future<void> _clearSource() async {
    final old = _controller;
    setState(() {
      _controller = null;
      _sourceLabel = null;
      _error = null;
      _mediaError = null;
      _socialWarning = false;
    });
    if (old != null) {
      await old.dispose();
    }
  }

  Future<void> _openLanguagePicker() async {
    final selected = await showDialog<String>(
      context: context,
      builder: (dialogContext) => _LanguagePickerDialog(
        initialQuery: _langQueryController.text,
        current: _targetLocale,
      ),
    );
    if (selected != null && mounted) {
      setState(() => _targetLocale = selected);
    }
  }

  @override
  Widget build(BuildContext context) {
    final target = videoLanguages.firstWhere(
      (l) => l.code == _targetLocale,
      orElse: () => videoLanguages.firstWhere((l) => l.code == 'vi'),
    );
    return Scaffold(
      appBar: AppBar(title: const Text('Dịch video')),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          const Text(
            'Preview video local hoặc URL media có quyền truy cập, chọn ngôn ngữ đích. '
            'Dịch AI và xuất video thật đang chờ backend — giai đoạn này chỉ giao diện.',
            style: TextStyle(fontSize: 13),
          ),
          const SizedBox(height: 12),
          TextField(
            key: const Key('video-url-field'),
            controller: _urlController,
            keyboardType: TextInputType.url,
            decoration: const InputDecoration(
              labelText: 'URL video',
              hintText: 'https://example.com/video.mp4',
              border: OutlineInputBorder(),
            ),
          ),
          const SizedBox(height: 4),
          const Text(
            'Chỉ phát sau khi bấm “Xem trước”. URL trang Facebook/YouTube có thể không phát được '
            'và giai đoạn này không trích xuất audio từ player.',
            style: TextStyle(fontSize: 12),
          ),
          const SizedBox(height: 8),
          Row(
            children: [
              FilledButton.icon(
                key: const Key('preview-btn'),
                onPressed: _loading ? null : _previewFromUrl,
                icon: const Icon(Icons.play_arrow_outlined),
                label: const Text('Xem trước'),
              ),
              const SizedBox(width: 8),
              OutlinedButton.icon(
                key: const Key('pick-file-btn'),
                onPressed: _loading ? null : _pickFile,
                icon: const Icon(Icons.folder_open_outlined),
                label: const Text('Chọn video'),
              ),
              if (_controller != null) ...[
                const SizedBox(width: 8),
                TextButton(
                  key: const Key('clear-source-btn'),
                  onPressed: _clearSource,
                  child: const Text('Xoá'),
                ),
              ],
            ],
          ),
          if (_sourceLabel != null)
            Padding(
              padding: const EdgeInsets.only(top: 6),
              child: Text(
                _sourceLabel!,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: TextStyle(fontSize: 12, color: AppTheme.textMuted),
              ),
            ),
          if (_error != null)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(
                _error!,
                key: const Key('video-error'),
                style: TextStyle(fontSize: 13, color: AppTheme.dangerText),
              ),
            ),
          if (_socialWarning)
            const Padding(
              padding: EdgeInsets.only(top: 8),
              child: Text(
                'Link Facebook/YouTube dạng trang xem thường không phải URL media trực tiếp — '
                'trình duyệt có thể chặn nhúng/CORS/đăng nhập/DRM. Giai đoạn này không tải hộ hay bóc audio.',
                style: TextStyle(fontSize: 12),
              ),
            ),
          const SizedBox(height: 12),
          // Ngôn ngữ đích
          OutlinedButton.icon(
            key: const Key('language-picker-btn'),
            onPressed: _openLanguagePicker,
            icon: const Icon(Icons.translate_outlined),
            label: Text('${target.nativeName} — ${target.englishName} (${target.code})'),
          ),
          const SizedBox(height: 12),
          // Viewer 16:9 + caption bottom-center
          AspectRatio(
            aspectRatio: 16 / 9,
            child: Container(
              decoration: BoxDecoration(
                color: Colors.black,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Stack(
                fit: StackFit.expand,
                children: [
                  if (_loading)
                    const Center(
                      child: CircularProgressIndicator(),
                    )
                  else if (_controller != null)
                    _controller!.buildPlayer()
                  else
                    const Center(
                      child: Padding(
                        padding: EdgeInsets.all(16),
                        child: Text(
                          'Chưa có nguồn preview — nhập URL rồi bấm “Xem trước” hoặc chọn video local.',
                          textAlign: TextAlign.center,
                          style: TextStyle(color: Colors.white70, fontSize: 13),
                        ),
                      ),
                    ),
                  if (_controller != null && !_loading)
                    Positioned(
                      bottom: 40,
                      left: 0,
                      right: 0,
                      child: IgnorePointer(
                        child: Center(
                          child: Container(
                            key: const Key('demo-caption'),
                            margin: const EdgeInsets.symmetric(horizontal: 16),
                            padding: const EdgeInsets.symmetric(
                                horizontal: 10, vertical: 5),
                            decoration: BoxDecoration(
                              color: Colors.black.withValues(alpha: 0.7),
                              borderRadius: BorderRadius.circular(6),
                            ),
                            child: const Text(
                              '$videoDemoCaption — Phụ đề minh họa — AI chưa kết nối',
                              textAlign: TextAlign.center,
                              maxLines: 2,
                              overflow: TextOverflow.ellipsis,
                              style: TextStyle(
                                  color: Colors.white,
                                  fontSize: 13,
                                  fontWeight: FontWeight.w500),
                            ),
                          ),
                        ),
                      ),
                    ),
                ],
              ),
            ),
          ),
          if (_mediaError != null)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(
                _mediaError!,
                key: const Key('media-error'),
                style: TextStyle(fontSize: 13, color: AppTheme.dangerText),
              ),
            ),
          const SizedBox(height: 12),
          FilledButton.icon(
            key: const Key('export-btn'),
            onPressed: null,
            icon: const Icon(Icons.cloud_download_outlined),
            label: const Text('Xuất video'),
          ),
          const Text(
            'Xuất video đang chưa khả dụng — cần backend xử lý media/transcription/translation/rendering. '
            'Không có file tải xuống ở giai đoạn này.',
            style: TextStyle(fontSize: 12),
          ),
        ],
      ),
    );
  }
}

class _LanguagePickerDialog extends StatefulWidget {
  final String initialQuery;
  final String current;
  const _LanguagePickerDialog(
      {required this.initialQuery, required this.current});

  @override
  State<_LanguagePickerDialog> createState() => _LanguagePickerDialogState();
}

class _LanguagePickerDialogState extends State<_LanguagePickerDialog> {
  late final TextEditingController _query;

  @override
  void initState() {
    super.initState();
    _query = TextEditingController(text: widget.initialQuery);
  }

  @override
  void dispose() {
    _query.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final results = filterVideoLanguages(_query.text);
    return AlertDialog(
      title: const Text('Ngôn ngữ đích'),
      content: SizedBox(
        width: double.maxFinite,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            TextField(
              key: const Key('language-search-field'),
              controller: _query,
              autofocus: true,
              decoration: const InputDecoration(
                hintText: 'Tìm: vi, Vietnamese, Tiếng Việt…',
                prefixIcon: Icon(Icons.search_outlined),
              ),
              onChanged: (_) => setState(() {}),
            ),
            const SizedBox(height: 8),
            Flexible(
              child: ListView.builder(
                shrinkWrap: true,
                itemCount: results.length,
                itemBuilder: (context, index) {
                  final lang = results[index];
                  return ListTile(
                    dense: true,
                    title: Text(
                        '${lang.nativeName} — ${lang.englishName} (${lang.code})'),
                    trailing: lang.code == widget.current
                        ? const Icon(Icons.check_outlined)
                        : null,
                    onTap: () => Navigator.pop(context, lang.code),
                  );
                },
              ),
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context),
          child: const Text('Đóng'),
        ),
      ],
    );
  }
}
