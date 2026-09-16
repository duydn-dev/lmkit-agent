import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:lmkit_omni_mobile/core/network/api_exception.dart';

/// Lỗi mạng/transport đi thẳng ra UI ở ~15 màn, nên câu chữ ở đây chính là câu
/// người dùng đọc. Test này ghim hai điều: **không bao giờ** để lọt câu tiếng
/// Anh của thư viện, và câu của máy chủ luôn được ưu tiên.
void main() {
  DioException dioError({
    required DioExceptionType type,
    Response<dynamic>? response,
    String? message,
  }) => DioException(
    requestOptions: RequestOptions(path: '/api/auth/login'),
    type: type,
    response: response,
    message: message,
  );

  group('ApiException.fromDio', () {
    test('mất kết nối thì ra câu tiếng Việt, không lộ chữ của thư viện', () {
      final exception = ApiException.fromDio(
        dioError(
          type: DioExceptionType.connectionError,
          message:
              'The connection errored: Connection refused This indicates an '
              'error which most likely cannot be solved by the library.',
        ),
      );

      expect(
        exception.message,
        'Không kết nối được máy chủ. Kiểm tra lại đường mạng rồi thử lại.',
      );
      expect(exception.message, isNot(contains('Connection refused')));
      expect(exception.code, 'connectionError');
    });

    test('câu của máy chủ được ưu tiên hơn câu mặc định', () {
      final exception = ApiException.fromDio(
        dioError(
          type: DioExceptionType.badResponse,
          response: Response<dynamic>(
            requestOptions: RequestOptions(path: '/api/auth/login'),
            statusCode: 401,
            data: {'message': 'Sai tài khoản hoặc mật khẩu.'},
          ),
        ),
      );

      expect(exception.message, 'Sai tài khoản hoặc mật khẩu.');
      expect(exception.statusCode, 401);
    });

    test('máy chủ trả chuỗi trắng thì rơi về câu theo mã trạng thái', () {
      final exception = ApiException.fromDio(
        dioError(
          type: DioExceptionType.badResponse,
          response: Response<dynamic>(
            requestOptions: RequestOptions(path: '/api/chat/sessions'),
            statusCode: 403,
            data: '   ',
          ),
        ),
      );

      expect(
        exception.message,
        'Bạn không có quyền thực hiện thao tác này.',
      );
      expect(exception.statusCode, 403);
    });

    test('mã 5xx không có nội dung thì báo máy chủ đang lỗi', () {
      final exception = ApiException.fromDio(
        dioError(
          type: DioExceptionType.badResponse,
          response: Response<dynamic>(
            requestOptions: RequestOptions(path: '/api/admin/users'),
            statusCode: 503,
          ),
        ),
      );

      expect(exception.message, 'Máy chủ đang gặp sự cố. Vui lòng thử lại sau.');
    });

    test('hết hạn chờ đọc và gửi đều có câu riêng, không nhắc tới Dio', () {
      final receive = ApiException.fromDio(
        dioError(type: DioExceptionType.receiveTimeout),
      );
      final send = ApiException.fromDio(
        dioError(type: DioExceptionType.sendTimeout),
      );
      final connect = ApiException.fromDio(
        dioError(type: DioExceptionType.connectionTimeout),
      );

      expect(receive.message, 'Máy chủ phản hồi quá lâu. Vui lòng thử lại.');
      expect(send.message, receive.message);
      expect(connect.message, 'Máy chủ không phản hồi kịp. Vui lòng thử lại.');
      for (final m in [receive.message, send.message, connect.message]) {
        expect(m.toLowerCase(), isNot(contains('dio')));
      }
    });

    test('lỗi không rõ nguyên nhân vẫn ra câu tiếng Việt', () {
      final exception = ApiException.fromDio(
        dioError(
          type: DioExceptionType.unknown,
          message: 'SocketException: Connection refused (OS Error: ...)',
        ),
      );

      expect(exception.message, 'Không thể kết nối đến máy chủ.');
    });
  });
}
