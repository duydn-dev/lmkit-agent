import 'dart:ui' as ui;

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';

/// `ImageProvider` tải ảnh bằng chính [Dio] của ứng dụng thay vì HTTP client
/// riêng của `Image.network`.
///
/// Vì sao cần: mọi ảnh nghiệp vụ (logo đơn vị) đều nằm sau `[Authorize]`, và
/// `Image.network` **không** đi qua `Dio` — nó không có interceptor, không có
/// timeout, không có base URL, và quan trọng nhất là không đi qua
/// `MockHttpAdapter`. Hệ quả thực tế đã gặp: ở chế độ dữ liệu mẫu, logo vẫn gọi
/// ra `http://localhost:5032/...` rồi thất bại (`ERR_CONNECTION_REFUSED`),
/// trong khi mọi request khác đều được phục vụ tại chỗ.
///
/// Dùng `ImageProvider` (thay vì tự tải byte rồi `Image.memory`) để giữ nguyên
/// `ImageCache` của Flutter: ảnh chỉ tải và giải mã một lần cho mỗi khoá.
@immutable
class DioImage extends ImageProvider<DioImage> {
  const DioImage(
    this.dio,
    this.url, {
    this.headers = const {},
    this.scale = 1.0,
  });

  /// Client trung tâm của app (đã có interceptor + adapter theo cấu hình).
  final Dio dio;

  /// URL tuyệt đối đã ghép sẵn với `apiBaseUrl`.
  final String url;

  final Map<String, String> headers;

  /// Tỉ lệ điểm ảnh logic / điểm ảnh vật lý, giữ nguyên như `Image.network`.
  final double scale;

  @override
  Future<DioImage> obtainKey(ImageConfiguration configuration) =>
      SynchronousFuture<DioImage>(this);

  @override
  ImageStreamCompleter loadImage(DioImage key, ImageDecoderCallback decode) {
    return MultiFrameImageStreamCompleter(
      codec: _load(key, decode),
      scale: key.scale,
      debugLabel: key.url,
      informationCollector: () => <DiagnosticsNode>[
        DiagnosticsProperty<DioImage>('Image provider', this),
      ],
    );
  }

  Future<ui.Codec> _load(DioImage key, ImageDecoderCallback decode) async {
    try {
      final response = await key.dio.get<List<int>>(
        key.url,
        options: Options(
          responseType: ResponseType.bytes,
          headers: key.headers,
        ),
      );

      final bytes = response.data;
      if (bytes == null || bytes.isEmpty) {
        throw NetworkImageLoadException(
          statusCode: response.statusCode ?? 0,
          uri: Uri.parse(key.url),
        );
      }

      return decode(
        await ui.ImmutableBuffer.fromUint8List(Uint8List.fromList(bytes)),
      );
    } catch (error) {
      // Ảnh hỏng mà im lặng là kiểu lỗi khó lần nhất: giao diện chỉ hiện avatar
      // dự phòng và không ai biết vì sao. Ghi lại lý do thật khi chạy debug.
      debugPrint('[image] không tải được ${key.url}: $error');
      rethrow;
    }
  }

  @override
  bool operator ==(Object other) =>
      other is DioImage &&
      other.url == url &&
      other.scale == scale &&
      identical(other.dio, dio) &&
      mapEquals(other.headers, headers);

  @override
  int get hashCode => Object.hash(url, scale, identityHashCode(dio));

  @override
  String toString() => 'DioImage("$url", scale: $scale)';
}
