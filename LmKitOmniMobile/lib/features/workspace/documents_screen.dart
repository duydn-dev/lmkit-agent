import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/network/api_exception.dart';
import 'workspace_models.dart';
import 'workspace_provider.dart';

class DocumentsScreen extends ConsumerStatefulWidget {
  const DocumentsScreen({super.key});

  @override
  ConsumerState<DocumentsScreen> createState() => _DocumentsScreenState();
}

class _DocumentsScreenState extends ConsumerState<DocumentsScreen> {
  List<DocumentModel> _documents = const [];
  bool _loading = true;
  bool _uploading = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() => _loading = true);
    try {
      final documents = await ref.read(workspaceRepositoryProvider).documents();
      if (mounted) {
        setState(() => _documents = documents);
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

  Future<void> _pickAndUpload() async {
    final file = await FilePicker.pickFile(
      type: FileType.custom,
      allowedExtensions: const [
        'pdf',
        'doc',
        'docx',
        'xls',
        'xlsx',
        'ppt',
        'pptx',
        'txt',
        'md',
      ],
    );
    if (file == null) {
      return;
    }
    final size = file.lengthSync() ?? await file.length() ?? 0;
    if (size > 50 * 1024 * 1024) {
      setState(() => _error = 'File vượt quá dung lượng tối đa 50MB.');
      return;
    }
    setState(() {
      _uploading = true;
      _error = null;
    });
    try {
      await ref.read(workspaceRepositoryProvider).uploadDocument(file);
      await _load();
    } catch (error) {
      if (mounted) {
        setState(() => _error = _message(error));
      }
    } finally {
      if (mounted) {
        setState(() => _uploading = false);
      }
    }
  }

  Future<void> _delete(DocumentModel document) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Xóa tài liệu'),
        content: Text('Xóa “${document.fileName}” khỏi kho RAG?'),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Hủy'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Xóa'),
          ),
        ],
      ),
    );
    if (confirmed != true) {
      return;
    }
    try {
      await ref.read(workspaceRepositoryProvider).deleteDocument(document.id);
      await _load();
    } catch (error) {
      if (mounted) {
        setState(() => _error = _message(error));
      }
    }
  }

  String _message(Object error) =>
      error is ApiException ? error.message : error.toString();

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: const Text('RAG Documents'),
      actions: [
        IconButton(
          onPressed: _pickAndUpload,
          tooltip: 'Tải tài liệu lên',
          icon: const Icon(Icons.cloud_upload_outlined),
        ),
      ],
    ),
    body: RefreshIndicator(
      onRefresh: _load,
      child: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          if (_error != null)
            Card(
              color: Theme.of(context).colorScheme.errorContainer,
              child: ListTile(
                title: Text(_error!),
                trailing: IconButton(
                  onPressed: () => setState(() => _error = null),
                  icon: const Icon(Icons.close),
                ),
              ),
            ),
          if (_uploading) const LinearProgressIndicator(),
          if (_loading)
            const Padding(
              padding: EdgeInsets.all(40),
              child: Center(child: CircularProgressIndicator()),
            )
          else if (_documents.isEmpty)
            const Padding(
              padding: EdgeInsets.all(40),
              child: Center(child: Text('Chưa có tài liệu nào.')),
            )
          else ...[
            Text(
              '${_documents.length} tài liệu',
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 8),
            for (final document in _documents)
              Card(
                child: ListTile(
                  leading: Icon(_icon(document.fileName)),
                  title: Text(
                    document.fileName,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                  ),
                  subtitle: Text(_status(document)),
                  trailing: IconButton(
                    onPressed: () => _delete(document),
                    tooltip: 'Xóa tài liệu',
                    icon: const Icon(Icons.delete_outline),
                  ),
                ),
              ),
          ],
        ],
      ),
    ),
    floatingActionButton: FloatingActionButton(
      onPressed: _uploading ? null : _pickAndUpload,
      child: const Icon(Icons.upload_file),
    ),
  );

  IconData _icon(String name) => name.toLowerCase().endsWith('.pdf')
      ? Icons.picture_as_pdf
      : Icons.description_outlined;

  String _status(DocumentModel document) => document.isVectorized
      ? 'Hoàn tất vector hóa'
      : document.vectorizationStatus == 'Failed' || document.hasError
      ? 'Xử lý lỗi'
      : 'Đang xử lý';
}
