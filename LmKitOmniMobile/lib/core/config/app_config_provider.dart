import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'app_config.dart';

/// Cấu hình đã sẵn sàng trước `runApp`.
///
/// `main()` override provider này bằng giá trị đọc từ
/// `--dart-define-from-file=env/<flavor>.json`, nên mọi provider phía dưới (Dio,
/// repository, màn hình) luôn thấy đúng API URL ngay từ frame đầu tiên — kể cả
/// request khôi phục phiên. Chạy trong test thì `AppConfig.fromEnvironment()` là
/// mặc định.
final bootstrapAppConfigProvider = Provider<AppConfig>(
  (ref) => AppConfig.fromEnvironment(),
);

/// Cấu hình đang có hiệu lực.
///
/// **Chỉ đọc.** Địa chỉ máy chủ là giá trị build-time: app không có đường nào
/// sửa nó lúc chạy — không màn cấu hình kết nối, không bản ghi lưu trên thiết
/// bị. Nhờ vậy mọi bản cài trong một môi trường đều trỏ về đúng một máy chủ đã
/// được duyệt, và không thể có chuyện một lần bấm nhầm để lại URL sai trên máy
/// người dùng. Muốn đổi thì sửa `env/<flavor>.json` rồi build lại (xem
/// `env/README.md`).
final appConfigProvider = Provider<AppConfig>(
  (ref) => ref.watch(bootstrapAppConfigProvider),
);
