import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../app/theme.dart';

import 'admin_models.dart';
import 'admin_provider.dart';
import 'admin_repository.dart';
import 'admin_widgets.dart';
import '../../app/ui/app_controls.dart';

class McpServersScreen extends ConsumerWidget {
  const McpServersScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final repository = ref.watch(adminRepositoryProvider);

    Future<void> openDialog(
      BuildContext context,
      McpServerModel? existing,
      McpCatalogEntryModel? prefill,
      Future<void> Function() reload,
    ) async {
      final result = await showAppDialog<Map<String, dynamic>>(
        context,
        builder: (context) =>
            _McpServerDialog(existing: existing, prefill: prefill),
      );
      if (result == null) return;
      try {
        await repository.saveMcpServer(
          id: existing?.id,
          name: result['name'] as String,
          url: result['url'] as String,
          isActive: result['isActive'] as bool,
          trustReadOnlyAnnotations: result['trustReadOnlyAnnotations'] as bool,
          headers: (result['headers'] as Map).cast<String, String>(),
          replaceHeaders: (result['replaceHeaders'] as bool?) ?? false,
          authMode: result['authMode'] as String?,
          oauthClientId: result['oauthClientId'] as String?,
          oauthClientSecret: result['oauthClientSecret'] as String?,
          oauthTokenUrl: result['oauthTokenUrl'] as String?,
          oauthAuthorizeUrl: result['oauthAuthorizeUrl'] as String?,
          oauthScopes: result['oauthScopes'] as String?,
        );
        await reload();
        if (context.mounted) {
          showAdminSnack(
            context,
            existing == null ? 'Đã thêm MCP server.' : 'Đã lưu.',
          );
        }
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
      }
    }

    Future<void> pickFromCatalog(
      BuildContext context,
      Future<void> Function() reload,
    ) async {
      try {
        final entries = await repository.mcpCatalog();
        if (!context.mounted) return;
        final selected = await showAppPicker<McpCatalogEntryModel>(
          context,
          title: 'Thư viện MCP server công khai',
          items: entries,
          labelOf: (entry) => entry.id,
          subtitleOf: (entry) => entry.description,
        );
        if (selected == null || !context.mounted) return;
        await openDialog(context, null, selected, reload);
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
      }
    }

    Future<void> remove(
      BuildContext context,
      McpServerModel server,
      Future<void> Function() reload,
    ) async {
      final confirmed = await confirmAdminAction(
        context,
        title: 'Xoá MCP server',
        message:
            'Xoá "${server.name}" khỏi tenant. Agent sẽ không gọi được server này nữa.',
      );
      if (!confirmed) return;
      try {
        await repository.deleteMcpServer(server.id);
        await reload();
        if (context.mounted) showAdminSnack(context, 'Đã xoá MCP server.');
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
      }
    }

    return AdminListView<McpServerModel>(
      title: 'MCP Servers',
      description:
          'Header và client secret là write-only: để trống khi sửa để giữ nguyên giá trị đang lưu.',
      searchHint: 'Tìm theo tên hoặc URL',
      emptyText: 'Chưa cấu hình MCP server nào.',
      fetch: (search) => repository.mcpServers(search: search),
      extraActions: (context, reload) => [
        AppIconButton(
          icon: Icons.travel_explore,
          tooltip: 'Thư viện MCP',
          onPressed: () => pickFromCatalog(context, reload),
        ),
      ],
      fabBuilder: (context, reload) => FloatingActionButton.extended(
        onPressed: () => openDialog(context, null, null, reload),
        icon: const Icon(Icons.add),
        label: const Text('Thêm server'),
      ),
      itemBuilder: (context, server, reload) => _McpServerCard(
        server: server,
        onEdit: () => openDialog(context, server, null, reload),
        onDelete: () => remove(context, server, reload),
      ),
    );
  }
}

/// Thẻ một MCP server kèm trạng thái kết nối OAuth của riêng người dùng.
///
/// Server cấu hình `AuthorizationCode` cần mỗi người tự cấp quyền: nút Kết nối
/// mở trang cấp quyền của nhà cung cấp trong trình duyệt, sau đó app hỏi lại
/// `GET /api/mcp-oauth/{id}/status` cho tới khi thấy token đã lưu.
class _McpServerCard extends ConsumerStatefulWidget {
  const _McpServerCard({
    required this.server,
    required this.onEdit,
    required this.onDelete,
  });

  final McpServerModel server;
  final VoidCallback onEdit;
  final VoidCallback onDelete;

  @override
  ConsumerState<_McpServerCard> createState() => _McpServerCardState();
}

class _McpServerCardState extends ConsumerState<_McpServerCard> {
  McpOAuthStatusModel? _status;
  bool _busy = false;
  bool _polling = false;
  String? _error;

  bool get _needsOAuth =>
      widget.server.authMode.toLowerCase() == 'authorizationcode';

  @override
  void initState() {
    super.initState();
    if (_needsOAuth) _refreshStatus();
  }

  Future<void> _refreshStatus() async {
    try {
      final status = await ref
          .read(adminRepositoryProvider)
          .mcpOAuthStatus(widget.server.id);
      if (mounted) setState(() => _status = status);
    } catch (error) {
      if (mounted) setState(() => _error = adminErrorMessage(error));
    }
  }

  Future<void> _connect() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final url = await ref
          .read(adminRepositoryProvider)
          .mcpAuthorizeUrl(widget.server.id);
      if (url.isEmpty) throw StateError('Server không trả về URL cấp quyền.');
      final launched = await launchUrl(
        Uri.parse(url),
        mode: LaunchMode.externalApplication,
      );
      if (!launched) throw StateError('Không mở được trình duyệt.');
      if (!mounted) return;
      showAdminSnack(
        context,
        'Hoàn tất cấp quyền trong trình duyệt, sau đó quay lại app.',
      );
      await _pollUntilConnected();
    } catch (error) {
      if (mounted) setState(() => _error = adminErrorMessage(error));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// Hỏi lại trạng thái mỗi 3 giây trong tối đa 2 phút — người dùng cần thời
  /// gian đăng nhập và bấm đồng ý ở trình duyệt.
  Future<void> _pollUntilConnected() async {
    if (_polling) return;
    setState(() => _polling = true);
    try {
      for (var attempt = 0; attempt < 40; attempt++) {
        await Future<void>.delayed(const Duration(seconds: 3));
        if (!mounted) return;
        await _refreshStatus();
        if (_status?.connected == true) {
          if (mounted) showAdminSnack(context, 'Đã kết nối MCP server.');
          return;
        }
        // App bị đẩy xuống nền thì dừng hỏi; lần mở sau sẽ tự kiểm tra lại.
        if (!mounted) return;
      }
    } finally {
      if (mounted) setState(() => _polling = false);
    }
  }

  Future<void> _disconnect() async {
    final confirmed = await confirmAdminAction(
      context,
      title: 'Ngắt kết nối',
      message:
          'Xoá token OAuth của bạn cho "${widget.server.name}"? Agent sẽ không gọi được server này cho tới khi bạn kết nối lại.',
      confirmLabel: 'Ngắt kết nối',
    );
    if (!confirmed) return;
    setState(() => _busy = true);
    try {
      await ref.read(adminRepositoryProvider).disconnectMcp(widget.server.id);
      await _refreshStatus();
      if (mounted) showAdminSnack(context, 'Đã ngắt kết nối.');
    } catch (error) {
      if (mounted) setState(() => _error = adminErrorMessage(error));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final server = widget.server;
    final texts = Theme.of(context).textTheme;
    return AppCard(
      child: Column(
        children: [
          AppTileRaw(
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 14, 8, 14),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Icon(
                    server.isActive
                        ? Icons.cloud_done_outlined
                        : Icons.cloud_off_outlined,
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(server.name, style: texts.titleSmall),
                        const SizedBox(height: 2),
                        Text(
                          '${server.url}\n${server.authMode}'
                          '${server.hasHeaders ? ' · có header' : ''}'
                          '${server.trustReadOnlyAnnotations ? ' · tin read-only' : ''}',
                          style: texts.bodySmall,
                        ),
                      ],
                    ),
                  ),
                  AppMenuButton(
                    tooltip: 'Tuỳ chọn',
                    items: [
                      AppMenuItem('Sửa', widget.onEdit),
                      AppMenuItem('Xoá', widget.onDelete, destructive: true),
                    ],
                  ),
                ],
              ),
            ),
          ),
          if (_needsOAuth)
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 12),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Icon(
                        _status?.connected == true
                            ? Icons.link
                            : Icons.link_off,
                        size: 16,
                        color: _status?.connected == true
                            ? AppTheme.success
                            : AppTheme.warning,
                      ),
                      const SizedBox(width: 6),
                      Expanded(
                        child: Text(
                          _status == null
                              ? 'Đang kiểm tra kết nối OAuth…'
                              : _status!.connected
                              ? 'Đã kết nối OAuth${_status!.expiresAtUtc == null ? '' : ' · hết hạn ${_status!.expiresAtUtc}'}'
                              : 'Chưa kết nối OAuth',
                          style: Theme.of(context).textTheme.bodySmall,
                        ),
                      ),
                      if (_busy || _polling)
                        const SizedBox(
                          width: 16,
                          height: 16,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        ),
                    ],
                  ),
                  if (_error != null)
                    Padding(
                      padding: const EdgeInsets.only(top: 6),
                      child: Text(
                        _error!,
                        style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AppTheme.dangerText,
                        ),
                      ),
                    ),
                  const SizedBox(height: 8),
                  Row(
                    children: [
                      Expanded(
                        child: AppSecondaryButton(
                          label: _status?.connected == true
                              ? 'Kết nối lại'
                              : 'Kết nối',
                          icon: Icons.login,
                          busy: _busy || _polling,
                          onPressed: _connect,
                        ),
                      ),
                      if (_status?.connected == true) ...[
                        const SizedBox(width: 8),
                        Expanded(
                          child: AppDestructiveButton(
                            label: 'Ngắt',
                            icon: Icons.logout,
                            onPressed: _busy ? null : _disconnect,
                          ),
                        ),
                      ],
                    ],
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }
}

class _McpServerDialog extends StatefulWidget {
  const _McpServerDialog({this.existing, this.prefill});

  final McpServerModel? existing;
  final McpCatalogEntryModel? prefill;

  @override
  State<_McpServerDialog> createState() => _McpServerDialogState();
}

class _McpServerDialogState extends State<_McpServerDialog> {
  late final TextEditingController _name;
  late final TextEditingController _url;
  late final TextEditingController _headerName;
  late final TextEditingController _headerValue;
  late final TextEditingController _oauthClientId;
  late final TextEditingController _oauthClientSecret;
  late final TextEditingController _oauthTokenUrl;
  late final TextEditingController _oauthAuthorizeUrl;
  late final TextEditingController _oauthScopes;
  late bool _isActive;
  late bool _trustReadOnly;
  late String _authMode;
  bool _replaceHeaders = false;

  @override
  void initState() {
    super.initState();
    final existing = widget.existing;
    _name = TextEditingController(
      text: existing?.name ?? widget.prefill?.id ?? '',
    );
    _url = TextEditingController(
      text: existing?.url ?? widget.prefill?.url ?? '',
    );
    _headerName = TextEditingController();
    _headerValue = TextEditingController();
    _oauthClientId = TextEditingController(text: existing?.oauthClientId ?? '');
    _oauthClientSecret = TextEditingController();
    _oauthTokenUrl = TextEditingController(text: existing?.oauthTokenUrl ?? '');
    _oauthAuthorizeUrl = TextEditingController(
      text: existing?.oauthAuthorizeUrl ?? '',
    );
    _oauthScopes = TextEditingController(text: existing?.oauthScopes ?? '');
    _isActive = existing?.isActive ?? true;
    _trustReadOnly = existing?.trustReadOnlyAnnotations ?? false;
    _authMode = existing?.authMode ?? 'Static';
  }

  @override
  void dispose() {
    for (final controller in [
      _name,
      _url,
      _headerName,
      _headerValue,
      _oauthClientId,
      _oauthClientSecret,
      _oauthTokenUrl,
      _oauthAuthorizeUrl,
      _oauthScopes,
    ]) {
      controller.dispose();
    }
    super.dispose();
  }

  void _submit() {
    final name = _name.text.trim();
    final url = _url.text.trim();
    if (name.isEmpty || url.isEmpty) {
      showAdminSnack(context, 'Tên và URL là bắt buộc.');
      return;
    }
    final headers = <String, String>{};
    if (_headerName.text.trim().isNotEmpty) {
      headers[_headerName.text.trim()] = _headerValue.text.trim();
    }
    Navigator.pop(context, {
      'name': name,
      'url': url,
      'isActive': _isActive,
      'trustReadOnlyAnnotations': _trustReadOnly,
      'authMode': _authMode,
      'headers': headers,
      'replaceHeaders': _replaceHeaders || headers.isNotEmpty,
      'oauthClientId': _oauthClientId.text.trim(),
      'oauthClientSecret': _oauthClientSecret.text.trim(),
      'oauthTokenUrl': _oauthTokenUrl.text.trim(),
      'oauthAuthorizeUrl': _oauthAuthorizeUrl.text.trim(),
      'oauthScopes': _oauthScopes.text.trim(),
    });
  }

  @override
  Widget build(BuildContext context) => AppDialog(
    title: widget.existing == null ? 'Thêm MCP server' : 'Sửa MCP server',
    content: Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        AdminField(controller: _name, label: 'Tên'),
        AdminField(controller: _url, label: 'URL MCP'),
        AppSwitchTile(
          label: 'Kích hoạt',
          value: _isActive,
          onChanged: (value) => setState(() => _isActive = value),
        ),
        AppSwitchTile(
          label: 'Tin công cụ read-only không cần phê duyệt',
          value: _trustReadOnly,
          onChanged: (value) => setState(() => _trustReadOnly = value),
        ),
        Padding(
          padding: const EdgeInsets.only(top: 8, bottom: 4),
          child: Text(
            widget.existing?.hasHeaders == true
                ? 'Header (đang có header — nhập để thay thế toàn bộ)'
                : 'Header (không bắt buộc)',
            style: const TextStyle(fontWeight: FontWeight.w600),
          ),
        ),
        AdminField(
          controller: _headerName,
          label: 'Tên header',
          hint: 'Authorization',
        ),
        AdminField(controller: _headerValue, label: 'Giá trị header'),
        if (widget.existing?.hasHeaders == true)
          AppCheckTile(
            label: 'Thay thế toàn bộ header đang lưu',
            value: _replaceHeaders,
            onChanged: (value) => setState(() => _replaceHeaders = value),
          ),
        const Divider(),
        // Ô chọn của hệ thiết kế: `DropdownButtonFormField` của Material cần tổ
        // tiên `Material` mà `AppDialog` (Forui) không có — xem `AppSelectTile`.
        AppSelectTile<String>(
          label: 'Kiểu xác thực',
          icon: Icons.lock_outline,
          value: _authMode,
          items: const ['Static', 'ClientCredentials', 'AuthorizationCode'],
          labelOf: (value) => switch (value) {
            'ClientCredentials' => 'OAuth · Client Credentials',
            'AuthorizationCode' => 'OAuth · Authorization Code',
            _ => 'Static (header)',
          },
          onChanged: (value) => setState(() => _authMode = value),
        ),
        const SizedBox(height: 12),
        if (_authMode != 'Static') ...[
          AdminField(controller: _oauthClientId, label: 'OAuth Client ID'),
          AdminField(
            controller: _oauthClientSecret,
            label: 'OAuth Client Secret',
            hint: widget.existing == null
                ? null
                : 'Để trống để giữ secret đang lưu',
            obscure: true,
          ),
          AdminField(controller: _oauthTokenUrl, label: 'Token URL'),
          if (_authMode == 'AuthorizationCode')
            AdminField(controller: _oauthAuthorizeUrl, label: 'Authorize URL'),
          AdminField(controller: _oauthScopes, label: 'Scopes'),
        ],
      ],
    ),
    actions: [
      AppSecondaryButton(label: 'Huỷ', onPressed: () => Navigator.pop(context)),
      const SizedBox(width: 10),
      AppPrimaryButton(label: 'Lưu', onPressed: _submit, expand: false),
    ],
  );
}
