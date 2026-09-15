import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_exception.dart';
import 'workspace_provider.dart';

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
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Đã lưu hướng dẫn tùy chỉnh.')),
        );
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
    appBar: AppBar(title: const Text('Custom Instructions')),
    body: _loading
        ? const Center(child: CircularProgressIndicator())
        : ListView(
            padding: const EdgeInsets.all(16),
            children: [
              if (_error != null)
                Card(
                  color: Theme.of(context).colorScheme.errorContainer,
                  child: Padding(
                    padding: const EdgeInsets.all(12),
                    child: Text(_error!),
                  ),
                ),
              const Text(
                'Những hướng dẫn này được thêm vào system prompt của các đoạn chat.',
              ),
              const SizedBox(height: 20),
              TextField(
                controller: _about,
                maxLines: 6,
                maxLength: maxLength,
                decoration: const InputDecoration(
                  labelText: 'Trợ lý nên biết gì về bạn?',
                  hintText: 'Ví dụ: Tôi là kỹ sư phần mềm ở Hà Nội...',
                ),
              ),
              const SizedBox(height: 16),
              TextField(
                controller: _style,
                maxLines: 6,
                maxLength: maxLength,
                decoration: const InputDecoration(
                  labelText: 'Bạn muốn trợ lý phản hồi như thế nào?',
                  hintText: 'Ví dụ: Trả lời ngắn gọn, luôn dùng tiếng Việt...',
                ),
              ),
              const SizedBox(height: 20),
              SizedBox(
                height: 48,
                child: FilledButton.icon(
                  onPressed: _saving ? null : _save,
                  icon: _saving
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Icons.check),
                  label: const Text('Lưu hướng dẫn'),
                ),
              ),
            ],
          ),
  );
}
