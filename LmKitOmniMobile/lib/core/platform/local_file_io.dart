import 'dart:io';

/// `true` nếu tệp tại [path] tồn tại (Android/iOS/desktop).
Future<bool> localFileExists(String path) => File(path).exists();

/// Xoá tệp nếu có; bỏ qua lỗi vì tệp rác trong thư mục tạm không ảnh hưởng.
Future<void> deleteLocalFileIfExists(String path) async {
  final file = File(path);
  if (!await file.exists()) return;
  try {
    await file.delete();
  } catch (_) {
    // Bỏ qua.
  }
}
