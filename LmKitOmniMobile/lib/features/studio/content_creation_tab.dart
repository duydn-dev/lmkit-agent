import 'package:flutter/material.dart';

import 'studio_repository.dart';
import '../../app/theme.dart';
import '../../app/ui/app_controls.dart';

class ContentCreationTab extends StatefulWidget {
  const ContentCreationTab({
    super.key,
    required this.repo,
    required this.onError,
  });
  final StudioRepository repo;
  final ValueChanged<String> onError;

  @override
  State<ContentCreationTab> createState() => _ContentCreationTabState();
}

class _ContentCreationTabState extends State<ContentCreationTab> {
  final _topic = TextEditingController();
  String _finalContent = '';
  List<Map<String, dynamic>> _stages = const [];
  bool _busy = false;

  @override
  void dispose() {
    _topic.dispose();
    super.dispose();
  }

  Future<void> _run() async {
    if (_topic.text.trim().isEmpty || _busy) return;
    setState(() {
      _busy = true;
      _finalContent = '';
      _stages = const [];
    });
    try {
      final result = await widget.repo.createContent(_topic.text);
      if (mounted) {
        setState(() {
          _finalContent = result['finalContent']?.toString() ?? '';
          final stages = result['stages'];
          _stages = stages is List
              ? stages
                    .whereType<Map>()
                    .map((e) => Map<String, dynamic>.from(e))
                    .toList()
              : const [];
        });
      }
    } catch (error) {
      widget.onError(error.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) => ListView(
    padding: const EdgeInsets.all(16),
    children: [
      Text('Tạo nội dung', style: Theme.of(context).textTheme.titleLarge),
      const SizedBox(height: 6),
      Text(
        'Pipeline nhiều bước: nghiên cứu, dàn ý, bản nháp và kiểm chứng.',
        style: Theme.of(
          context,
        ).textTheme.bodyMedium?.copyWith(color: AppTheme.textMuted),
      ),
      const SizedBox(height: 16),
      TextField(
        controller: _topic,
        minLines: 3,
        maxLines: 6,
        enabled: !_busy,
        decoration: const InputDecoration(labelText: 'Chủ đề nội dung'),
      ),
      const SizedBox(height: 12),
      AppPrimaryButton(
        label: _busy ? 'Đang tạo...' : 'Bắt đầu pipeline',
        icon: Icons.auto_awesome,
        onPressed: _busy ? null : _run,
        expand: false,
      ),
      for (final stage in _stages)
        Card(
          margin: const EdgeInsets.only(top: 12),
          child: ExpansionTile(
            title: Text(stage['stageName']?.toString() ?? 'Bước'),
            subtitle: Text(stage['isSuccess'] == true ? 'Hoàn tất' : 'Có lỗi'),
            children: [
              Padding(
                padding: const EdgeInsets.all(16),
                child: SelectableText(
                  stage['content']?.toString() ??
                      stage['errorMessage']?.toString() ??
                      '',
                ),
              ),
            ],
          ),
        ),
      if (_finalContent.isNotEmpty)
        Card(
          margin: const EdgeInsets.only(top: 16),
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: SelectableText(_finalContent),
          ),
        ),
    ],
  );
}
