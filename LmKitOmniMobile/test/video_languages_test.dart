import 'package:flutter_test/flutter_test.dart';

import 'package:lmkit_omni_mobile/features/video_translation/video_languages.dart';

void main() {
  test('có đủ 184 ISO 639-1 entries, mã duy nhất', () {
    expect(videoLanguages.length, 184);
    final codes = videoLanguages.map((l) => l.code).toList();
    expect(codes.toSet().length, 184);
    for (final lang in videoLanguages) {
      expect(lang.code, matches(RegExp(r'^[a-z]{2}$')));
      expect(lang.nativeName.trim(), isNotEmpty);
      expect(lang.englishName.trim(), isNotEmpty);
    }
  });

  test('tìm được vi / Vietnamese / Tiếng Việt', () {
    expect(filterVideoLanguages('vi').any((l) => l.code == 'vi'), isTrue);
    expect(
        filterVideoLanguages('Vietnamese').any((l) => l.code == 'vi'), isTrue);
    expect(
        filterVideoLanguages('vietnamese').any((l) => l.code == 'vi'), isTrue);
    expect(
        filterVideoLanguages('Tiếng Việt').any((l) => l.code == 'vi'), isTrue);
    expect(
        filterVideoLanguages('tieng viet').any((l) => l.code == 'vi'), isTrue);
  });

  test('query rỗng trả toàn bộ, query lạ trả rỗng', () {
    expect(filterVideoLanguages('').length, 184);
    expect(filterVideoLanguages('zzz-khong-ton-tai'), isEmpty);
  });

  test('isAllowedVideoSource chỉ nhận http/https tuyệt đối', () {
    expect(isAllowedVideoSource('https://example.com/video.mp4'), isTrue);
    expect(isAllowedVideoSource('http://example.com/v.mp4'), isTrue);
    expect(isAllowedVideoSource(''), isFalse);
    expect(isAllowedVideoSource('/videos/a.mp4'), isFalse);
    expect(isAllowedVideoSource('example.com/video.mp4'), isFalse);
    expect(isAllowedVideoSource('javascript:alert(1)'), isFalse);
    expect(isAllowedVideoSource('data:video/mp4;base64,AAA'), isFalse);
    expect(isAllowedVideoSource('ftp://example.com/v.mp4'), isFalse);
  });
}
