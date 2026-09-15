import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';

import '../../core/config/app_config_provider.dart';
import 'admin_provider.dart';
import 'admin_repository.dart';
import 'admin_widgets.dart';
import '../../app/ui/app_controls.dart';

class WidgetSettingsScreen extends ConsumerStatefulWidget {
  const WidgetSettingsScreen({super.key});

  @override
  ConsumerState<WidgetSettingsScreen> createState() =>
      _WidgetSettingsScreenState();
}

class _WidgetSettingsScreenState extends ConsumerState<WidgetSettingsScreen> {
  final _origins = TextEditingController();
  final _perMinute = TextEditingController();
  final _perDay = TextEditingController();
  final _title = TextEditingController();
  final _welcome = TextEditingController();
  final _brandColor = TextEditingController();
  final _logoUrl = TextEditingController();

  bool _loading = true;
  bool _saving = false;
  bool _rotating = false;
  bool _isActive = false;
  String _position = 'bottom-right';
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    for (final controller in [
      _origins,
      _perMinute,
      _perDay,
      _title,
      _welcome,
      _brandColor,
      _logoUrl,
    ]) {
      controller.dispose();
    }
    super.dispose();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final settings = await ref.read(adminRepositoryProvider).widgetSettings();
      if (!mounted) return;
      setState(() {
        _isActive = settings.isActive;
        _origins.text = settings.allowedOrigins.join('\n');
        _perMinute.text = settings.requestsPerMinute.toString();
        _perDay.text = settings.requestsPerDay.toString();
        _title.text = settings.widgetTitle ?? '';
        _welcome.text = settings.welcomeMessage ?? '';
        _brandColor.text = settings.brandColor ?? '';
        _logoUrl.text = settings.logoUrl ?? '';
        _position = settings.position;
      });
    } catch (error) {
      if (mounted) setState(() => _error = adminErrorMessage(error));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  List<String> get _originList => _origins.text
      .split('\n')
      .map((line) => line.trim())
      .where((line) => line.isNotEmpty)
      .toList();

  Future<void> _save() async {
    if (_originList.length > 20) {
      showAdminSnack(context, 'Tối đa 20 origin được phép.');
      return;
    }
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      await ref
          .read(adminRepositoryProvider)
          .updateWidgetSettings(
            isActive: _isActive,
            allowedOrigins: _originList,
            requestsPerMinute: int.tryParse(_perMinute.text.trim()) ?? 0,
            requestsPerDay: int.tryParse(_perDay.text.trim()) ?? 0,
            widgetTitle: _title.text.trim(),
            welcomeMessage: _welcome.text.trim(),
            brandColor: _brandColor.text.trim(),
            logoUrl: _logoUrl.text.trim(),
            position: _position,
          );
      if (mounted) showAdminSnack(context, 'Đã lưu cấu hình widget.');
    } catch (error) {
      if (mounted) setState(() => _error = adminErrorMessage(error));
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _rotate() async {
    final confirmed = await confirmAdminAction(
      context,
      title: 'Tạo khóa widget mới',
      message:
          'Khóa cũ sẽ ngừng hoạt động ngay. Widget đang nhúng bằng khóa cũ sẽ không '
          'gọi được API nữa.',
      confirmLabel: 'Tạo khóa mới',
    );
    if (!confirmed) return;

    setState(() {
      _rotating = true;
      _error = null;
    });
    try {
      final rawKey = await ref.read(adminRepositoryProvider).rotateWidgetKey();
      if (!mounted) return;
      await showDialog<void>(
        context: context,
        builder: (context) => AlertDialog(
          title: const Text('Khóa widget mới'),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Text(
                'Khóa chỉ hiển thị MỘT lần. Sao chép và lưu ngay.',
                style: TextStyle(color: AppTheme.warning),
              ),
              const SizedBox(height: 12),
              SelectableText(rawKey),
            ],
          ),
          actions: [
            TextButton.icon(
              onPressed: () async {
                await Clipboard.setData(ClipboardData(text: rawKey));
                if (context.mounted) {
                  Navigator.pop(context);
                  showAdminSnack(context, 'Đã sao chép khóa widget.');
                }
              },
              icon: const Icon(Icons.copy, size: 18),
              label: const Text('Sao chép'),
            ),
            AppPrimaryButton(
              label: 'Đã lưu',
              onPressed: () => Navigator.pop(context),
              expand: false,
            ),
          ],
        ),
      );
    } catch (error) {
      if (mounted) setState(() => _error = adminErrorMessage(error));
    } finally {
      if (mounted) setState(() => _rotating = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final webBaseUrl = ref.watch(appConfigProvider).webBaseUrl;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Widget Settings'),
        actions: [
          IconButton(
            tooltip: 'Làm mới',
            onPressed: _load,
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: _loading
          ? const Center(child: CircularProgressIndicator())
          : ListView(
              padding: const EdgeInsets.all(16),
              children: [
                if (_error != null)
                  AdminBanner(message: _error!, isError: true, onRetry: _load),
                Card(
                  child: Padding(
                    padding: const EdgeInsets.all(16),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        SwitchListTile(
                          contentPadding: EdgeInsets.zero,
                          value: _isActive,
                          onChanged: (value) =>
                              setState(() => _isActive = value),
                          title: const Text('Bật widget công khai'),
                          subtitle: const Text(
                            'Khi bật, trang web có khóa và origin khớp allowlist '
                            'đều có thể chat bằng tài nguyên AI của tenant.',
                          ),
                        ),
                        const Divider(),
                        AdminField(
                          controller: _origins,
                          label: 'Origin được phép nhúng',
                          hint: 'https://example.com',
                          maxLines: 4,
                        ),
                        AdminField(
                          controller: _perMinute,
                          label: 'Giới hạn / phút',
                          hint: '0 = mặc định hệ thống (60)',
                          keyboardType: TextInputType.number,
                        ),
                        AdminField(
                          controller: _perDay,
                          label: 'Giới hạn / ngày',
                          hint: '0 = mặc định hệ thống (10.000)',
                          keyboardType: TextInputType.number,
                        ),
                        AdminField(controller: _title, label: 'Tiêu đề widget'),
                        AdminField(
                          controller: _welcome,
                          label: 'Lời chào',
                          maxLines: 2,
                        ),
                        AdminField(
                          controller: _brandColor,
                          label: 'Màu thương hiệu (#RRGGBB)',
                          hint: '#2563eb',
                        ),
                        AdminField(controller: _logoUrl, label: 'Logo URL'),
                        DropdownButtonFormField<String>(
                          initialValue: _position,
                          isExpanded: true,
                          decoration: const InputDecoration(
                            labelText: 'Vị trí hiển thị',
                          ),
                          items: const [
                            DropdownMenuItem(
                              value: 'bottom-right',
                              child: Text('Góc phải dưới'),
                            ),
                            DropdownMenuItem(
                              value: 'bottom-left',
                              child: Text('Góc trái dưới'),
                            ),
                          ],
                          onChanged: (value) => setState(
                            () => _position = value ?? 'bottom-right',
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
                const SizedBox(height: 16),
                Row(
                  children: [
                    Expanded(
                      child: AppPrimaryButton(
                        label: _saving ? 'Đang lưu...' : 'Lưu cấu hình',
                        icon: Icons.save_outlined,
                        onPressed: _saving ? null : _save,
                        expand: false,
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: AppSecondaryButton(
                        label: _rotating ? 'Đang tạo...' : 'Tạo khóa mới',
                        icon: Icons.key_outlined,
                        onPressed: _rotating ? null : _rotate,
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 16),
                if (_isActive)
                  Card(
                    child: Padding(
                      padding: const EdgeInsets.all(16),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            'Mã nhúng',
                            style: Theme.of(context).textTheme.titleMedium,
                          ),
                          const SizedBox(height: 8),
                          SelectableText(
                            '<iframe src="$webBaseUrl/widget/chat?key=<WIDGET_KEY>"\n'
                            '  width="380" height="560" frameborder="0"></iframe>',
                            style: const TextStyle(
                              fontFamily: AppTheme.monoFamily,
                              fontFamilyFallback: AppTheme.monoFallback,
                              fontSize: 12,
                              height: 1.45,
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
              ],
            ),
    );
  }
}
