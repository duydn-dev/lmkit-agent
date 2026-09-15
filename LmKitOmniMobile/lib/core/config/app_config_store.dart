import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Lưu override `apiBaseUrl` do người dùng đặt trong app.
///
/// Dùng chung `FlutterSecureStorage` với session để không phải thêm dependency
/// và để giá trị tồn tại qua các lần mở app trên cả Android/iOS.
class AppConfigStore {
  AppConfigStore(this._storage);

  static const _apiBaseUrlKey = 'lmkit.mobile.config.apiBaseUrl.v1';

  final FlutterSecureStorage _storage;

  Future<String?> readApiBaseUrl() async {
    final raw = await _storage.read(key: _apiBaseUrlKey);
    if (raw == null || raw.trim().isEmpty) return null;
    return raw.trim();
  }

  Future<void> writeApiBaseUrl(String value) =>
      _storage.write(key: _apiBaseUrlKey, value: value.trim());

  Future<void> clearApiBaseUrl() => _storage.delete(key: _apiBaseUrlKey);
}
