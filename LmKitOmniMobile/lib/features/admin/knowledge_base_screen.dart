import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';
import 'admin_provider.dart';
import 'admin_repository.dart';
import 'admin_widgets.dart';
import '../../app/ui/app_controls.dart';

/// Nạp văn bản vào kho tri thức tenant và truy vấn thử để kiểm chứng.
class KnowledgeBaseScreen extends ConsumerStatefulWidget {
  const KnowledgeBaseScreen({super.key});

  @override
  ConsumerState<KnowledgeBaseScreen> createState() =>
      _KnowledgeBaseScreenState();
}

class _KnowledgeBaseScreenState extends ConsumerState<KnowledgeBaseScreen> {
  final _fileName = TextEditingController();
  final _content = TextEditingController();
  final _query = TextEditingController();

  bool _busy = false;
  int _topK = 3;
  String? _ingestResult;
  String? _queryResult;

  @override
  void dispose() {
    _fileName.dispose();
    _content.dispose();
    _query.dispose();
    super.dispose();
  }

  Future<void> _ingest() async {
    if (_fileName.text.trim().isEmpty || _content.text.trim().isEmpty) {
      showAdminSnack(context, 'Cần nhập tên tài liệu và nội dung.');
      return;
    }
    setState(() {
      _busy = true;
      _ingestResult = null;
    });
    try {
      final result = await ref
          .read(adminRepositoryProvider)
          .ingestKnowledge(fileName: _fileName.text, content: _content.text);
      if (mounted) setState(() => _ingestResult = 'Đã nạp: $result');
    } catch (error) {
      if (mounted) setState(() => _ingestResult = adminErrorMessage(error));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _runQuery() async {
    if (_query.text.trim().isEmpty) {
      showAdminSnack(context, 'Nhập câu truy vấn trước.');
      return;
    }
    setState(() {
      _busy = true;
      _queryResult = null;
    });
    try {
      final result = await ref
          .read(adminRepositoryProvider)
          .queryKnowledge(query: _query.text, topK: _topK);
      if (mounted) setState(() => _queryResult = result);
    } catch (error) {
      if (mounted) setState(() => _queryResult = adminErrorMessage(error));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppTopBar(title: const Text('Cơ sở kiến thức')),
    body: ListView(
      padding: const EdgeInsets.all(16),
      children: [
        if (_busy) const LinearProgressIndicator(),
        AppCard(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Nạp tài liệu',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                const SizedBox(height: 6),
                Text(
                  'Nội dung văn bản được chunk và nhúng vào vector store của tenant.',
                  style: Theme.of(
                    context,
                  ).textTheme.bodyMedium?.copyWith(color: AppTheme.textMuted),
                ),
                const SizedBox(height: 12),
                AdminField(controller: _fileName, label: 'Tên tài liệu'),
                AdminField(
                  controller: _content,
                  label: 'Nội dung',
                  maxLines: 6,
                ),
                Align(
                  alignment: Alignment.centerRight,
                  child: AppPrimaryButton(
                    label: 'Nạp vào kho tri thức',
                    icon: Icons.upload_file,
                    onPressed: _busy ? null : _ingest,
                    expand: false,
                  ),
                ),
                if (_ingestResult != null) ...[
                  const SizedBox(height: 12),
                  Text(_ingestResult!),
                ],
              ],
            ),
          ),
        ),
        const SizedBox(height: 16),
        AppCard(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Truy vấn thử',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                const SizedBox(height: 12),
                AdminField(controller: _query, label: 'Câu truy vấn'),
                // `AppSelectTile` thay cho `Slider` của Material: thanh trượt
                // Material mang sẵn màu/độ dày của Material nên lệch hẳn với
                // phần còn lại, mà thang 1–10 thì chọn trong danh sách còn rõ
                // giá trị đang chọn hơn.
                AppSelectTile<int>(
                  label: 'Số đoạn trả về',
                  icon: Icons.format_list_numbered,
                  value: _topK,
                  items: [for (var i = 1; i <= 10; i++) i],
                  labelOf: (value) => '$value đoạn',
                  onChanged: (value) => setState(() => _topK = value),
                ),
                Align(
                  alignment: Alignment.centerRight,
                  child: AppPrimaryButton(
                    label: 'Truy vấn',
                    icon: Icons.search,
                    onPressed: _busy ? null : _runQuery,
                    expand: false,
                  ),
                ),
                if (_queryResult != null) ...[
                  const SizedBox(height: 12),
                  SelectableText(_queryResult!),
                ],
              ],
            ),
          ),
        ),
      ],
    ),
  );
}
