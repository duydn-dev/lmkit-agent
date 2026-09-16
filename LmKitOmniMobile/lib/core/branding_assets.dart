/// Đường dẫn asset của dấu nhận diện đơn vị — **Quốc huy Việt Nam**.
///
/// Quốc huy là logo **mặc định** của mọi tenant: đơn vị chưa tải lên logo riêng,
/// ảnh tải hỏng, hay token chưa sẵn sàng thì chỗ hiển thị rơi về đây chứ không
/// phải ô trống hay avatar chữ cái. Vì logo là cấu hình theo từng tenant
/// (`logoUrl` trong `/api/auth/me`, tải lên ở màn Quản trị), app phải có sẵn một
/// dấu nhận diện dùng được khi **không có mạng** — nên nó nằm trong gói cài chứ
/// không gọi về máy chủ.
///
/// Để hai bản khớp nhau, PNG được sinh lại từ chính SVG (`flutter test
/// test/emblem_test.dart` kiểm cả hai), không chép tay một tệp nhị phân.
abstract final class BrandingAssets {
  /// Quốc huy dạng vector — dùng để **vẽ** trong app (`flutter_svg`).
  static const emblemSvg = 'assets/images/quochuy.svg';

  /// Cùng Quốc huy ở dạng raster — dùng ở chỗ cần *bytes ảnh thật*, ví dụ lớp dữ
  /// liệu mẫu phục vụ `/api/tenant-branding/logo` (để kiểm đúng đường ảnh phải
  /// gửi kèm `Authorization`, mà ảnh SVG thì `Image` không giải mã được).
  static const emblemPng = 'assets/images/quochuy.png';
}
