import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';
import '../../app/ui/app_controls.dart';
import '../../core/network/api_exception.dart';
import 'canvas_models.dart';
import 'canvas_provider.dart';

/// Canvas artifact của một phiên chat: xem, duyệt version, chèn vào composer,
/// sửa và lưu phiên bản mới.
class CanvasPanelScreen extends ConsumerStatefulWidget {
  const CanvasPanelScreen({
    super.key,
    required this.sessionId,
    this.onInsert,
    this.draft = '',
  });

  final String? sessionId;

  /// Gọi khi người dùng chọn "Chèn vào chat"; nhận nội dung artifact.
  final ValueChanged<String>? onInsert;

  /// Nội dung đang có trong ô soạn tin nhắn, dùng làm giá trị khởi tạo.
  final String draft;

  @override
  ConsumerState<CanvasPanelScreen> createState() => _CanvasPanelScreenState();
}

class _CanvasPanelScreenState extends ConsumerState<CanvasPanelScreen> {
  final _editor = TextEditingController();
  final _newTitle = TextEditingController();

  List<CanvasArtifactModel> _artifacts = const [];
  List<CanvasVersionModel> _versions = const [];
  CanvasArtifactDetailModel? _detail;
  String? _selectedId;
  int? _selectedVersion;
  bool _loading = true;
  bool _busy = false;
  bool _editing = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _loadArtifacts();
  }

  @override
  void dispose() {
    _editor.dispose();
    _newTitle.dispose();
    super.dispose();
  }

  String _message(Object error) =>
      error is ApiException ? error.message : error.toString();

  Future<void> _loadArtifacts() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final artifacts = await ref
          .read(canvasRepositoryProvider)
          .artifacts(sessionId: widget.sessionId);
      if (!mounted) return;
      setState(() => _artifacts = artifacts);
      if (artifacts.isNotEmpty) {
        final keep = artifacts.any((item) => item.rootId == _selectedId);
        await _select(keep ? _selectedId! : artifacts.first.rootId);
      } else {
        setState(() {
          _selectedId = null;
          _detail = null;
          _versions = const [];
        });
      }
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _select(String rootId, {int? version}) async {
    setState(() {
      _selectedId = rootId;
      _selectedVersion = version;
      _editing = false;
    });
    try {
      final repository = ref.read(canvasRepositoryProvider);
      final detail = await repository.artifact(
        rootId: rootId,
        version: version,
      );
      final versions = await repository.versions(rootId);
      if (!mounted || _selectedId != rootId) return;
      setState(() {
        _detail = detail;
        _selectedVersion = detail.version;
        _versions = versions.isEmpty
            ? [CanvasVersionModel(version: detail.version)]
            : versions;
      });
    } catch (error) {
      if (mounted) setState(() => _error = _message(error));
    }
  }

  Future<void> _saveVersion() async {
    final detail = _detail;
    if (detail == null) return;
    if (_editor.text.trim().isEmpty) {
      showAppSnack(context, 'Nội dung không được để trống.');
      return;
    }
    setState(() => _busy = true);
    try {
      await ref
          .read(canvasRepositoryProvider)
          .update(rootId: detail.rootId, content: _editor.text);
      if (!mounted) return;
      setState(() => _editing = false);
      await _select(detail.rootId);
      await _loadArtifacts();
      if (mounted) showAppSnack(context, 'Đã lưu phiên bản mới.');
    } catch (error) {
      if (mounted) showAppSnack(context, _message(error));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _createArtifact() async {
    _newTitle.clear();
    _editor.text = widget.draft;
    final created = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Tạo artifact mới'),
        content: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              AppTextField(
                controller: _newTitle,
                label: 'Tiêu đề',
                hint: 'Ví dụ: Bản nháp email',
              ),
              AppTextField(controller: _editor, label: 'Nội dung', maxLines: 6),
            ],
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Huỷ'),
          ),
          AppPrimaryButton(
            label: 'Tạo',
            onPressed: () => Navigator.pop(context, true),
            expand: false,
          ),
        ],
      ),
    );
    if (created != true) return;

    setState(() => _busy = true);
    try {
      await ref
          .read(canvasRepositoryProvider)
          .create(
            content: _editor.text,
            sessionId: widget.sessionId,
            title: _newTitle.text.trim().isEmpty
                ? 'Artifact mới'
                : _newTitle.text.trim(),
            kind: 'text',
          );
      await _loadArtifacts();
      if (mounted) showAppSnack(context, 'Đã tạo artifact.');
    } catch (error) {
      if (mounted) showAppSnack(context, _message(error));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _delete() async {
    final detail = _detail;
    if (detail == null) return;
    final confirmed = await confirmAppAction(
      context,
      title: 'Xoá artifact',
      message: 'Xoá “${detail.title}” và toàn bộ phiên bản của nó?',
    );
    if (!confirmed) return;
    try {
      await ref.read(canvasRepositoryProvider).delete(detail.rootId);
      await _loadArtifacts();
      if (mounted) showAppSnack(context, 'Đã xoá artifact.');
    } catch (error) {
      if (mounted) showAppSnack(context, _message(error));
    }
  }

  void _insert() {
    final detail = _detail;
    if (detail == null) return;
    final onInsert = widget.onInsert;
    if (onInsert == null) {
      showAppSnack(context, 'Canvas này chỉ để xem.');
      return;
    }
    onInsert(detail.content);
    Navigator.of(context).pop();
  }

  @override
  Widget build(BuildContext context) {
    final detail = _detail;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Canvas'),
        actions: [
          IconButton(
            tooltip: 'Làm mới',
            onPressed: _loading ? null : _loadArtifacts,
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: _busy ? null : _createArtifact,
        icon: const Icon(Icons.add),
        label: const Text('Artifact mới'),
      ),
      body: _loading
          ? const Center(child: CircularProgressIndicator())
          : RefreshIndicator(
              onRefresh: _loadArtifacts,
              child: ListView(
                padding: const EdgeInsets.all(16),
                children: [
                  if (_error != null)
                    AppAlert(
                      message: _error!,
                      isError: true,
                      onRetry: _loadArtifacts,
                    ),
                  if (_artifacts.isEmpty)
                    const Padding(
                      padding: EdgeInsets.symmetric(vertical: 48),
                      child: Center(
                        child: Text(
                          'Phiên chat này chưa có artifact nào.\n'
                          'Agent sẽ tạo artifact khi bạn yêu cầu soạn thảo nội dung dài.',
                          textAlign: TextAlign.center,
                        ),
                      ),
                    )
                  else ...[
                    const AppSectionTitle(
                      title: 'Artifact trong phiên này',
                      subtitle: 'Chọn để xem nội dung và lịch sử phiên bản.',
                    ),
                    Wrap(
                      spacing: 8,
                      runSpacing: 8,
                      children: [
                        for (final artifact in _artifacts)
                          ChoiceChip(
                            selected: artifact.rootId == _selectedId,
                            label: Text(
                              '${artifact.title} · v${artifact.version}',
                            ),
                            onSelected: (_) => _select(artifact.rootId),
                          ),
                      ],
                    ),
                    const SizedBox(height: 16),
                  ],
                  if (detail != null) ...[
                    AppCard(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          AppSectionTitle(
                            title: detail.title,
                            subtitle:
                                'Loại ${detail.kind}'
                                '${detail.language == null ? '' : ' · ${detail.language}'}',
                          ),
                          if (_versions.length > 1)
                            DropdownButtonFormField<int>(
                              initialValue: _selectedVersion,
                              isExpanded: true,
                              decoration: const InputDecoration(
                                labelText: 'Phiên bản',
                              ),
                              items: [
                                for (final version in _versions.reversed)
                                  DropdownMenuItem(
                                    value: version.version,
                                    child: Text('v${version.version}'),
                                  ),
                              ],
                              onChanged: (value) {
                                if (value == null) return;
                                _select(detail.rootId, version: value);
                              },
                            ),
                          const SizedBox(height: 12),
                          if (_editing)
                            AppTextField(
                              controller: _editor,
                              label: 'Nội dung',
                              maxLines: 12,
                            )
                          else
                            Container(
                              width: double.infinity,
                              padding: const EdgeInsets.all(12),
                              decoration: BoxDecoration(
                                color: AppTheme.surfaceMuted,
                                borderRadius: BorderRadius.circular(
                                  AppTheme.radiusSmall,
                                ),
                              ),
                              child: SelectableText(
                                detail.content.isEmpty
                                    ? '(Nội dung trống)'
                                    : detail.content,
                                style: const TextStyle(
                                  fontFamily: AppTheme.monoFamily,
                                  fontFamilyFallback: AppTheme.monoFallback,
                                  fontSize: 12,
                                  height: 1.45,
                                ),
                              ),
                            ),
                          const SizedBox(height: 16),
                          Wrap(
                            spacing: 8,
                            runSpacing: 8,
                            children: [
                              if (_editing) ...[
                                AppPrimaryButton(
                                  label: 'Lưu phiên bản mới',
                                  icon: Icons.save_outlined,
                                  busy: _busy,
                                  expand: false,
                                  onPressed: _saveVersion,
                                ),
                                AppSecondaryButton(
                                  label: 'Huỷ',
                                  onPressed: () =>
                                      setState(() => _editing = false),
                                ),
                              ] else ...[
                                AppPrimaryButton(
                                  label: 'Chèn vào chat',
                                  icon: Icons.input,
                                  expand: false,
                                  onPressed: _insert,
                                ),
                                AppSecondaryButton(
                                  label: 'Sửa',
                                  icon: Icons.edit_outlined,
                                  onPressed: () {
                                    _editor.text = detail.content;
                                    setState(() => _editing = true);
                                  },
                                ),
                                AppSecondaryButton(
                                  label: 'Sao chép',
                                  icon: Icons.copy_all_outlined,
                                  onPressed: () async {
                                    await Clipboard.setData(
                                      ClipboardData(text: detail.content),
                                    );
                                    if (context.mounted) {
                                      showAppSnack(
                                        context,
                                        'Đã sao chép nội dung artifact.',
                                      );
                                    }
                                  },
                                ),
                              ],
                            ],
                          ),
                          if (!_editing)
                            Padding(
                              padding: const EdgeInsets.only(top: 8),
                              child: Align(
                                alignment: Alignment.centerLeft,
                                // Hành động không hoàn tác được: dùng đúng sắc
                                // đỏ nguy hiểm thay vì TextButton xanh mặc định.
                                child: TextButton.icon(
                                  onPressed: _delete,
                                  style: TextButton.styleFrom(
                                    foregroundColor: AppTheme.govRed,
                                  ),
                                  icon: const Icon(Icons.delete_outline),
                                  label: const Text('Xoá artifact'),
                                ),
                              ),
                            ),
                        ],
                      ),
                    ),
                  ],
                ],
              ),
            ),
    );
  }
}
