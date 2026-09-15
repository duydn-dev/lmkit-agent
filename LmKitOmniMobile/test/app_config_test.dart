import 'package:flutter_test/flutter_test.dart';
import 'package:lmkit_omni_mobile/core/config/app_config.dart';

void main() {
  group('AppConfig.normalizeBaseUrl', () {
    test('bỏ khoảng trắng và dấu / cuối', () {
      expect(
        AppConfig.normalizeBaseUrl('  http://10.0.2.2:5032/  '),
        'http://10.0.2.2:5032',
      );
      expect(
        AppConfig.normalizeBaseUrl('https://api.example.com///'),
        'https://api.example.com',
      );
    });

    test('giữ nguyên path khi URL có path', () {
      expect(
        AppConfig.normalizeBaseUrl('https://host/gateway/'),
        'https://host/gateway',
      );
    });

    test('chuỗi rỗng quay về fallback theo nền tảng', () {
      expect(AppConfig.normalizeBaseUrl('   '), AppConfig.fallbackBaseUrl);
    });
  });

  group('AppConfig.isValidBaseUrl', () {
    test('chấp nhận http và https có host', () {
      expect(AppConfig.isValidBaseUrl('http://localhost:5032'), isTrue);
      expect(AppConfig.isValidBaseUrl('https://api.example.com'), isTrue);
    });

    test('từ chối URL thiếu scheme hoặc scheme lạ', () {
      expect(AppConfig.isValidBaseUrl('localhost:5032'), isFalse);
      expect(AppConfig.isValidBaseUrl('10.0.2.2:5032'), isFalse);
      expect(AppConfig.isValidBaseUrl('ftp://api.example.com'), isFalse);
      expect(AppConfig.isValidBaseUrl(''), isFalse);
    });
  });

  test('copyWith đổi API URL và đánh dấu đã override', () {
    final base = AppConfig.fromEnvironment();
    final changed = base.copyWith(
      apiBaseUrl: 'http://192.168.1.10:5032',
      isApiBaseUrlOverridden: true,
    );

    expect(changed.apiBaseUrl, 'http://192.168.1.10:5032');
    expect(changed.isApiBaseUrlOverridden, isTrue);
    // Cấu hình gốc không bị thay đổi.
    expect(base.isApiBaseUrlOverridden, isFalse);
    expect(changed.receiveTimeout, base.receiveTimeout);
  });

  test('receiveTimeout đủ dài cho SSE chat', () {
    expect(
      AppConfig.fromEnvironment().receiveTimeout.inSeconds,
      greaterThanOrEqualTo(60),
    );
  });

  group('URL LiveKit cho phòng thoại', () {
    test('suy ra từ host của API với cổng 7880', () {
      expect(
        AppConfig.defaultLiveKitUrlFor('http://10.0.2.2:5032'),
        'ws://10.0.2.2:7880',
      );
      expect(
        AppConfig.defaultLiveKitUrlFor('https://api.example.com'),
        'wss://api.example.com:7880',
      );
      expect(
        AppConfig.defaultLiveKitUrlFor('https://host/gateway'),
        'wss://host:7880',
      );
    });

    test('đổi API URL trong app thì phòng thoại đi theo', () {
      final base = AppConfig.fromEnvironment();
      final moved = base.copyWith(apiBaseUrl: 'https://staging.example.com');

      expect(base.livekitUrl, isNot(moved.livekitUrl));
      expect(moved.livekitUrl, 'wss://staging.example.com:7880');
    });

    test('LIVEKIT_URL được chỉ định thì dùng luôn và bỏ dấu / cuối', () {
      final config = AppConfig.fromEnvironment().copyWith(
        livekitUrlOverride: 'wss://livekit.example.com/',
      );

      expect(config.livekitUrl, 'wss://livekit.example.com');
    });

    test('chuẩn hoá http(s) sang ws(s)', () {
      expect(
        AppConfig.normalizeLiveKitUrl('https://livekit.example.com'),
        'wss://livekit.example.com',
      );
      expect(
        AppConfig.normalizeLiveKitUrl('http://10.0.2.2:7880/'),
        'ws://10.0.2.2:7880',
      );
      expect(AppConfig.normalizeLiveKitUrl('   '), 'ws://localhost:7880');
    });
  });
}
