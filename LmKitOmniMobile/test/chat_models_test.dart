import 'package:flutter_test/flutter_test.dart';
import 'package:lmkit_omni_mobile/core/config/app_config.dart';
import 'package:lmkit_omni_mobile/features/chat/chat_models.dart';

ChatAttachmentModel _attachment(String name, int size) =>
    ChatAttachmentModel(path: '/tmp/$name', name: name, size: size);

void main() {
  group('ChatAttachmentModel', () {
    test('nhận các phần mở rộng backend cho phép', () {
      expect(_attachment('bao-cao.pdf', 1024).isSupported, isTrue);
      expect(_attachment('anh.PNG', 1024).isSupported, isTrue);
      expect(_attachment('du-lieu.csv', 1024).isSupported, isTrue);
    });

    test('từ chối định dạng ngoài danh sách', () {
      expect(_attachment('script.exe', 1024).isSupported, isFalse);
      expect(_attachment('khong-co-duoi', 1024).isSupported, isFalse);
    });

    test('chặn file quá 20 MB', () {
      final oversize = _attachment('bao-cao.pdf', 21 * 1024 * 1024);
      expect(
        ChatAttachmentModel.validateAdd(const [], oversize),
        contains('vượt quá 20 MB'),
      );
    });

    test('chặn khi vượt quá 8 file', () {
      final current = [
        for (var i = 0; i < ChatAttachmentModel.maxFiles; i++)
          _attachment('file-$i.txt', 10),
      ];
      expect(
        ChatAttachmentModel.validateAdd(current, _attachment('them.txt', 10)),
        contains('tối đa'),
      );
    });

    test('chặn khi tổng dung lượng vượt 50 MB', () {
      final current = [
        _attachment('a.pdf', 20 * 1024 * 1024),
        _attachment('b.pdf', 20 * 1024 * 1024),
      ];
      expect(
        ChatAttachmentModel.validateAdd(
          current,
          _attachment('c.pdf', 15 * 1024 * 1024),
        ),
        contains('50 MB'),
      );
    });

    test('file hợp lệ không trả lỗi và hiển thị dung lượng', () {
      final file = _attachment('anh.png', 2 * 1024 * 1024);
      expect(ChatAttachmentModel.validateAdd(const [], file), isNull);
      expect(file.displaySize, '2.0 MB');
    });
  });

  group('ShareLinkModel', () {
    test('đọc token và hạn từ API', () {
      final link = ShareLinkModel.fromJson({
        'token': 'abc123',
        'expiresAtUtc': '2026-12-31T00:00:00Z',
      });
      expect(link.token, 'abc123');
      expect(link.isExpired, isFalse);
    });

    test('coi link đã qua hạn là hết hiệu lực', () {
      final link = ShareLinkModel.fromJson({
        'token': 'cu',
        'expiresAtUtc': '2020-01-01T00:00:00Z',
      });
      expect(link.isExpired, isTrue);
    });
  });

  test('AppConfig dựng link share từ web base url', () {
    final config = AppConfig.fromEnvironment().copyWith(
      webBaseUrl: 'https://app.example.com',
    );
    expect(
      config.shareUrlFor('token-1'),
      'https://app.example.com/share/token-1',
    );
  });
}
