import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import 'app/app.dart';
import 'core/config/app_config.dart';
import 'core/config/app_config_provider.dart';
import 'core/config/app_config_store.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // Cấu hình phải sẵn sàng trước frame đầu tiên: nó quyết định API URL mà mọi
  // request đầu tiên (kể cả khôi phục phiên) sẽ dùng.
  final store = AppConfigStore(const FlutterSecureStorage());
  final override = await store.readApiBaseUrl();
  final envConfig = AppConfig.fromEnvironment();
  final config = override == null
      ? envConfig
      : envConfig.copyWith(
          apiBaseUrl: AppConfig.normalizeBaseUrl(override),
          isApiBaseUrlOverridden: true,
        );

  runApp(
    ProviderScope(
      overrides: [bootstrapAppConfigProvider.overrideWithValue(config)],
      child: const LmKitOmniApp(),
    ),
  );
}
