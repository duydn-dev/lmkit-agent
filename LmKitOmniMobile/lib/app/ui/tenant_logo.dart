import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_svg/flutter_svg.dart';

import '../../core/auth/auth_provider.dart';
import '../../core/branding_assets.dart';
import '../../core/config/app_config_provider.dart';
import '../../core/network/dio_image.dart';
import '../theme.dart';

/// Logo thương hiệu của tenant, dùng cho header khung chat và màn quản trị.
///
/// Hai endpoint phục vụ logo (`/api/tenant-branding/logo` cho chính tenant đang
/// đăng nhập, `/api/tenants/{id}/logo` cho admin) đều nằm sau `[Authorize]`.
/// Thẻ `<img>` của web tự gửi cookie JWT, còn HTTP client mặc định của Flutter
/// thì không — nếu thiếu header `Authorization`, ảnh sẽ luôn 404 và người dùng
/// chỉ thấy ô trống. Vì vậy mọi chỗ hiển thị logo đều đi qua widget này, và ảnh
/// được tải bằng [DioImage] trên chính `Dio` của app (xem [_image]).
///
/// Backend trả về ĐƯỜNG DẪN TƯƠNG ĐỐI (`/api/tenant-branding/logo?v=…`) nên phải
/// ghép với `apiBaseUrl` đang cấu hình; nếu sau này trả URL tuyệt đối thì vẫn
/// dùng nguyên.
///
/// **Logo là cấu hình theo từng tenant**: đơn vị tự tải logo riêng lên (màn
/// Quản trị → Tenant Management), và khi đơn vị chưa có logo — hoặc ảnh tải
/// hỏng, hoặc token chưa sẵn sàng — thì chỗ này rơi về **Quốc huy**, không phải
/// avatar chữ cái. Tên đơn vị của các cơ quan nhà nước thường bắt đầu cùng một
/// cụm từ ("Trung tâm…", "Sở…"), nên avatar chữ cái vừa vô nghĩa vừa dễ trùng;
/// Quốc huy là dấu nhận diện đúng và luôn có sẵn trong gói cài.
class TenantLogo extends ConsumerWidget {
  const TenantLogo({
    super.key,
    this.path,
    this.size = 32,
    this.border = true,
    this.fallback,
  });

  /// `logoUrl` từ `/api/auth/me`, hoặc đường dẫn tự dựng (màn admin).
  ///
  /// Bỏ trống = đơn vị chưa cấu hình logo → hiện Quốc huy.
  final String? path;

  final double size;

  /// Web bọc logo bằng viền xám mảnh (`border border-gray-200`).
  final bool border;

  /// Nội dung thay thế khi tenant chưa có logo hoặc tải ảnh thất bại.
  ///
  /// Mặc định — khi không truyền gì — là Quốc huy.
  final Widget? fallback;

  /// Quốc huy dùng làm logo mặc định cho mọi tenant (xem [BrandingAssets]).
  static const emblemAsset = BrandingAssets.emblemSvg;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final child = _image(ref) ?? fallback ?? const NationalEmblem();

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

    // Tải bằng `Dio` của app chứ không phải `Image.network`: `Image.network`
    // dựng HTTP client riêng, nên nó không có Bearer token của
    // `AuthInterceptor`, không dùng timeout/base URL đang cấu hình, và đặc biệt
    // là **không** đi qua `MockHttpAdapter` — ở chế độ dữ liệu mẫu nó vẫn gọi
    // ra máy chủ thật rồi thất bại. Token do interceptor gắn, không truyền tay.
    return Image(
      image: DioImage(ref.watch(apiClientProvider).dio, uri),
      fit: BoxFit.cover,
      // Ảnh hỏng (tenant vừa xoá logo, mạng lỗi, token hết hạn) cũng rơi về
      // Quốc huy — không để ô trống hay avatar chữ cái.
      errorBuilder: (context, _, _) => fallback ?? const NationalEmblem(),
    );
  }
}

/// Quốc huy Việt Nam — mặc định của [TenantLogo].
///
/// Vẽ từ SVG đã đóng gói trong app chứ không gọi mạng: logo mặc định phải hiện
/// được cả khi chưa đăng nhập, khi mất mạng, hoặc khi backend chưa có ảnh nào.
class NationalEmblem extends StatelessWidget {
  const NationalEmblem({super.key, this.size});

  /// Bỏ trống thì lấp đầy ô chứa (dùng trong [TenantLogo]).
  final double? size;

  @override
  Widget build(BuildContext context) {
    final emblem = SvgPicture.asset(
      TenantLogo.emblemAsset,
      fit: BoxFit.contain,
      semanticsLabel: 'Quốc huy Việt Nam',
    );
    return size == null
        ? emblem
        : SizedBox(width: size, height: size, child: emblem);
  }
}
