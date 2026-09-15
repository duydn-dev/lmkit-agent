import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/config/app_config_provider.dart';
import '../../core/network/dio_factory.dart';
import 'shared_chat_models.dart';

/// Đọc transcript công khai của một link chia sẻ.
///
/// Endpoint này `AllowAnonymous` và **không** đi qua `ApiClient`: nếu đi qua
/// interceptor, một 401 (không bao giờ xảy ra ở đây) có thể kéo theo refresh
/// token và đẩy người xem ẩn danh về màn đăng nhập.
class ShareRepository {
  ShareRepository(this._dio);

  final Dio _dio;

  /// Nhận cả URL đầy đủ (`.../share/<token>`) hoặc token trần.
  static String? extractToken(String input) {
    final value = input.trim();
    if (value.isEmpty) return null;

    final uri = Uri.tryParse(value);
    if (uri != null && uri.hasScheme) {
      final segments = uri.pathSegments.where((s) => s.isNotEmpty).toList();
      final index = segments.indexOf('share');
      if (index >= 0 && index + 1 < segments.length) return segments[index + 1];
      return segments.isEmpty ? null : segments.last;
    }

    // Không phải URL: nhận token trần, cắt phần query/hash nếu người dùng dán kèm.
    return value
        .split(RegExp(r'[?#/]'))
        .firstWhere((part) => part.isNotEmpty, orElse: () => '');
  }

  Future<SharedChatResult> fetch(String token) async {
    try {
      final response = await _dio.get<dynamic>(
        '/api/share/chat/${Uri.encodeComponent(token)}',
      );
      final data = response.data;
      if (data is! Map) {
        return const SharedChatResult(
          status: SharedChatStatus.failed,
          message: 'Phản hồi không hợp lệ từ server.',
        );
      }
      return SharedChatResult(
        status: SharedChatStatus.ok,
        chat: SharedChatModel.fromJson(Map<String, dynamic>.from(data)),
      );
    } on DioException catch (error) {
      final data = error.response?.data;
      final body = data is Map ? Map<String, dynamic>.from(data) : null;
      final reason = body?['reason']?.toString();
      final refusedAt = switch (body?['revokedAtUtc'] ??
          body?['expiredAtUtc']) {
        final String value => DateTime.tryParse(value),
        _ => null,
      };

      return switch (error.response?.statusCode) {
        404 => const SharedChatResult(status: SharedChatStatus.notFound),
        410 => SharedChatResult(
          status: reason == 'expired'
              ? SharedChatStatus.expired
              : SharedChatStatus.revoked,
          refusedAtUtc: refusedAt,
        ),
        _ => SharedChatResult(
          status: SharedChatStatus.failed,
          message: error.message ?? 'Không đọc được đoạn chat được chia sẻ.',
        ),
      };
    }
  }
}

final shareRepositoryProvider = Provider<ShareRepository>(
  (ref) => ShareRepository(buildDio(ref.watch(appConfigProvider))),
);
