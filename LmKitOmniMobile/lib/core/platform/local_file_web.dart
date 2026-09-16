/// Bản web: không có hệ thống tệp cục bộ.
///
/// Trình duyệt không cho ghi tệp tạm để gửi lên `/api/speech/transcribe-upload`,
/// nên ghi âm bị tắt ở nền tảng này (`VoiceRecorder.isSupported` trả `false`) và
/// hai hàm dưới không bao giờ được gọi tới.
Future<bool> localFileExists(String path) async => false;

Future<void> deleteLocalFileIfExists(String path) async {}
