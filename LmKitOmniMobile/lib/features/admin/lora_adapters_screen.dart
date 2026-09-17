import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_exception.dart';
import '../studio/studio_models.dart';
import '../studio/studio_provider.dart';
import 'admin_models.dart';
import 'admin_provider.dart';
import 'admin_repository.dart';
import 'admin_widgets.dart';
import '../../app/ui/app_controls.dart';

class LoraAdaptersScreen extends ConsumerWidget {
  const LoraAdaptersScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final repository = ref.watch(adminRepositoryProvider);
    final texts = Theme.of(context).textTheme;

    Future<void> upload(
      BuildContext context,
      Future<void> Function() reload,
    ) async {
      try {
        final file = await FilePicker.pickFile(type: FileType.any);
        if (file == null) return;
        final path = file.path;
        if (path == null) {
          if (context.mounted) showAdminSnack(context, 'Không đọc được file.');
          return;
        }
        if (!context.mounted) return;

        final name = TextEditingController(
          text: file.name.replaceAll(RegExp(r'\.[^.]+$'), ''),
        );
        final description = TextEditingController();
        final scale = TextEditingController(text: '1.0');
        final targetModel = TextEditingController();
        final saved = await showAppDialog<bool>(
          context,
          builder: (dialogContext) => AppDialog(
            title: 'Tải LoRA adapter',
            content: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                AdminField(controller: name, label: 'Tên adapter'),
                AdminField(controller: description, label: 'Mô tả'),
                AdminField(
                  controller: scale,
                  label: 'Scale',
                  hint: '1.0',
                  keyboardType: const TextInputType.numberWithOptions(
                    decimal: true,
                  ),
                ),
                AdminField(
                  controller: targetModel,
                  label: 'Model đích',
                  hint: 'Để trống nếu áp dụng cho model mặc định',
                ),
              ],
            ),
            actions: [
              AppSecondaryButton(
                label: 'Huỷ',
                onPressed: () => Navigator.pop(dialogContext, false),
              ),
              const SizedBox(width: 10),
              AppPrimaryButton(
                label: 'Tải lên',
                onPressed: () => Navigator.pop(dialogContext, true),
                expand: false,
              ),
            ],
          ),
        );
        if (saved != true) return;

        await repository.uploadLoraAdapter(
          name: name.text.trim().isEmpty ? file.name : name.text.trim(),
          path: path,
          fileName: file.name,
          description: description.text.trim(),
          scale: double.tryParse(scale.text.trim()),
          targetModelId: targetModel.text.trim(),
        );
        name.dispose();
        description.dispose();
        scale.dispose();
        targetModel.dispose();
        await reload();
        if (context.mounted) showAdminSnack(context, 'Đã tải adapter lên.');
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
      }
    }

    Future<void> edit(
      BuildContext context,
      LoraAdapterModel adapter,
      Future<void> Function() reload,
    ) async {
      final name = TextEditingController(text: adapter.name);
      final scale = TextEditingController(text: adapter.scale.toString());
      var isActive = adapter.isActive;
      final saved = await showAppDialog<bool>(
        context,
        builder: (dialogContext) => StatefulBuilder(
          builder: (dialogContext, setDialogState) => AppDialog(
            title: 'Sửa adapter',
            content: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                AdminField(controller: name, label: 'Tên adapter'),
                AdminField(
                  controller: scale,
                  label: 'Scale',
                  keyboardType: const TextInputType.numberWithOptions(
                    decimal: true,
                  ),
                ),
                AppSwitchTile(
                  label: 'Kích hoạt',
                  value: isActive,
                  onChanged: (value) => setDialogState(() => isActive = value),
                ),
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
      if (saved != true) return;
      try {
        await repository.updateLoraAdapter(
          id: adapter.id,
          name: name.text.trim().isEmpty ? adapter.name : name.text.trim(),
          scale: double.tryParse(scale.text.trim()) ?? adapter.scale,
          isActive: isActive,
        );
        name.dispose();
        scale.dispose();
        await reload();
        if (context.mounted) showAdminSnack(context, 'Đã lưu adapter.');
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
      }
    }

    Future<void> assign(
      BuildContext context,
      LoraAdapterModel adapter,
      Future<void> Function() reload,
    ) async {
      List<CustomAgentModel> agents;
      try {
        agents = await ref.read(studioRepositoryProvider).customAgents();
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
        return;
      }
      if (!context.mounted) return;
      if (agents.isEmpty) {
        showAdminSnack(context, 'Bạn chưa có custom agent nào để gán adapter.');
        return;
      }

      final selected = await showAppPicker<CustomAgentModel>(
        context,
        title: 'Gán adapter cho custom agent',
        items: agents,
        labelOf: (agent) => agent.name,
        subtitleOf: (agent) => agent.description ?? '',
      );
      if (selected == null || !context.mounted) return;

      try {
        await repository.assignLoraAdapter(
          adapterId: adapter.id,
          agentId: selected.id,
        );
        await reload();
        if (context.mounted) {
          showAdminSnack(context, 'Đã gán cho ${selected.name}.');
        }
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
      }
    }

    Future<void> unassign(
      BuildContext context,
      Future<void> Function() reload,
    ) async {
      List<CustomAgentModel> agents;
      try {
        agents = await ref.read(studioRepositoryProvider).customAgents();
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
        return;
      }
      if (!context.mounted) return;
      final selected = await showAppPicker<CustomAgentModel>(
        context,
        title: 'Bỏ gán adapter khỏi agent',
        items: agents,
        labelOf: (agent) => agent.name,
      );
      if (selected == null || !context.mounted) return;
      try {
        await repository.unassignLoraAdapter(selected.id);
        await reload();
        if (context.mounted) {
          showAdminSnack(context, 'Đã bỏ gán cho ${selected.name}.');
        }
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
      }
    }

    Future<void> remove(
      BuildContext context,
      LoraAdapterModel adapter,
      Future<void> Function() reload,
    ) async {
      final confirmed = await confirmAdminAction(
        context,
        title: 'Xoá adapter',
        message: 'Xoá "${adapter.name}" cùng file adapter trên server?',
      );
      if (!confirmed) return;
      try {
        await repository.deleteLoraAdapter(adapter.id);
        await reload();
        if (context.mounted) showAdminSnack(context, 'Đã xoá adapter.');
      } catch (error) {
        if (context.mounted) {
          showAdminSnack(context, adminErrorMessage(error));
        }
      }
    }

    return AdminListView<LoraAdapterModel>(
      title: 'LoRA Adapters',
      description:
          'Tính năng LoRA có thể bị tắt ở cấu hình server; khi đó API trả 501 và '
          'màn này không hiển thị dữ liệu.',
      emptyText: 'Chưa có LoRA adapter nào.',
      fetch: (search) async {
        try {
          return await repository.loraAdapters();
        } catch (error) {
          if (isFeatureDisabled(error)) {
            throw const ApiException(
              message: 'Tính năng LoRA đang tắt trên server.',
              statusCode: 501,
            );
          }
          rethrow;
        }
      },
      fabBuilder: (context, reload) => FloatingActionButton.extended(
        onPressed: () => upload(context, reload),
        icon: const Icon(Icons.upload),
        label: const Text('Tải adapter'),
      ),
      itemBuilder: (context, adapter, reload) => AppCard(
        child: AppTileRaw(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(16, 14, 8, 14),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Icon(adapter.isActive ? Icons.tune : Icons.tune_outlined),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(adapter.name, style: texts.titleSmall),
                      const SizedBox(height: 2),
                      Text(
                        '${adapter.description?.trim().isNotEmpty == true ? '${adapter.description}\n' : ''}'
                        'scale ${adapter.scale} · ${adapter.displaySize}'
                        '${adapter.targetModelId?.isNotEmpty == true ? ' · ${adapter.targetModelId}' : ''}',
                        style: texts.bodySmall,
                      ),
                    ],
                  ),
                ),
                AppMenuButton(
                  tooltip: 'Tuỳ chọn',
                  items: [
                    AppMenuItem('Sửa', () => edit(context, adapter, reload)),
                    AppMenuItem(
                      'Gán cho agent',
                      () => assign(context, adapter, reload),
                    ),
                    AppMenuItem('Bỏ gán', () => unassign(context, reload)),
                    AppMenuItem(
                      'Xoá',
                      () => remove(context, adapter, reload),
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
