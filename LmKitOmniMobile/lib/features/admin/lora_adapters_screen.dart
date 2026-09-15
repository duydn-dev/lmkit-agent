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
        final saved = await showDialog<bool>(
          context: context,
          builder: (context) => AlertDialog(
            title: const Text('Tải LoRA adapter'),
            content: SingleChildScrollView(
              child: Column(
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
            ),
            actions: [
              TextButton(
                onPressed: () => Navigator.pop(context, false),
                child: const Text('Huỷ'),
              ),
              AppPrimaryButton(
                label: 'Tải lên',
                onPressed: () => Navigator.pop(context, true),
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
      final saved = await showDialog<bool>(
        context: context,
        builder: (context) => StatefulBuilder(
          builder: (context, setDialogState) => AlertDialog(
            title: const Text('Sửa adapter'),
            content: SingleChildScrollView(
              child: Column(
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
                  SwitchListTile(
                    contentPadding: EdgeInsets.zero,
                    value: isActive,
                    onChanged: (value) =>
                        setDialogState(() => isActive = value),
                    title: const Text('Kích hoạt'),
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

      final selected = await showModalBottomSheet<CustomAgentModel>(
        context: context,
        showDragHandle: true,
        builder: (context) => ListView(
          padding: const EdgeInsets.all(16),
          children: [
            Text(
              'Gán adapter cho custom agent',
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 8),
            for (final agent in agents)
              ListTile(
                title: Text(agent.name),
                subtitle: agent.description == null
                    ? null
                    : Text(agent.description!),
                onTap: () => Navigator.pop(context, agent),
              ),
          ],
        ),
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
      final selected = await showModalBottomSheet<CustomAgentModel>(
        context: context,
        showDragHandle: true,
        builder: (context) => ListView(
          padding: const EdgeInsets.all(16),
          children: [
            Text(
              'Bỏ gán adapter khỏi agent',
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 8),
            for (final agent in agents)
              ListTile(
                title: Text(agent.name),
                onTap: () => Navigator.pop(context, agent),
              ),
          ],
        ),
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
      itemBuilder: (context, adapter, reload) => Card(
        child: ListTile(
          isThreeLine: true,
          leading: Icon(adapter.isActive ? Icons.tune : Icons.tune_outlined),
          title: Text(adapter.name),
          subtitle: Text(
            '${adapter.description?.trim().isNotEmpty == true ? '${adapter.description}\n' : ''}'
            'scale ${adapter.scale} · ${adapter.displaySize}'
            '${adapter.targetModelId?.isNotEmpty == true ? ' · ${adapter.targetModelId}' : ''}',
          ),
          trailing: PopupMenuButton<String>(
            tooltip: 'Tuỳ chọn',
            onSelected: (value) async {
              switch (value) {
                case 'edit':
                  await edit(context, adapter, reload);
                case 'assign':
                  await assign(context, adapter, reload);
                case 'unassign':
                  await unassign(context, reload);
                case 'delete':
                  await remove(context, adapter, reload);
              }
            },
            itemBuilder: (context) => const [
              PopupMenuItem(value: 'edit', child: Text('Sửa')),
              PopupMenuItem(value: 'assign', child: Text('Gán cho agent')),
              PopupMenuItem(value: 'unassign', child: Text('Bỏ gán')),
              PopupMenuItem(value: 'delete', child: Text('Xoá')),
            ],
          ),
        ),
      ),
    );
  }
}
