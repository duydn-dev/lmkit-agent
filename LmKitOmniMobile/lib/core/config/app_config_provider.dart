import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../storage/secure_storage_provider.dart';
import 'app_config.dart';
import 'app_config_store.dart';

/// Cấu hình đã load xong trước `runApp` (đã áp override từ thiết bị nếu có).
///
/// `main()` override provider này bằng giá trị đọc từ đĩa, nên mọi provider phía
/// dưới luôn thấy đúng API URL ngay từ frame đầu tiên.
final bootstrapAppConfigProvider = Provider<AppConfig>(
  (ref) => AppConfig.fromEnvironment(),
);

final appConfigStoreProvider = Provider<AppConfigStore>(
  (ref) => AppConfigStore(ref.watch(secureStorageProvider)),
);

/// Cấu hình đang có hiệu lực. Đổi ở đây sẽ rebuild `apiClientProvider` và toàn
/// bộ repository phía trên, không cần restart app.
class AppConfigController extends Notifier<AppConfig> {
  @override
  AppConfig build() => ref.watch(bootstrapAppConfigProvider);

  /// Lưu API URL mới xuống thiết bị. Truyền chuỗi rỗng để quay về giá trị env.
  ///
  /// Trả về `true` nếu URL thực sự thay đổi.
  Future<bool> setApiBaseUrl(String raw) async {
    final trimmed = raw.trim();
    if (trimmed.isEmpty) return resetApiBaseUrl();
    if (!AppConfig.isValidBaseUrl(trimmed)) {
      throw ArgumentError.value(raw, 'apiBaseUrl', 'URL không hợp lệ');
    }
    final normalized = AppConfig.normalizeBaseUrl(trimmed);
    if (normalized == state.apiBaseUrl) return false;

    await ref.read(appConfigStoreProvider).writeApiBaseUrl(normalized);
    state = state.copyWith(
      apiBaseUrl: normalized,
      isApiBaseUrlOverridden: true,
    );
    return true;
  }

  /// Bỏ override, quay lại giá trị đến từ `env/*.json`.
  Future<bool> resetApiBaseUrl() async {
    await ref.read(appConfigStoreProvider).clearApiBaseUrl();
    final envConfig = ref.read(bootstrapAppConfigProvider);
    if (envConfig.apiBaseUrl == state.apiBaseUrl) {
      state = envConfig;
      return false;
    }
    state = envConfig;
    return true;
  }
}

final appConfigProvider = NotifierProvider<AppConfigController, AppConfig>(
  AppConfigController.new,
);
