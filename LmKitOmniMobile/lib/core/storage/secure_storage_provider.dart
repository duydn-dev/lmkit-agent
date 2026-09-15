import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Một instance `FlutterSecureStorage` duy nhất cho cả app.
///
/// Đặt ở `core/storage` để cả session token lẫn cấu hình môi trường dùng chung,
/// không phụ thuộc lẫn nhau.
final secureStorageProvider = Provider<FlutterSecureStorage>(
  (_) => const FlutterSecureStorage(),
);
