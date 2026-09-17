import 'package:dio/dio.dart';

class ApiException implements Exception {
  const ApiException({required this.message, this.statusCode, this.code});

  final String message;
  final int? statusCode;
  final String? code;

  factory ApiException.fromDio(DioException error) {
    return ApiException(
      message: _messageFromServer(error.response?.data) ?? _fallbackFor(error),
      statusCode: error.response?.statusCode,
      code: error.type.name,
    );
  }

  /// Câu chữ máy chủ gửi kèm trong thân phản hồi, nếu có.
  ///
  /// Đây là câu đã được soạn theo ngữ cảnh nghiệp vụ nên luôn ưu tiên hơn câu
  /// chữ mặc định của app.
  static String? _messageFromServer(Object? data) {
    final String? raw = switch (data) {
      Map<String, dynamic> map =>
        (map['message'] ?? map['title'] ?? map['detail']) as String?,
      String text => text,
      _ => null,
    };
    final trimmed = raw?.trim();
    return (trimmed == null || trimmed.isEmpty) ? null : trimmed;
  }

  /// Câu chữ cho lỗi **không** có thân phản hồi — mạng đứt, hết hạn chờ, sai
  /// chứng chỉ…
  ///
  /// Cố ý **không** dùng `DioException.message`: đó là câu tiếng Anh của thư
  /// viện, từng lọt thẳng ra màn đăng nhập ("The connection errored: Connection
  /// refused This indicates an error which most likely cannot be solved by the
  /// library.") khi backend chưa chạy. Người dùng cuối không đọc được, và nó
  /// không cho biết phải làm gì tiếp.
  static String _fallbackFor(DioException error) {
    return switch (error.type) {
      DioExceptionType.connectionTimeout =>
        'Máy chủ không phản hồi kịp. Vui lòng thử lại.',
      DioExceptionType.sendTimeout ||
      DioExceptionType.receiveTimeout ||
      DioExceptionType.transformTimeout =>
        'Máy chủ phản hồi quá lâu. Vui lòng thử lại.',
      DioExceptionType.connectionError =>
        'Không kết nối được máy chủ. Kiểm tra lại đường mạng rồi thử lại.',
      DioExceptionType.badCertificate =>
        'Chứng chỉ bảo mật của máy chủ không hợp lệ.',
      DioExceptionType.cancel => 'Yêu cầu đã bị huỷ.',
      DioExceptionType.badResponse => _fallbackForStatus(
        error.response?.statusCode,
      ),
      DioExceptionType.unknown => 'Không thể kết nối đến máy chủ.',
    };
  }

  /// Câu chữ theo mã trạng thái khi máy chủ trả lỗi mà không kèm nội dung đọc được.
  static String _fallbackForStatus(int? statusCode) {
    return switch (statusCode) {
      400 => 'Dữ liệu gửi lên không hợp lệ.',
      401 => 'Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.',
      403 => 'Bạn không có quyền thực hiện thao tác này.',
      404 => 'Không tìm thấy dữ liệu yêu cầu.',
      408 => 'Máy chủ không phản hồi kịp. Vui lòng thử lại.',
      409 => 'Dữ liệu đã thay đổi ở nơi khác. Vui lòng tải lại.',
      413 => 'Tệp gửi lên quá lớn.',
      429 => 'Quá nhiều yêu cầu. Vui lòng chờ một lát rồi thử lại.',
      500 ||
      502 ||
      503 ||
      504 => 'Máy chủ đang gặp sự cố. Vui lòng thử lại sau.',
      _ => 'Không thể kết nối đến máy chủ.',
    };
  }

  @override
  String toString() => message;
}
