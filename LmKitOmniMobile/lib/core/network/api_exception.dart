import 'package:dio/dio.dart';

class ApiException implements Exception {
  const ApiException({required this.message, this.statusCode, this.code});

  final String message;
  final int? statusCode;
  final String? code;

  factory ApiException.fromDio(DioException error) {
    final data = error.response?.data;
    var message = 'Không thể kết nối đến máy chủ.';
    if (data is Map<String, dynamic>) {
      message =
          data['message'] as String? ??
          data['title'] as String? ??
          data['detail'] as String? ??
          message;
    } else if (data is String && data.trim().isNotEmpty) {
      message = data;
    } else if (error.message?.trim().isNotEmpty == true) {
      message = error.message!;
    }
    return ApiException(
      message: message,
      statusCode: error.response?.statusCode,
      code: error.type.name,
    );
  }

  @override
  String toString() => message;
}
