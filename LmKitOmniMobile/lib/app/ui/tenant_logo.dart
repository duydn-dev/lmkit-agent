import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_provider.dart';
import '../../core/config/app_config_provider.dart';
import '../theme.dart';

/// Logo thương hiệu của tenant, dùng cho header khung chat và màn quản trị.
///
/// Hai endpoint phục vụ logo (`/api/tenant-branding/logo` cho chính tenant đang
/// đăng nhập, `/api/tenants/{id}/logo` cho admin) đều nằm sau `[Authorize]`.
/// Thẻ `<img>` của web tự gửi cookie JWT, còn `Image.network` của Flutter thì
/// không — nếu thiếu header `Authorization`, ảnh sẽ luôn 404 và người dùng chỉ
/// thấy ô trống. Vì vậy mọi chỗ hiển thị logo đều đi qua widget này.
///
/// Backend trả về ĐƯỜNG DẪN TƯƠNG ĐỐI (`/api/tenant-branding/logo?v=…`) nên phải
/// ghép với `apiBaseUrl` đang cấu hình; nếu sau này trả URL tuyệt đối thì vẫn
/// dùng nguyên.
class TenantLogo extends ConsumerWidget {
  const TenantLogo({
    super.key,
    required this.path,
    this.size = 32,
    this.border = true,
    this.fallback,
  });

  /// `logoUrl` từ `/api/auth/me`, hoặc đường dẫn tự dựng (màn admin).
  final String? path;

  final double size;

  /// Web bọc logo bằng viền xám mảnh (`border border-gray-200`).
  final bool border;

  /// Nội dung thay thế khi tenant chưa có logo hoặc tải ảnh thất bại.
  final Widget? fallback;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final child = _image(ref) ?? fallback ?? _initials(context, ref);

    return Container(
      width: size,
      height: size,
      decoration: BoxDecoration(
        shape: BoxShape.circle,
        color: AppTheme.surface,
        border: border ? Border.all(color: AppTheme.border) : null,
      ),
      clipBehavior: Clip.antiAlias,
      child: child,
    );
  }

  Widget? _image(WidgetRef ref) {
    final raw = path?.trim();
    if (raw == null || raw.isEmpty) return null;

    final token = ref.watch(authControllerProvider).asData?.value?.accessToken;
    if (token == null || token.isEmpty) return null;

    final uri = raw.startsWith('http')
        ? raw
        : '${ref.watch(appConfigProvider).apiBaseUrl}$raw';

    return Image.network(
      uri,
      headers: {'Authorization': 'Bearer $token'},
      fit: BoxFit.cover,
      errorBuilder: (context, _, _) => fallback ?? _initials(context, ref),
    );
  }

  /// Chữ cái đầu của tên đơn vị, như avatar dự phòng của web.
  Widget _initials(BuildContext context, WidgetRef ref) {
    final name =
        ref.watch(authControllerProvider).asData?.value?.user.tenant?.name ??
        '';
    final letter = name.trim().isEmpty
        ? '?'
        : name.trim().characters.first.toUpperCase();

    return Center(
      child: Text(
        letter,
        style: TextStyle(
          fontFamily: AppTheme.fontFamily,
          fontSize: size * 0.45,
          fontWeight: FontWeight.w700,
          color: AppTheme.govBlueDark,
        ),
      ),
    );
  }
}
