import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';

import '../../core/auth/auth_provider.dart';
import '../../core/config/app_config.dart';
import '../../core/config/app_config_provider.dart';
import '../../app/ui/app_controls.dart';

/// Màn đổi API URL ngay trên thiết bị, không cần build lại app.
///
/// Giá trị lưu ở đây ghi đè `--dart-define-from-file`. Vì token cấp cho một
/// server không dùng được ở server khác, đổi URL sẽ xoá phiên hiện tại và đưa
/// người dùng về màn đăng nhập.
class ApiEndpointScreen extends ConsumerStatefulWidget {
  const ApiEndpointScreen({super.key});

  @override
  ConsumerState<ApiEndpointScreen> createState() => _ApiEndpointScreenState();
}

class _ApiEndpointScreenState extends ConsumerState<ApiEndpointScreen> {
  late final TextEditingController _controller;
  bool _saving = false;
  bool _testing = false;
  String? _testResult;
  bool _testOk = false;

  @override
  void initState() {
    super.initState();
    _controller = TextEditingController(
      text: ref.read(appConfigProvider).apiBaseUrl,
    );
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    final value = _controller.text.trim();
    if (!AppConfig.isValidBaseUrl(value)) {
      setState(() {
        _testOk = false;
        _testResult = 'URL không hợp lệ. Ví dụ: http://192.168.1.10:5032';
      });
      return;
    }

    setState(() => _saving = true);
    try {
      final changed = await ref
          .read(appConfigProvider.notifier)
          .setApiBaseUrl(value);
      if (!mounted) return;
      if (!changed) {
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(const SnackBar(content: Text('API URL không đổi.')));
        return;
      }
      // Token cũ thuộc server cũ; xoá để tránh request 401 hàng loạt.
      await ref.read(authControllerProvider.notifier).clearLocalSession();
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('Đã chuyển sang ${_controller.text.trim()}')),
      );
      Navigator.of(context).pop(true);
    } catch (error) {
      if (!mounted) return;
      setState(() => _testResult = error.toString());
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _reset() async {
    setState(() => _saving = true);
    try {
      final changed = await ref
          .read(appConfigProvider.notifier)
          .resetApiBaseUrl();
      final envUrl = ref.read(appConfigProvider).apiBaseUrl;
      _controller.text = envUrl;
      if (!mounted) return;
      if (changed) {
        await ref.read(authControllerProvider.notifier).clearLocalSession();
      }
      if (!mounted) return;
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(SnackBar(content: Text('Đã khôi phục mặc định: $envUrl')));
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _testConnection() async {
    final value = _controller.text.trim();
    if (!AppConfig.isValidBaseUrl(value)) {
      setState(() {
        _testOk = false;
        _testResult = 'URL không hợp lệ.';
      });
      return;
    }

    setState(() {
      _testing = true;
      _testResult = null;
    });
    final dio = Dio(
      BaseOptions(
        baseUrl: AppConfig.normalizeBaseUrl(value),
        connectTimeout: const Duration(seconds: 8),
        receiveTimeout: const Duration(seconds: 15),
      ),
    );
    try {
      final response = await dio.get<dynamic>('/health');
      if (!mounted) return;
      setState(() {
        _testOk = response.statusCode == 200;
        _testResult = _testOk
            ? 'Kết nối OK (HTTP ${response.statusCode} · /health)'
            : 'Server phản hồi HTTP ${response.statusCode}.';
      });
    } on DioException catch (error) {
      if (!mounted) return;
      setState(() {
        _testOk = false;
        _testResult = switch (error.type) {
          DioExceptionType.connectionTimeout ||
          DioExceptionType.receiveTimeout =>
            'Hết thời gian kết nối. Kiểm tra IP/port và firewall.',
          DioExceptionType.connectionError =>
            'Không kết nối được. Android emulator dùng 10.0.2.2, thiết bị thật dùng IP LAN của máy dev.',
          _ => 'Lỗi: ${error.message ?? error.type.name}',
        };
      });
    } finally {
      if (mounted) setState(() => _testing = false);
    }
  }

  void _usePreset(String url) {
    setState(() {
      _controller.text = url;
      _testResult = null;
    });
  }

  @override
  Widget build(BuildContext context) {
    final config = ref.watch(appConfigProvider);
    final presets = <({String label, String url})>[
      (
        label: 'Android emulator',
        url: 'http://10.0.2.2:${_portOf(AppConfig.fallbackBaseUrl)}',
      ),
      (
        label: 'iOS simulator',
        url: 'http://localhost:${_portOf(AppConfig.fallbackBaseUrl)}',
      ),
      (label: 'API mặc định (env)', url: config.apiBaseUrl),
    ];

    return Scaffold(
      appBar: AppBar(title: const Text('Cấu hình kết nối')),
      body: SafeArea(
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            Card(
              child: ListTile(
                leading: Icon(
                  Icons.cloud_outlined,
                  color: config.isApiBaseUrlOverridden
                      ? Theme.of(context).colorScheme.primary
                      : null,
                ),
                title: Text(config.apiBaseUrl),
                subtitle: Text(
                  config.isApiBaseUrlOverridden
                      ? 'Đang dùng URL đặt trên thiết bị (ghi đè env)'
                      : 'Đang dùng URL từ env · flavor ${config.flavor}',
                ),
              ),
            ),
            const SizedBox(height: 16),
            // Dùng chung `AppTextField` của design system thay vì tự đặt nhãn +
            // `OutlineInputBorder()` (bán kính mặc định 4px, lệch khỏi 8px của
            // mọi ô nhập khác trong app).
            AppTextField(
              controller: _controller,
              label: 'URL máy chủ API',
              hint: 'http://10.0.2.2:5032',
              keyboardType: TextInputType.url,
              onChanged: (_) {
                if (_testResult != null) setState(() => _testResult = null);
              },
              onSubmitted: (_) => _testConnection(),
            ),
            const SizedBox(height: 8),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final preset in presets)
                  ActionChip(
                    label: Text(preset.label),
                    onPressed: () => _usePreset(preset.url),
                  ),
              ],
            ),
            if (_testResult != null) ...[
              const SizedBox(height: 12),
              Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: _testOk
                      ? AppTheme.success.withValues(alpha: 0.12)
                      : AppTheme.dangerSurface,
                  border: Border.all(
                    color: _testOk
                        ? AppTheme.success.withValues(alpha: 0.4)
                        : AppTheme.dangerBorder,
                  ),
                  borderRadius: BorderRadius.circular(AppTheme.radiusSmall),
                ),
                child: Text(
                  _testResult!,
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: _testOk ? AppTheme.success : AppTheme.dangerText,
                  ),
                ),
              ),
            ],
            const SizedBox(height: 20),
            // Dùng nút của design system: `FButton` trần để nhãn nằm trực tiếp
            // trong hàng nội bộ của Forui nên nhãn dài (và cỡ chữ hệ thống lớn)
            // sẽ tràn ra ngoài khung.
            AppPrimaryButton(
              label: _saving ? 'Đang lưu...' : 'Lưu và đăng nhập lại',
              busy: _saving,
              onPressed: _save,
            ),
            const SizedBox(height: 12),
            AppSecondaryButton(
              label: _testing ? 'Đang kiểm tra...' : 'Kiểm tra kết nối',
              onPressed: _testing ? null : _testConnection,
            ),
            const SizedBox(height: 8),
            TextButton.icon(
              onPressed: _saving ? null : _reset,
              icon: const Icon(Icons.settings_backup_restore),
              label: const Text('Khôi phục URL từ env'),
            ),
            const SizedBox(height: 16),
            Text(
              'Thứ tự ưu tiên: URL lưu trên thiết bị → env build '
              '(--dart-define-from-file=env/<flavor>.json) → mặc định của app.',
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ],
        ),
      ),
    );
  }

  static String _portOf(String url) =>
      Uri.tryParse(url)?.port.toString() ?? '5032';
}
