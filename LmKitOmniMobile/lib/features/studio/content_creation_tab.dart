import 'package:flutter/material.dart';

import 'studio_repository.dart';

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
      const Text(
        'Tạo nội dung',
        style: TextStyle(fontSize: 20, fontWeight: FontWeight.w700),
      ),
      const SizedBox(height: 4),
      const Text(
        'Pipeline nhiều bước: nghiên cứu, dàn ý, bản nháp và kiểm chứng.',
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
      FilledButton.icon(
        onPressed: _busy ? null : _run,
        icon: const Icon(Icons.auto_awesome),
        label: Text(_busy ? 'Đang tạo...' : 'Bắt đầu pipeline'),
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
