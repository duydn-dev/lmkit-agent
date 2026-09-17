import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';

import '../../app/ui/app_controls.dart';
import '../../core/network/api_exception.dart';
import 'workspace_models.dart';
import 'workspace_provider.dart';

class DocumentsScreen extends ConsumerStatefulWidget {
  const DocumentsScreen({super.key});

  @override
  ConsumerState<DocumentsScreen> createState() => _DocumentsScreenState();
}

class _DocumentsScreenState extends ConsumerState<DocumentsScreen> {
  final _search = TextEditingController();
  List<DocumentModel> _documents = const [];
  bool _loading = true;
  bool _uploading = false;
  bool _grid = false;
  String _query = '';
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  /// Lọc phía client: `GET /api/document` không nhận tham số tìm kiếm.
  List<DocumentModel> get _visible {
    if (_query.isEmpty) return _documents;
    final needle = _query.toLowerCase();
    return _documents
        .where((document) => document.fileName.toLowerCase().contains(needle))
        .toList();
  }

  Future<void> _load() async {
    setState(() => _loading = true);
    try {
      final documents = await ref.read(workspaceRepositoryProvider).documents();
      if (mounted) {
        setState(() {
          _documents = documents;
          _error = null;
        });
      }
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    } finally {
      if (mounted) setState(() => _loading = false);
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
      if (mounted) {
        showAppSnack(context, 'File vượt quá dung lượng tối đa 50MB.');
      }
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
      if (mounted) setState(() => _error = _message(error));
    } finally {
      if (mounted) setState(() => _uploading = false);
    }
  }

  Future<void> _delete(DocumentModel document) async {
    final confirmed = await confirmAppAction(
      context,
      title: 'Xóa tài liệu',
      message: 'Xóa “${document.fileName}” khỏi kho RAG?',
    );
    if (!confirmed) return;
    try {
      await ref.read(workspaceRepositoryProvider).deleteDocument(document.id);
      await _load();
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    }
  }

  String _message(Object error) =>
      error is ApiException ? error.message : error.toString();

  @override
  Widget build(BuildContext context) {
    final visible = _visible;
    final texts = Theme.of(context).textTheme;

    return Scaffold(
      appBar: AppTopBar(
        title: const Text('RAG Documents'),
        actions: [
          AppIconButton(
            icon: _grid ? Icons.view_list_outlined : Icons.grid_view,
            tooltip: _grid ? 'Xem dạng danh sách' : 'Xem dạng lưới',
            onPressed: () => setState(() => _grid = !_grid),
          ),
          AppIconButton(
            icon: Icons.cloud_upload_outlined,
            tooltip: 'Tải tài liệu lên',
            onPressed: _pickAndUpload,
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            AppTextField(
              controller: _search,
              label: 'Tìm theo tên tài liệu',
              onChanged: (value) => setState(() => _query = value.trim()),
            ),
            if (_error != null)
              AppAlert(message: _error!, isError: true, onRetry: _load),
            if (_uploading) const LinearProgressIndicator(),
            if (_loading)
              const Padding(
                padding: EdgeInsets.all(40),
                child: Center(child: CircularProgressIndicator()),
              )
            else if (visible.isEmpty)
              AppEmptyState(
                icon: _documents.isEmpty
                    ? Icons.description_outlined
                    : Icons.search_off,
                message: _documents.isEmpty
                    ? 'Chưa có tài liệu nào.'
                    : 'Không có tài liệu khớp từ khoá.',
                hint: _documents.isEmpty
                    ? 'Nạp tài liệu bằng nút bên dưới để trợ lý tra cứu được nội dung.'
                    : 'Thử từ khoá ngắn hơn hoặc xoá bộ lọc.',
              )
            else ...[
              Text(
                '${visible.length}${visible.length == _documents.length ? '' : '/${_documents.length}'} tài liệu',
                style: Theme.of(context).textTheme.titleMedium,
              ),
              const SizedBox(height: 8),
              if (_grid)
                GridView.count(
                  crossAxisCount: 2,
                  shrinkWrap: true,
                  physics: const NeverScrollableScrollPhysics(),
                  mainAxisSpacing: 12,
                  crossAxisSpacing: 12,
                  childAspectRatio: 1.05,
                  children: [
                    for (final document in visible)
                      _DocumentGridTile(
                        document: document,
                        onDelete: () => _delete(document),
                      ),
                  ],
                )
              else
                for (final document in visible)
                  AppCard(
                    padding: EdgeInsets.zero,
                    child: AppTileRaw(
                      child: Padding(
                        padding: const EdgeInsets.fromLTRB(16, 12, 8, 12),
                        child: Row(
                          children: [
                            Icon(_icon(document.fileName)),
                            const SizedBox(width: 12),
                            Expanded(
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  Text(
                                    document.fileName,
                                    maxLines: 2,
                                    overflow: TextOverflow.ellipsis,
                                    style: texts.titleSmall,
                                  ),
                                  const SizedBox(height: 2),
                                  Text(
                                    _status(document),
                                    style: texts.bodySmall,
                                  ),
                                ],
                              ),
                            ),
                            AppIconButton(
                              icon: Icons.delete_outline,
                              tooltip: 'Xóa tài liệu',
                              onPressed: () => _delete(document),
                            ),
                          ],
                        ),
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
  }

  IconData _icon(String name) => name.toLowerCase().endsWith('.pdf')
      ? Icons.picture_as_pdf
      : Icons.description_outlined;

  String _status(DocumentModel document) => document.isVectorized
      ? 'Hoàn tất vector hóa'
      : document.vectorizationStatus == 'Failed' || document.hasError
      ? 'Xử lý lỗi'
      : 'Đang xử lý';
}

class _DocumentGridTile extends StatelessWidget {
  const _DocumentGridTile({required this.document, required this.onDelete});

  final DocumentModel document;
  final VoidCallback onDelete;

  @override
  Widget build(BuildContext context) {
    final failed =
        document.vectorizationStatus == 'Failed' || document.hasError;
    return GestureDetector(
      onLongPress: onDelete,
      child: AppCard(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(
                  document.fileName.toLowerCase().endsWith('.pdf')
                      ? Icons.picture_as_pdf
                      : Icons.description_outlined,
                ),
                const Spacer(),
                AppIconButton(
                  icon: Icons.delete_outline,
                  tooltip: 'Xóa tài liệu',
                  onPressed: onDelete,
                ),
              ],
            ),
            Expanded(
              child: Text(
                document.fileName,
                maxLines: 3,
                overflow: TextOverflow.ellipsis,
                style: const TextStyle(fontWeight: FontWeight.w600),
              ),
            ),
            Text(
              document.isVectorized
                  ? 'Đã vector hóa'
                  : failed
                  ? 'Xử lý lỗi'
                  : 'Đang xử lý',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                color: document.isVectorized
                    ? AppTheme.success
                    : failed
                    ? AppTheme.dangerText
                    : AppTheme.textMuted,
              ),
            ),
          ],
        ),
      ),
    );
  }
}
