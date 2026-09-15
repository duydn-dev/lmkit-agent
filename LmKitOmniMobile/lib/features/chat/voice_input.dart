import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:path_provider/path_provider.dart';
import 'package:record/record.dart';

import '../../core/auth/auth_provider.dart';
import '../../core/network/api_client.dart';

final voiceRecorderProvider = Provider<VoiceRecorder>(
  (ref) => VoiceRecorder(ref.watch(apiClientProvider)),
);

/// Thu âm bằng micro thiết bị rồi gửi lên `/api/speech/transcribe-upload`.
///
/// File ghi tạm luôn bị xoá sau khi phiên âm xong, kể cả khi lỗi.
class VoiceRecorder {
  VoiceRecorder(this._client);

  final ApiClient _client;
  final AudioRecorder _recorder = AudioRecorder();

  String? _path;

  /// Xin quyền micro (có hỏi hệ thống lần đầu).
  Future<bool> hasPermission() => _recorder.hasPermission();

  /// Bắt đầu ghi vào file tạm định dạng m4a (AAC-LC) — định dạng backend chấp nhận.
  Future<void> start() async {
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
  Future<String> stopAndTranscribe() async {
    final path = await _recorder.stop() ?? _path;
    _path = null;
    if (path == null) return '';

    final file = File(path);
    try {
      if (!await file.exists()) return '';
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
      if (await file.exists()) {
        try {
          await file.delete();
        } catch (_) {
          // Bỏ qua: file rác trong thư mục tạm không ảnh hưởng chức năng.
        }
      }
    }
  }

  Future<void> cancel() async {
    await _recorder.cancel();
    final path = _path;
    _path = null;
    if (path != null) {
      final file = File(path);
      if (await file.exists()) {
        try {
          await file.delete();
        } catch (_) {
          // Bỏ qua.
        }
      }
    }
  }

  Future<void> dispose() async {
    await _recorder.dispose();
  }
}
