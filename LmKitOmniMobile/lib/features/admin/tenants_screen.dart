import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';
import '../../app/ui/tenant_logo.dart';
import 'admin_models.dart';
import 'admin_provider.dart';
import 'admin_repository.dart';
import 'admin_widgets.dart';
import '../../app/ui/app_controls.dart';

class TenantsScreen extends ConsumerWidget {
  const TenantsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final repository = ref.watch(adminRepositoryProvider);

    Future<void> edit(
      BuildContext context,
      TenantModel? existing,
      Future<void> Function() reload,
    ) async {
      final name = TextEditingController(text: existing?.name ?? '');
      final agent = TextEditingController(
        text: existing?.agentDisplayName ?? '',
      );
      final saved = await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
          title: Text(existing == null ? 'Tạo tenant' : 'Sửa tenant'),
          content: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                AdminField(controller: name, label: 'Tên tenant'),
                AdminField(
                  controller: agent,
                  label: 'Tên trợ lý AI',
                  hint: 'Để trống để dùng mặc định',
                ),
              ],
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(context, false),
              child: const Text('Huỷ'),
            ),
            AppPrimaryButton(
              label: 'Lưu',
              onPressed: () => Navigator.pop(context, true),
              expand: false,
            ),
          ],
        ),
      );
      if (saved != true) {
        name.dispose();
        agent.dispose();
        return;
      }
      final trimmed = name.text.trim();
      name.dispose();
      final agentName = agent.text.trim();
      agent.dispose();
      if (trimmed.isEmpty) {
        if (context.mounted) {
          showAdminSnack(context, 'Tên tenant không được trống.');
        }
        return;
      }

      try {
        if (existing == null) {
          await repository.createTenant(
            name: trimmed,
            agentDisplayName: agentName,
          );
        } else {
          await repository.updateTenant(
            id: existing.id,
            name: trimmed,
            agentDisplayName: agentName,
          );
        }
        await reload();
        if (context.mounted) {
          showAdminSnack(
            context,
            existing == null ? 'Đã tạo tenant.' : 'Đã lưu.',
          );
        }
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
      }
    }

    Future<void> uploadLogo(
      BuildContext context,
      TenantModel tenant,
      Future<void> Function() reload,
    ) async {
      try {
        final file = await FilePicker.pickFile(type: FileType.image);
        if (file == null) return;
        final path = file.path;
        if (path == null) {
          if (context.mounted) showAdminSnack(context, 'Không đọc được file.');
          return;
        }
        await repository.uploadTenantLogo(
          tenant.id,
          path: path,
          fileName: file.name,
        );
        await reload();
        if (context.mounted) showAdminSnack(context, 'Đã cập nhật logo.');
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
      }
    }

    Future<void> removeLogo(
      BuildContext context,
      TenantModel tenant,
      Future<void> Function() reload,
    ) async {
      final confirmed = await confirmAdminAction(
        context,
        title: 'Xoá logo',
        message: 'Logo của ${tenant.name} sẽ bị xoá khỏi hệ thống.',
      );
      if (!confirmed) return;
      try {
        await repository.deleteTenantLogo(tenant.id);
        await reload();
        if (context.mounted) showAdminSnack(context, 'Đã xoá logo.');
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
      }
    }

    Future<void> remove(
      BuildContext context,
      TenantModel tenant,
      Future<void> Function() reload,
    ) async {
      final confirmed = await confirmAdminAction(
        context,
        title: 'Xoá tenant',
        message:
            'Xoá "${tenant.name}" (${tenant.userCount} người dùng, '
            '${tenant.databaseConnectionCount} kết nối CSDL). Hành động này không '
            'hoàn tác được.',
      );
      if (!confirmed) return;
      try {
        await repository.deleteTenant(tenant.id);
        await reload();
        if (context.mounted) showAdminSnack(context, 'Đã xoá tenant.');
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
      }
    }

    return AdminListView<TenantModel>(
      title: 'Tenant Management',
      description:
          'Tên tenant và tên trợ lý AI dùng cho branding khi người dùng đăng nhập.',
      searchHint: 'Tìm tenant',
      emptyText: 'Chưa có tenant nào.',
      fetch: (search) => repository.tenants(search: search),
      fabBuilder: (context, reload) => FloatingActionButton.extended(
        onPressed: () => edit(context, null, reload),
        icon: const Icon(Icons.add),
        label: const Text('Tạo tenant'),
      ),
      itemBuilder: (context, tenant, reload) => Card(
        child: ListTile(
          leading: _TenantLogo(tenant: tenant),
          title: Text(tenant.name),
          subtitle: Text(
            '${tenant.agentDisplayName?.trim().isNotEmpty == true ? tenant.agentDisplayName : 'Trợ lý mặc định'} · '
            '${tenant.userCount} user · ${tenant.databaseConnectionCount} CSDL',
          ),
          trailing: PopupMenuButton<String>(
            tooltip: 'Tuỳ chọn',
            onSelected: (value) => switch (value) {
              'edit' => edit(context, tenant, reload),
              'logo' => uploadLogo(context, tenant, reload),
              'delete-logo' => removeLogo(context, tenant, reload),
              _ => remove(context, tenant, reload),
            },
            itemBuilder: (context) => [
              const PopupMenuItem(value: 'edit', child: Text('Sửa')),
              const PopupMenuItem(value: 'logo', child: Text('Tải logo')),
              if (tenant.hasLogo)
                const PopupMenuItem(
                  value: 'delete-logo',
                  child: Text('Xoá logo'),
                ),
              const PopupMenuItem(value: 'delete', child: Text('Xoá tenant')),
            ],
          ),
        ),
      ),
    );
  }
}

/// Logo tenant nằm sau route cần Bearer token nên phải gửi kèm header.
/// Logo tenant trong danh sách quản trị.
///
/// Dùng lại [TenantLogo] để việc gửi kèm Bearer chỉ tồn tại ở một chỗ — trước
/// đây màn này tự dựng lại và dễ lệch khỏi logo ở khung chat.
class _TenantLogo extends StatelessWidget {
  const _TenantLogo({required this.tenant});

  final TenantModel tenant;

  @override
  Widget build(BuildContext context) {
    if (!tenant.hasLogo) return _fallback(context);

    final version = tenant.logoUpdatedAt?.millisecondsSinceEpoch ?? 0;
    return TenantLogo(
      path: '/api/tenants/${tenant.id}/logo?v=$version',
      size: 40,
      fallback: _fallback(context),
    );
  }

  Widget _fallback(BuildContext context) => Container(
    width: 40,
    height: 40,
    alignment: Alignment.center,
    decoration: const BoxDecoration(
      shape: BoxShape.circle,
      color: AppTheme.surfaceMuted,
    ),
    child: Text(
      tenant.name.isEmpty ? '?' : tenant.name.characters.first.toUpperCase(),
      style: Theme.of(context).textTheme.titleMedium,
    ),
  );
}
