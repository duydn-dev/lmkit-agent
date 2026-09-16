import 'dart:convert';
import 'dart:io';

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

  test('copyWith đổi API URL mà không sửa cấu hình gốc', () {
    final base = AppConfig.fromEnvironment();
    final changed = base.copyWith(apiBaseUrl: 'http://192.168.1.10:5032');

    expect(changed.apiBaseUrl, 'http://192.168.1.10:5032');
    expect(base.apiBaseUrl, isNot(changed.apiBaseUrl));
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

    test('đổi API URL giữa các môi trường thì phòng thoại đi theo', () {
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

  group('file env trong repo', () {
    Map<String, dynamic> readEnv(String name) {
      final file = File('env/$name');
      expect(file.existsSync(), isTrue, reason: 'thiếu env/$name');
      return jsonDecode(file.readAsStringSync()) as Map<String, dynamic>;
    }

    test('không truyền define nào thì app gọi mạng thật', () {
      expect(AppConfig.fromEnvironment().useMockData, isFalse);
    });

    test('mọi env dùng để phát hành đều tắt dữ liệu mẫu', () {
      for (final name in ['dev.json', 'dev-ios.json', 'prod.json']) {
        final env = readEnv(name);
        expect(
          env['USE_MOCK_DATA'],
          anyOf(isNull, isFalse),
          reason: '$name không được phục vụ dữ liệu mẫu',
        );
        expect((env['API_BASE_URL'] as String).trim(), isNotEmpty);
      }
    });

    test('chỉ file *-mock.json mới bật dữ liệu mẫu', () {
      final withMock = Directory('env')
          .listSync()
          .whereType<File>()
          .where((f) => f.path.endsWith('.json'))
          .where(
            (f) =>
                (jsonDecode(f.readAsStringSync()) as Map<String, dynamic>)
                        ['USE_MOCK_DATA'] ==
                    true,
          )
          .map((f) => f.uri.pathSegments.last)
          .toList()
        ..sort();

      expect(withMock, ['dev-mock.json', 'web-mock.json']);
    });

    test('dev.json trỏ localhost của máy dev qua 10.0.2.2, đúng cổng API', () {
      expect(readEnv('dev.json')['USE_MOCK_DATA'], anyOf(isNull, isFalse));

      final base = Uri.parse(readEnv('dev.json')['API_BASE_URL'] as String);
      expect(base.host, '10.0.2.2', reason: 'emulator không vào được localhost');
      expect(base.scheme, 'http');

      // Cổng phải khớp cổng API thật khai trong launchSettings để hai bên không
      // lệch nhau khi backend đổi cổng. Repo xếp khác đi thì bỏ qua phép so này.
      final launch = File('../LmKitOmniApi/Properties/launchSettings.json');
      if (!launch.existsSync()) return;
      final profiles =
          (jsonDecode(
                    launch.readAsStringSync().replaceFirst('\ufeff', ''),
                  )
                  as Map<String, dynamic>)['profiles']
              as Map<String, dynamic>;
      final applicationUrl =
          (profiles['http'] as Map<String, dynamic>)['applicationUrl'] as String;

      expect(base.port, Uri.parse(applicationUrl).port);
    });
  });
}
