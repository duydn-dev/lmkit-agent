import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_exception.dart';
import 'workspace_provider.dart';
import '../../app/ui/app_controls.dart';

class CustomInstructionsScreen extends ConsumerStatefulWidget {
  const CustomInstructionsScreen({super.key});

  @override
  ConsumerState<CustomInstructionsScreen> createState() =>
      _CustomInstructionsScreenState();
}

class _CustomInstructionsScreenState
    extends ConsumerState<CustomInstructionsScreen> {
  static const maxLength = 2000;
  final _about = TextEditingController();
  final _style = TextEditingController();
  bool _loading = true;
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _about.dispose();
    _style.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final model = await ref.read(workspaceRepositoryProvider).instructions();
      if (mounted) {
        setState(() {
          _about.text = model.aboutUser ?? '';
          _style.text = model.responseStyle ?? '';
        });
      }
    } catch (error) {
      if (mounted) {
        setState(() => _error = _message(error));
      }
    } finally {
      if (mounted) {
        setState(() => _loading = false);
      }
    }
  }

  Future<void> _save() async {
    if (_about.text.length > maxLength || _style.text.length > maxLength) {
      return;
    }
    setState(() {
      _saving = true;
      _error = null;
    });
    try {
      await ref
          .read(workspaceRepositoryProvider)
          .saveInstructions(aboutUser: _about.text, responseStyle: _style.text);
      if (mounted) {
        showAppSnack(context, 'Đã lưu hướng dẫn tùy chỉnh.');
      }
    } catch (error) {
      if (mounted) {
        setState(() => _error = _message(error));
      }
    } finally {
      if (mounted) {
        setState(() => _saving = false);
      }
    }
  }

  String _message(Object error) =>
      error is ApiException ? error.message : error.toString();

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppTopBar(title: const Text('Custom Instructions')),
    body: _loading
        ? const Center(child: CircularProgressIndicator())
        : ListView(
            padding: const EdgeInsets.all(16),
            children: [
              if (_error != null)
                AppCard(
                  child: Padding(
                    padding: const EdgeInsets.all(12),
                    child: Text(_error!),
                  ),
                ),
              const Text(
                'Những hướng dẫn này được thêm vào system prompt của các đoạn chat.',
              ),
              const SizedBox(height: 20),
              // Ô nhập của hệ thiết kế (Forui). Giới hạn độ dài do [_save] chặn
              // trước khi gọi API; trước đây `maxLength` của Material vừa hiện
              // bộ đếm ký tự vừa tự cắt chữ — không cần bộ đếm đó nữa.
              AppTextField(
                controller: _about,
                label: 'Trợ lý nên biết gì về bạn?',
                hint: 'Ví dụ: Tôi là kỹ sư phần mềm ở Hà Nội...',
                maxLines: 6,
              ),
              AppTextField(
                controller: _style,
                label: 'Bạn muốn trợ lý phản hồi như thế nào?',
                hint: 'Ví dụ: Trả lời ngắn gọn, luôn dùng tiếng Việt...',
                maxLines: 6,
              ),
              const SizedBox(height: 20),
              SizedBox(
                height: 48,
                child: AppPrimaryButton(
                  label: 'Lưu hướng dẫn',
                  onPressed: _saving ? null : _save,
                  expand: false,
                ),
              ),
            ],
          ),
  );
}
