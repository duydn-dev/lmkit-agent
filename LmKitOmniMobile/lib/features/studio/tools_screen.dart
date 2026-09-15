import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';

import '../../core/network/api_exception.dart';
import 'studio_provider.dart';

class ToolsScreen extends ConsumerStatefulWidget {
  const ToolsScreen({super.key});

  @override
  ConsumerState<ToolsScreen> createState() => _ToolsScreenState();
}

class _ToolsScreenState extends ConsumerState<ToolsScreen>
    with SingleTickerProviderStateMixin {
  late final TabController _tabs = TabController(length: 2, vsync: this);
  final _text = TextEditingController();
  final _categories = TextEditingController(
    text: 'Kinh tế, Thể thao, Giải trí',
  );
  final _prompt = TextEditingController(text: 'Mô tả chi tiết hình ảnh này.');
  final _imageCategories = TextEditingController(
    text: 'người, vật thể, tài liệu',
  );
  String? _imagePath;
  String _textResult = '', _visionResult = '';
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _tabs.dispose();
    _text.dispose();
    _categories.dispose();
    _prompt.dispose();
    _imageCategories.dispose();
    super.dispose();
  }

  String _message(Object error) =>
      error is ApiException ? error.message : error.toString();

  Future<void> _runText(String operation) async {
    final value = _text.text.trim();
    if (value.isEmpty || _busy) return;
    setState(() {
      _busy = true;
      _error = null;
      _textResult = '';
    });
    try {
      final repo = ref.read(studioRepositoryProvider);
      final result = switch (operation) {
        'analyze' => await repo.analyzeText(value),
        'classify' => await repo.classifyText(
          value,
          _categories.text
              .split(',')
              .map((e) => e.trim())
              .where((e) => e.isNotEmpty)
              .toList(),
        ),
        'language' => await repo.detectLanguage(value),
        'embeddings' => await repo.embeddings(value),
        _ => await repo.extractKeywords(value),
      };
      if (mounted) setState(() => _textResult = _pretty(result));
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _pickImage() async {
    final file = await ImagePicker().pickImage(
      source: ImageSource.gallery,
      imageQuality: 90,
    );
    if (file == null || !mounted) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final path = await ref
          .read(studioRepositoryProvider)
          .uploadVisionImage(file);
      if (mounted) setState(() => _imagePath = path);
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _runVision(String operation) async {
    if (_imagePath == null || _busy) return;
    setState(() {
      _busy = true;
      _error = null;
      _visionResult = '';
    });
    try {
      final repo = ref.read(studioRepositoryProvider);
      final categories = _imageCategories.text
          .split(',')
          .map((e) => e.trim())
          .where((e) => e.isNotEmpty)
          .toList();
      final result = switch (operation) {
        'ocr' => await repo.ocrVision(_imagePath!),
        'classify' => await repo.classifyVision(_imagePath!, categories),
        'remove' => await repo.removeVisionBackground(_imagePath!),
        _ => await repo.analyzeVision(_imagePath!, _prompt.text),
      };
      if (mounted) setState(() => _visionResult = _pretty(result));
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  String _pretty(Map<String, dynamic> value) {
    if (value.isEmpty) return 'Không có kết quả.';
    return value.entries
        .map((entry) => '${entry.key}: ${entry.value}')
        .join('\n');
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: const Text('AI Tools'),
      bottom: TabBar(
        controller: _tabs,
        tabs: const [
          Tab(text: 'Văn bản'),
          Tab(text: 'Vision / OCR'),
        ],
      ),
    ),
    body: Column(
      children: [
        if (_error != null)
          MaterialBanner(
            content: Text(_error!),
            actions: [
              TextButton(
                onPressed: () => setState(() => _error = null),
                child: const Text('Đóng'),
              ),
            ],
          ),
        Expanded(
          child: TabBarView(
            controller: _tabs,
            children: [_textTab(), _visionTab()],
          ),
        ),
      ],
    ),
  );

  Widget _textTab() => ListView(
    padding: const EdgeInsets.all(16),
    children: [
      const Text(
        'Phân tích văn bản',
        style: TextStyle(fontSize: 20, fontWeight: FontWeight.w700),
      ),
      const SizedBox(height: 6),
      const Text('Cảm xúc, thực thể, phân loại, ngôn ngữ và từ khóa.'),
      const SizedBox(height: 16),
      TextField(
        controller: _text,
        minLines: 6,
        maxLines: 12,
        maxLength: 50000,
        decoration: const InputDecoration(labelText: 'Văn bản đầu vào'),
      ),
      TextField(
        controller: _categories,
        decoration: const InputDecoration(
          labelText: 'Danh mục phân loại (phân tách bằng dấu phẩy)',
        ),
      ),
      const SizedBox(height: 12),
      Wrap(
        spacing: 8,
        runSpacing: 8,
        children: [
          _action('Phân tích', 'analyze'),
          _action('Phân loại', 'classify'),
          _action('Ngôn ngữ', 'language'),
          _action('Từ khóa', 'keywords'),
          _action('Embeddings', 'embeddings'),
        ],
      ),
      if (_textResult.isNotEmpty)
        Card(
          margin: const EdgeInsets.only(top: 16),
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: SelectableText(_textResult),
          ),
        ),
    ],
  );

  Widget _action(String label, String operation) => FilledButton(
    onPressed: _busy ? null : () => _runText(operation),
    child: Text(label),
  );

  Widget _visionTab() => ListView(
    padding: const EdgeInsets.all(16),
    children: [
      const Text(
        'Vision & OCR',
        style: TextStyle(fontSize: 20, fontWeight: FontWeight.w700),
      ),
      const SizedBox(height: 6),
      const Text('Tải ảnh lên, phân tích nội dung hoặc trích xuất văn bản.'),
      const SizedBox(height: 16),
      OutlinedButton.icon(
        onPressed: _busy ? null : _pickImage,
        icon: const Icon(Icons.upload_file),
        label: Text(_imagePath == null ? 'Chọn ảnh' : 'Đã tải ảnh lên'),
      ),
      const SizedBox(height: 12),
      TextField(
        controller: _imageCategories,
        decoration: const InputDecoration(
          labelText: 'Danh mục phân loại ảnh (phân tách bằng dấu phẩy)',
        ),
      ),
      const SizedBox(height: 8),
      TextField(
        controller: _prompt,
        maxLines: 3,
        decoration: const InputDecoration(
          labelText: 'Prompt phân tích hình ảnh',
        ),
      ),
      const SizedBox(height: 12),
      Row(
        children: [
          Expanded(
            child: FilledButton.icon(
              onPressed: _imagePath == null || _busy
                  ? null
                  : () => _runVision('analyze'),
              icon: const Icon(Icons.image_search),
              label: const Text('Phân tích ảnh'),
            ),
          ),
          const SizedBox(width: 8),
          Expanded(
            child: FilledButton.icon(
              onPressed: _imagePath == null || _busy
                  ? null
                  : () => _runVision('ocr'),
              icon: const Icon(Icons.text_snippet),
              label: const Text('OCR'),
            ),
          ),
          const SizedBox(width: 8),
          Expanded(
            child: OutlinedButton.icon(
              onPressed: _imagePath == null || _busy
                  ? null
                  : () => _runVision('classify'),
              icon: const Icon(Icons.category),
              label: const Text('Phân loại'),
            ),
          ),
          const SizedBox(width: 8),
          Expanded(
            child: OutlinedButton.icon(
              onPressed: _imagePath == null || _busy
                  ? null
                  : () => _runVision('remove'),
              icon: const Icon(Icons.auto_fix_high),
              label: const Text('Tách nền'),
            ),
          ),
        ],
      ),
      if (_visionResult.isNotEmpty)
        Card(
          margin: const EdgeInsets.only(top: 16),
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: SelectableText(_visionResult),
          ),
        ),
    ],
  );
}
