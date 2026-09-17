import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

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
    final texts = Theme.of(context).textTheme;

    Future<void> edit(
      BuildContext context,
      TenantModel? existing,
      Future<void> Function() reload,
    ) async {
      final name = TextEditingController(text: existing?.name ?? '');
      final agent = TextEditingController(
        text: existing?.agentDisplayName ?? '',
      );
      // Trạng thái logo ngay trong hộp thoại, để phần xem trước đổi tại chỗ sau
      // khi tải/gỡ — không phải lưu rồi mở lại hộp thoại mới thấy kết quả.
      var hasLogo = existing?.hasLogo ?? false;
      var logoVersion = existing?.logoUpdatedAt?.millisecondsSinceEpoch ?? 0;

      final saved = await showAppDialog<bool>(
        context,
        builder: (dialogContext) => StatefulBuilder(
          builder: (dialogContext, setDialogState) => AppDialog(
            title: existing == null ? 'Tạo tenant' : 'Sửa tenant',
            content: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                AdminField(controller: name, label: 'Tên tenant'),
                AdminField(
                  controller: agent,
                  label: 'Tên trợ lý AI',
                  hint: 'Để trống để dùng mặc định',
                ),
                // Logo là **cấu hình theo từng tenant**; đơn vị chưa tải lên thì
                // dùng Quốc huy. Chỉ hiện khi sửa: tenant mới chưa có id để gắn
                // logo (web cũng vậy — "Lưu tenant trước, rồi mở lại").
                if (existing != null) ...[
                  const SizedBox(height: 16),
                  _LogoField(
                    tenant: existing,
                    hasLogo: hasLogo,
                    version: logoVersion,
                    onPick: () async {
                      final ok = await _uploadTenantLogo(
                        dialogContext,
                        repository,
                        existing,
                        reload,
                      );
                      if (!ok) return;
                      setDialogState(() {
                        hasLogo = true;
                        // Mốc mới để ảnh không bị `ImageCache` trả bản cũ.
                        logoVersion = DateTime.now().millisecondsSinceEpoch;
                      });
                    },
                    onRemove: () async {
                      final ok = await _removeTenantLogo(
                        dialogContext,
                        repository,
                        existing,
                        reload,
                      );
                      if (!ok) return;
                      setDialogState(() => hasLogo = false);
                    },
                  ),
                ],
              ],
            ),
            actions: [
              AppSecondaryButton(
                label: 'Huỷ',
                onPressed: () => Navigator.pop(dialogContext, false),
              ),
              const SizedBox(width: 10),
              AppPrimaryButton(
                label: 'Lưu',
                onPressed: () => Navigator.pop(dialogContext, true),
                expand: false,
              ),
            ],
          ),
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
      itemBuilder: (context, tenant, reload) => AppCard(
        child: AppTileRaw(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(16, 14, 8, 14),
            child: Row(
              children: [
                _TenantLogo(tenant: tenant),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(tenant.name, style: texts.titleSmall),
                      const SizedBox(height: 2),
                      Text(
                        '${tenant.agentDisplayName?.trim().isNotEmpty == true ? tenant.agentDisplayName : 'Trợ lý mặc định'} · '
                        '${tenant.userCount} user · ${tenant.databaseConnectionCount} CSDL',
                        style: texts.bodySmall,
                      ),
                    ],
                  ),
                ),
                AppMenuButton(
                  tooltip: 'Tuỳ chọn',
                  items: [
                    // Logo nằm trong hộp thoại sửa — **một chỗ duy nhất** cho
                    // cấu hình theo từng tenant, thay vì hai lối vào cùng làm
                    // một việc rồi lệch nhau lúc sửa. Web cũng đặt logo trong form.
                    AppMenuItem(
                      'Sửa (tên, logo)',
                      () => edit(context, tenant, reload),
                    ),
                    AppMenuItem(
                      'Xoá tenant',
                      () => remove(context, tenant, reload),
                      destructive: true,
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// Tải logo riêng cho một tenant (đơn vị tự chọn ảnh của mình).
///
/// Trả về `true` khi logo **đã đổi trên máy chủ**, để chỗ gọi biết có phải vẽ
/// lại phần xem trước hay không — người dùng bấm Huỷ ở hộp chọn tệp cũng đi qua
/// đây và phải được coi là "không có gì thay đổi".
Future<bool> _uploadTenantLogo(
  BuildContext context,
  AdminRepository repository,
  TenantModel tenant,
  Future<void> Function() reload,
) async {
  try {
    final file = await FilePicker.pickFile(type: FileType.image);
    if (file == null) return false;
    final path = file.path;
    if (path == null) {
      if (context.mounted) showAdminSnack(context, 'Không đọc được file.');
      return false;
    }
    await repository.uploadTenantLogo(
      tenant.id,
      path: path,
      fileName: file.name,
    );
    await reload();
    if (context.mounted) showAdminSnack(context, 'Đã cập nhật logo.');
    return true;
  } catch (error) {
    if (context.mounted) showAdminSnack(context, adminErrorMessage(error));
    return false;
  }
}

/// Xoá logo riêng của tenant → chỗ hiển thị quay về **Quốc huy** (mặc định).
Future<bool> _removeTenantLogo(
  BuildContext context,
  AdminRepository repository,
  TenantModel tenant,
  Future<void> Function() reload,
) async {
  final confirmed = await confirmAdminAction(
    context,
    title: 'Xoá logo',
    message:
        'Logo của ${tenant.name} sẽ bị xoá khỏi hệ thống. Từ đó đơn vị dùng lại '
        'Quốc huy (logo mặc định).',
  );
  if (!confirmed) return false;
  try {
    await repository.deleteTenantLogo(tenant.id);
    await reload();
    if (context.mounted) showAdminSnack(context, 'Đã xoá logo.');
    return true;
  } catch (error) {
    if (context.mounted) showAdminSnack(context, adminErrorMessage(error));
    return false;
  }
}

/// Khối "Logo đơn vị" trong hộp thoại sửa tenant: xem trước + tải lên + gỡ.
///
/// Nhãn nói rõ **mặc định là Quốc huy** — quản trị viên phải biết rằng để trống
/// không phải là lỗi cấu hình, mà là dùng dấu nhận diện chung.
class _LogoField extends StatelessWidget {
  const _LogoField({
    required this.tenant,
    required this.hasLogo,
    required this.version,
    required this.onPick,
    required this.onRemove,
  });

  final TenantModel tenant;
  final bool hasLogo;
  final int version;
  final Future<void> Function() onPick;
  final Future<void> Function() onRemove;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text('Logo đơn vị', style: Theme.of(context).textTheme.labelLarge),
      const SizedBox(height: 8),
      Row(
        children: [
          TenantLogo(
            path: hasLogo ? '/api/tenants/${tenant.id}/logo?v=$version' : null,
            size: 48,
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                AppPrimaryButton(
                  label: 'Tải logo',
                  icon: Icons.upload_outlined,
                  expand: false,
                  onPressed: onPick,
                ),
                if (hasLogo)
                  AppSecondaryButton(
                    label: 'Gỡ',
                    icon: Icons.delete_outline,
                    onPressed: onRemove,
                  ),
              ],
            ),
          ),
        ],
      ),
      const SizedBox(height: 6),
      Text(
        hasLogo
            ? 'Logo riêng của đơn vị đang được dùng.'
            : 'Chưa tải logo riêng — hệ thống dùng Quốc huy làm mặc định.',
        style: Theme.of(context).textTheme.bodySmall,
      ),
    ],
  );
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
    // Đơn vị chưa có logo thì hiện **Quốc huy** (mặc định của [TenantLogo]),
    // không dùng avatar chữ cái: trong danh sách đơn vị, chữ cái đầu gần như
    // luôn trùng nhau ("Trung tâm…", "Sở…") nên nó không phân biệt được gì.
    if (!tenant.hasLogo) return const TenantLogo(size: 40);

    final version = tenant.logoUpdatedAt?.millisecondsSinceEpoch ?? 0;
    return TenantLogo(
      path: '/api/tenants/${tenant.id}/logo?v=$version',
      size: 40,
    );
  }
}
