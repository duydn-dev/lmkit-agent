import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:path_provider/path_provider.dart';
import 'package:record/record.dart';

import '../../core/auth/auth_provider.dart';
import '../../core/network/api_client.dart';
import '../../core/platform/local_file.dart';

/// Provider của bộ ghi âm — kiểu là [VoiceTranscriber] để test thay được bằng
/// bản giả: plugin `record` cần micro thật và không chạy trong `flutter test`,
/// nên giao diện của khối ghi âm sẽ không kiểm được nếu provider buộc đúng lớp
/// cụ thể.
final voiceRecorderProvider = Provider<VoiceTranscriber>(
  (ref) => VoiceRecorder(ref.watch(apiClientProvider)),
);

/// Việc mà khối soạn tin cần ở micro: xin quyền, ghi, dừng để phiên âm, huỷ, và
/// mức âm lượng để vẽ đồng hồ sóng.
abstract interface class VoiceTranscriber {
  /// Xin quyền micro (có hỏi hệ thống lần đầu).
  Future<bool> hasPermission();

  /// Bắt đầu ghi.
  Future<void> start();

  /// Dừng ghi, gửi lên máy chủ và trả về văn bản phiên âm (rỗng nếu không có gì).
  Future<String> stopAndTranscribe();

  /// Bỏ bản ghi hiện tại.
  Future<void> cancel();

  /// Mức âm lượng 0..1, phát đều khi đang ghi.
  Stream<double> levels();
}

/// Thu âm bằng micro thiết bị rồi gửi lên `/api/speech/transcribe-upload`.
///
/// File ghi tạm luôn bị xoá sau khi phiên âm xong, kể cả khi lỗi.
class VoiceRecorder implements VoiceTranscriber {
  VoiceRecorder(this._client);

  final ApiClient _client;
  final AudioRecorder _recorder = AudioRecorder();

  /// Ghi âm gửi lên server cần một tệp tạm để upload.
  ///
  /// Trình duyệt web không có hệ thống tệp, nên nút micro bị ẩn ở đó thay vì
  /// báo lỗi khó hiểu khi người dùng bấm vào.
  static bool get isSupported => !kIsWeb;

  String? _path;

  @override
  Future<bool> hasPermission() => _recorder.hasPermission();

  /// Mức âm lượng hiện tại trong khoảng 0..1, phát đều khi đang ghi.
  ///
  /// Dùng để vẽ cột sóng trong khối soạn tin — người dùng phải **thấy** micro
  /// đang nghe thấy tiếng của mình, nếu không họ sẽ tưởng nút không ăn và bấm
  /// loạn (hoặc chuyển sang bàn phím hệ thống).
  ///
  /// `record` trả dBFS (số âm, ~-160..0). Khoảng nói bình thường rơi vào khoảng
  /// -45..0 dB, nên lấy -45 dB làm đáy: dưới ngưỡng đó coi như im lặng, còn lại
  /// trải đều 0..1 để cột sóng nhúc nhích theo giọng nói.
  @override
  Stream<double> levels() => _recorder
      .onAmplitudeChanged(const Duration(milliseconds: 120))
      .map((amplitude) => ((amplitude.current + 45) / 45).clamp(0.0, 1.0));

  /// Bắt đầu ghi vào file tạm định dạng m4a (AAC-LC) — định dạng backend chấp nhận.
  @override
  Future<void> start() async {
    if (!isSupported) {
      throw UnsupportedError(
        'Ghi âm tin nhắn thoại chưa hỗ trợ trên nền tảng này.',
      );
    }
    final directory = await getTemporaryDirectory();
    final path =
        '${directory.path}/lmkit-voice-${DateTime.now().millisecondsSinceEpoch}.m4a';
    await _recorder.start(
      const RecordConfig(
        encoder: AudioEncoder.aacLc,
        bitRate: 128000,
        sampleRate: 44100,
        numChannels: 1,
      ),
      path: path,
    );
    _path = path;
  }

  /// Dừng ghi, gửi file lên server và trả về văn bản phiên âm.
  ///
  /// Trả về chuỗi rỗng nếu không có nội dung nào được ghi.
  @override
  Future<String> stopAndTranscribe() async {
    final path = await _recorder.stop() ?? _path;
    _path = null;
    if (path == null) return '';

    try {
      if (!await localFileExists(path)) return '';
      final form = FormData.fromMap({
        'audio': await MultipartFile.fromFile(
          path,
          filename: 'voice.m4a',
          contentType: DioMediaType('audio', 'mp4'),
        ),
      });
      final response = await _client.upload(
        '/api/speech/transcribe-upload',
        data: form,
      );
      final data = response.data;
      if (data is Map) {
        return data['text']?.toString().trim() ?? '';
      }
      return '';
    } finally {
      // File ghi tạm không được giữ lại trên thiết bị.
      await deleteLocalFileIfExists(path);
    }
  }

  @override
  Future<void> cancel() async {
    await _recorder.cancel();
    final path = _path;
    _path = null;
    if (path != null) await deleteLocalFileIfExists(path);
  }

  Future<void> dispose() async {
    await _recorder.dispose();
  }
}
