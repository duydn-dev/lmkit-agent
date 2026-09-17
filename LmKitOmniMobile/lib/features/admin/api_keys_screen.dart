import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';

import '../../app/ui/app_controls.dart';
import '../studio/studio_provider.dart';
import 'admin_widgets.dart';

/// Một API key của người dùng hiện tại.
class ApiKeyModel {
  const ApiKeyModel({
    required this.id,
    required this.name,
    this.maxRequests = 0,
    this.usedRequests = 0,
    this.expiresAtUtc,
    this.createdAtUtc,
    this.isActive = true,
  });

  final String id;
  final String name;

  /// 0 = không giới hạn.
  final int maxRequests;
  final int usedRequests;
  final DateTime? expiresAtUtc;
  final DateTime? createdAtUtc;
  final bool isActive;

  String get usageLabel => maxRequests == 0
      ? 'Đã dùng $usedRequests (không giới hạn)'
      : 'Đã dùng $usedRequests / $maxRequests';

  factory ApiKeyModel.fromJson(Map<String, dynamic> json) => ApiKeyModel(
    id: json['id']?.toString() ?? '',
    name: json['name'] as String? ?? '',
    maxRequests: (json['maxRequests'] as num?)?.toInt() ?? 0,
    usedRequests: (json['usedRequests'] as num?)?.toInt() ?? 0,
    expiresAtUtc: DateTime.tryParse(json['expiresAtUtc']?.toString() ?? ''),
    createdAtUtc: DateTime.tryParse(json['createdAtUtc']?.toString() ?? ''),
    isActive: json['isActive'] as bool? ?? true,
  );
}

/// Quản lý API key cá nhân: tạo (kèm hạn dùng và hạn mức), thu hồi.
///
/// Tương ứng `ApiKeysView.vue`. `rawKey` chỉ xuất hiện trong response tạo mới —
/// backend chỉ lưu SHA-256 nên không thể hiển thị lại lần thứ hai.
class ApiKeysScreen extends ConsumerWidget {
  const ApiKeysScreen({super.key});

  @override
  Widget build(
    BuildContext context,
    WidgetRef ref,
  ) => AdminListView<ApiKeyModel>(
    title: 'API Keys',
    description:
        'Khóa truy cập API cho ứng dụng tích hợp. Raw key chỉ hiển thị đúng một '
        'lần ngay sau khi tạo, hệ thống chỉ lưu bản băm.',
    emptyText: 'Chưa có API key nào.',
    fetch: (search) => ref
        .read(studioRepositoryProvider)
        .apiKeys(search: search)
        .then((rows) => rows.map(ApiKeyModel.fromJson).toList()),
    fabBuilder: (context, reload) => FloatingActionButton(
      tooltip: 'Tạo API key',
      onPressed: () => _create(context, ref, reload),
      child: const Icon(Icons.add),
    ),
    itemBuilder: (context, key, reload) =>
        _ApiKeyCard(apiKey: key, reload: reload),
  );

  static Future<void> _create(
    BuildContext context,
    WidgetRef ref,
    Future<void> Function() reload,
  ) async {
    final created = await showAppDialog<Map<String, dynamic>>(
      context,
      builder: (_) => const _CreateApiKeyDialog(),
    );
    if (created == null) return;
    if (!context.mounted) return;
    await _showRawKey(context, created);
    await reload();
  }

  /// Hiển thị raw key một lần kèm nút sao chép và cảnh báo rõ ràng.
  static Future<void> _showRawKey(
    BuildContext context,
    Map<String, dynamic> created,
  ) async {
    final rawKey = created['rawKey']?.toString() ?? '';
    final name = created['name']?.toString() ?? '';
    var copied = false;
    await showAppDialog<void>(
      context,
      barrierDismissible: false,
      builder: (dialogContext) => StatefulBuilder(
        builder: (dialogContext, setDialogState) => AppDialog(
          title: 'Lưu khóa ngay bây giờ',
          content: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              AppAlert(
                message:
                    'API key "$name" đã được tạo. Sau khi đóng bảng này, hệ '
                    'thống không thể hiển thị lại khóa.',
                isError: true,
              ),
              SelectableText(
                rawKey,
                style: const TextStyle(fontFamily: 'monospace'),
              ),
              const SizedBox(height: 12),
              Row(
                children: [
                  AppSecondaryButton(
                    label: copied ? 'Đã sao chép' : 'Sao chép',
                    icon: copied ? Icons.check : Icons.copy,
                    onPressed: () async {
                      await Clipboard.setData(ClipboardData(text: rawKey));
                      setDialogState(() => copied = true);
                    },
                  ),
                ],
              ),
            ],
          ),
          actions: [
            AppPrimaryButton(
              label: 'Tôi đã lưu khóa',
              expand: false,
              onPressed: () => Navigator.pop(dialogContext),
            ),
          ],
        ),
      ),
    );
  }
}

class _ApiKeyCard extends ConsumerStatefulWidget {
  const _ApiKeyCard({required this.apiKey, required this.reload});

  final ApiKeyModel apiKey;
  final Future<void> Function() reload;

  @override
  ConsumerState<_ApiKeyCard> createState() => _ApiKeyCardState();
}

class _ApiKeyCardState extends ConsumerState<_ApiKeyCard> {
  bool _busy = false;

  Future<void> _revoke() async {
    final confirmed = await confirmAdminAction(
      context,
      title: 'Thu hồi API key',
      message:
          'Thu hồi API key "${widget.apiKey.name}"? Ứng dụng đang dùng khóa này '
          'sẽ mất quyền truy cập ngay lập tức.',
      confirmLabel: 'Thu hồi',
    );
    if (!confirmed) return;
    setState(() => _busy = true);
    try {
      await ref.read(studioRepositoryProvider).revokeApiKey(widget.apiKey.id);
      await widget.reload();
    } catch (error) {
      if (mounted) showAdminSnack(context, error.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final key = widget.apiKey;
    final expired =
        key.expiresAtUtc != null &&
        key.expiresAtUtc!.isBefore(DateTime.now().toUtc());
    return AppCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(key.name, style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 4),
          Text(key.usageLabel, style: Theme.of(context).textTheme.bodySmall),
          if (key.expiresAtUtc != null)
            Text(
              expired
                  ? 'Đã hết hạn ${_date(key.expiresAtUtc!)}'
                  : 'Hết hạn ${_date(key.expiresAtUtc!)}',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                color: expired ? AppTheme.govRed : null,
              ),
            ),
          if (key.createdAtUtc != null)
            Text(
              'Tạo ${_date(key.createdAtUtc!)}',
              style: Theme.of(context).textTheme.bodySmall,
            ),
          const SizedBox(height: 8),
          Row(
            children: [
              if (!key.isActive)
                Padding(
                  padding: const EdgeInsets.only(right: 8),
                  child: Text(
                    'Đã thu hồi / hết hạn',
                    style: Theme.of(context).textTheme.bodySmall,
                  ),
                ),
              Expanded(
                child: AppDestructiveButton(
                  label: 'Thu hồi',
                  icon: Icons.delete_outline,
                  onPressed: _busy ? null : _revoke,
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }

  static String _date(DateTime value) {
    final local = value.toLocal();
    String two(int number) => number.toString().padLeft(2, '0');
    return '${two(local.day)}/${two(local.month)}/${local.year}';
  }
}

class _CreateApiKeyDialog extends ConsumerStatefulWidget {
  const _CreateApiKeyDialog();

  @override
  ConsumerState<_CreateApiKeyDialog> createState() =>
      _CreateApiKeyDialogState();
}

class _CreateApiKeyDialogState extends ConsumerState<_CreateApiKeyDialog> {
  final _name = TextEditingController(text: 'Tích hợp CRM');
  final _expires = TextEditingController(text: '90');
  final _maxRequests = TextEditingController(text: '0');
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _name.dispose();
    _expires.dispose();
    _maxRequests.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    final name = _name.text.trim();
    if (name.isEmpty) {
      setState(() => _error = 'Vui lòng nhập tên khóa.');
      return;
    }
    if (name.length > 64) {
      setState(() => _error = 'Tên khóa tối đa 64 ký tự.');
      return;
    }
    final expires = int.tryParse(_expires.text.trim());
    if (expires == null || expires < 1 || expires > 365) {
      setState(() => _error = 'Hạn dùng phải từ 1 đến 365 ngày.');
      return;
    }
    final maxRequests = int.tryParse(_maxRequests.text.trim()) ?? 0;
    if (maxRequests < 0 || maxRequests > 1000000) {
      setState(() => _error = 'Hạn mức từ 0 (không giới hạn) đến 1.000.000.');
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final created = await ref
          .read(studioRepositoryProvider)
          .createApiKey(
            name: name,
            expiresInDays: expires,
            maxRequests: maxRequests,
          );
      if (mounted) Navigator.pop(context, created);
    } catch (error) {
      if (mounted) {
        setState(() {
          _busy = false;
          _error = error.toString();
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) => AppDialog(
    title: 'Tạo API key',
    content: Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (_error != null) AppAlert(message: _error!, isError: true),
        AdminField(controller: _name, label: 'Tên khóa'),
        AdminField(
          controller: _expires,
          label: 'Hạn dùng (ngày)',
          hint: '1 – 365, mặc định 90',
          keyboardType: TextInputType.number,
        ),
        AdminField(
          controller: _maxRequests,
          label: 'Hạn mức request',
          hint: '0 = không giới hạn',
          keyboardType: TextInputType.number,
        ),
        Text(
          'Mỗi người dùng tối đa 5 khóa còn hiệu lực.',
          style: Theme.of(context).textTheme.bodySmall,
        ),
      ],
    ),
    actions: [
      AppSecondaryButton(
        label: 'Huỷ',
        onPressed: _busy ? null : () => Navigator.pop(context),
      ),
      const SizedBox(width: 10),
      AppPrimaryButton(
        label: 'Tạo khóa',
        busy: _busy,
        expand: false,
        onPressed: _submit,
      ),
    ],
  );
}
