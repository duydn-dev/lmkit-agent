import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'admin_models.dart';
import 'admin_provider.dart';
import 'admin_repository.dart';
import 'admin_widgets.dart';
import '../../app/ui/app_controls.dart';

/// Giá trị phải khớp enum `DbProvider` của backend.
const _providers = <({String label, String value, String sample})>[
  (
    label: 'PostgreSQL',
    value: 'Postgres',
    sample: 'Host=...;Port=5432;Database=...;Username=...;Password=...',
  ),
  (
    label: 'SQLite',
    value: 'Sqlite',
    sample: 'Data Source=/path/to/database.db',
  ),
  (
    label: 'MySQL / MariaDB',
    value: 'MySql',
    sample: 'Server=...;Port=3306;Database=...;User ID=...;Password=...',
  ),
  (
    label: 'SQL Server',
    value: 'SqlServer',
    sample:
        'Server=...,1433;Database=...;User ID=...;Password=...;Encrypt=True',
  ),
  (
    label: 'Oracle',
    value: 'Oracle',
    sample: 'Data Source=host:1521/service;User ID=...;Password=...',
  ),
  (
    label: 'MongoDB',
    value: 'Mongo',
    sample: 'mongodb://user:pass@host:27017/database',
  ),
];

class DatabaseConnectionsScreen extends ConsumerWidget {
  const DatabaseConnectionsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final repository = ref.watch(adminRepositoryProvider);

    Future<void> openDialog(
      BuildContext context,
      DatabaseConnectionModel? existing,
      Future<void> Function() reload,
    ) async {
      List<TenantOptionModel> tenants = const [];
      try {
        tenants = await repository.tenantOptions();
      } catch (_) {
        // Không có danh sách tenant vẫn dùng được kết nối toàn hệ thống.
      }
      if (!context.mounted) return;

      final result = await showDialog<Map<String, dynamic>>(
        context: context,
        builder: (context) =>
            _DatabaseConnectionDialog(existing: existing, tenants: tenants),
      );
      if (result == null) return;

      try {
        await repository.saveDatabaseConnection(
          id: existing?.id,
          name: result['name'] as String,
          provider: result['provider'] as String,
          connectionString: result['connectionString'] as String?,
          isActive: result['isActive'] as bool,
          allowWrites: result['allowWrites'] as bool,
          isGlobal: result['isGlobal'] as bool,
          tenantId: result['tenantId'] as String?,
        );
        await reload();
        if (context.mounted) {
          showAdminSnack(
            context,
            existing == null ? 'Đã thêm kết nối.' : 'Đã lưu.',
          );
        }
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
      }
    }

    Future<void> remove(
      BuildContext context,
      DatabaseConnectionModel connection,
      Future<void> Function() reload,
    ) async {
      final confirmed = await confirmAdminAction(
        context,
        title: 'Xoá kết nối',
        message: 'Xoá "${connection.name}" và dữ liệu chỉ mục của nó?',
      );
      if (!confirmed || !context.mounted) return;
      try {
        await repository.deleteDatabaseConnection(connection.id);
        await reload();
        if (context.mounted) showAdminSnack(context, 'Đã xoá kết nối.');
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
      }
    }

    Future<void> run(
      BuildContext context,
      Future<String> Function() action, {
      required Future<void> Function() reload,
      bool refreshAfter = false,
    }) async {
      try {
        final message = await action();
        if (refreshAfter) await reload();
        if (context.mounted) showAdminSnack(context, message);
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
      }
    }

    return AdminListView<DatabaseConnectionModel>(
      title: 'Database Connections',
      description:
          'Connection string là write-only: để trống khi sửa để giữ nguyên giá trị đang lưu.',
      searchHint: 'Tìm theo tên hoặc loại CSDL',
      emptyText: 'Chưa có kết nối cơ sở dữ liệu nào.',
      fetch: (search) => repository.databaseConnections(search: search),
      fabBuilder: (context, reload) => FloatingActionButton.extended(
        onPressed: () => openDialog(context, null, reload),
        icon: const Icon(Icons.add),
        label: const Text('Thêm kết nối'),
      ),
      itemBuilder: (context, connection, reload) => Card(
        child: ListTile(
          isThreeLine: true,
          leading: Icon(
            connection.isActive ? Icons.storage : Icons.storage_outlined,
          ),
          title: Text(connection.name),
          subtitle: Text(
            '${_providerLabel(connection.provider)} · '
            '${connection.isGlobal ? 'Toàn hệ thống' : connection.tenantName ?? 'Theo tenant'}\n'
            '${_indexLabel(connection)}'
            '${connection.allowWrites ? ' · cho phép ghi' : ''}',
          ),
          trailing: PopupMenuButton<String>(
            tooltip: 'Tuỳ chọn',
            onSelected: (value) async {
              switch (value) {
                case 'edit':
                  await openDialog(context, connection, reload);
                case 'test':
                  await run(
                    context,
                    () => repository.testDatabaseConnection(connection.id),
                    reload: reload,
                  );
                case 'reindex':
                  await run(
                    context,
                    () async {
                      await repository.reindexDatabaseConnection(connection.id);
                      return 'Đã xếp hàng đánh lại chỉ mục.';
                    },
                    reload: reload,
                    refreshAfter: true,
                  );
                case 'delete':
                  await remove(context, connection, reload);
              }
            },
            itemBuilder: (context) => const [
              PopupMenuItem(value: 'edit', child: Text('Sửa')),
              PopupMenuItem(value: 'test', child: Text('Kiểm tra kết nối')),
              PopupMenuItem(value: 'reindex', child: Text('Đánh lại chỉ mục')),
              PopupMenuItem(value: 'delete', child: Text('Xoá')),
            ],
          ),
        ),
      ),
    );
  }

  static String _providerLabel(String value) =>
      _providers
          .where((item) => item.value.toLowerCase() == value.toLowerCase())
          .map((item) => item.label)
          .firstOrNull ??
      (value.isEmpty ? 'Không rõ' : value);

  static String _indexLabel(DatabaseConnectionModel connection) {
    if (connection.lastIndexError?.isNotEmpty == true) {
      return 'Chỉ mục lỗi: ${connection.lastIndexError}';
    }
    return switch (connection.indexStatus) {
      'Pending' => 'Chờ đánh chỉ mục',
      'Indexing' => 'Đang đánh chỉ mục',
      'Completed' => 'Đã đánh chỉ mục',
      'Failed' => 'Đánh chỉ mục thất bại',
      _ => connection.isIndexed ? 'Đã đánh chỉ mục' : 'Chưa đánh chỉ mục',
    };
  }
}

class _DatabaseConnectionDialog extends StatefulWidget {
  const _DatabaseConnectionDialog({this.existing, required this.tenants});

  final DatabaseConnectionModel? existing;
  final List<TenantOptionModel> tenants;

  @override
  State<_DatabaseConnectionDialog> createState() =>
      _DatabaseConnectionDialogState();
}

class _DatabaseConnectionDialogState extends State<_DatabaseConnectionDialog> {
  late final TextEditingController _name;
  late final TextEditingController _connectionString;
  late String _provider;
  late bool _isActive;
  late bool _allowWrites;
  late bool _isGlobal;
  String? _tenantId;

  @override
  void initState() {
    super.initState();
    final existing = widget.existing;
    _name = TextEditingController(text: existing?.name ?? '');
    _connectionString = TextEditingController();
    _provider = existing?.provider ?? 'Postgres';
    _isActive = existing?.isActive ?? true;
    _allowWrites = existing?.allowWrites ?? false;
    _isGlobal = existing?.isGlobal ?? widget.tenants.isEmpty;
    _tenantId = existing?.tenantId;
  }

  @override
  void dispose() {
    _name.dispose();
    _connectionString.dispose();
    super.dispose();
  }

  String get _sample =>
      _providers
          .where((item) => item.value == _provider)
          .map((item) => item.sample)
          .firstOrNull ??
      '';

  void _submit() {
    if (_name.text.trim().isEmpty) {
      showAdminSnack(context, 'Tên kết nối không được trống.');
      return;
    }
    if (widget.existing == null && _connectionString.text.trim().isEmpty) {
      showAdminSnack(context, 'Cần nhập connection string khi tạo mới.');
      return;
    }
    Navigator.pop(context, {
      'name': _name.text.trim(),
      'provider': _provider,
      'connectionString': _connectionString.text.trim(),
      'isActive': _isActive,
      'allowWrites': _allowWrites,
      'isGlobal': _isGlobal,
      'tenantId': _isGlobal ? null : _tenantId,
    });
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: Text(
      widget.existing == null ? 'Thêm kết nối CSDL' : 'Sửa kết nối CSDL',
    ),
    content: SizedBox(
      width: 420,
      child: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            AdminField(controller: _name, label: 'Tên kết nối'),
            DropdownButtonFormField<String>(
              initialValue: _provider,
              // `isExpanded`: nhãn tiếng Việt dài + cỡ chữ hệ thống lớn vẫn phải
              // nằm trong khung, nếu không thì hàng bên trong tràn ra ngoài.
              isExpanded: true,
              decoration: const InputDecoration(labelText: 'Loại CSDL'),
              items: [
                for (final provider in _providers)
                  DropdownMenuItem(
                    value: provider.value,
                    child: Text(provider.label),
                  ),
              ],
              onChanged: (value) =>
                  setState(() => _provider = value ?? 'Postgres'),
            ),
            const SizedBox(height: 12),
            AdminField(
              controller: _connectionString,
              label: 'Connection string',
              hint: _sample,
              maxLines: 3,
            ),
            SwitchListTile(
              contentPadding: EdgeInsets.zero,
              value: _isActive,
              onChanged: (value) => setState(() => _isActive = value),
              title: const Text('Kích hoạt'),
            ),
            SwitchListTile(
              contentPadding: EdgeInsets.zero,
              value: _allowWrites,
              onChanged: (value) => setState(() => _allowWrites = value),
              title: const Text('Cho phép ghi (có phê duyệt)'),
            ),
            SwitchListTile(
              contentPadding: EdgeInsets.zero,
              value: _isGlobal,
              onChanged: (value) => setState(() => _isGlobal = value),
              title: const Text('Dùng chung toàn hệ thống'),
            ),
            if (!_isGlobal && widget.tenants.isNotEmpty)
              DropdownButtonFormField<String>(
                initialValue: _tenantId,
                isExpanded: true,
                decoration: const InputDecoration(labelText: 'Gán cho tenant'),
                items: [
                  for (final tenant in widget.tenants)
                    DropdownMenuItem(
                      value: tenant.id,
                      child: Text(tenant.name),
                    ),
                ],
                onChanged: (value) => setState(() => _tenantId = value),
              ),
          ],
        ),
      ),
    ),
    actions: [
      TextButton(
        onPressed: () => Navigator.pop(context),
        child: const Text('Huỷ'),
      ),
      AppPrimaryButton(label: 'Lưu', onPressed: _submit, expand: false),
    ],
  );
}
