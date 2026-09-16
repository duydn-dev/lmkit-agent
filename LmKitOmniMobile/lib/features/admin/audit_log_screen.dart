import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/ui/app_controls.dart';
import 'admin_models.dart';
import 'admin_provider.dart';
import 'admin_repository.dart';
import 'admin_widgets.dart';

/// Nhật ký kiểm toán của tenant: lọc theo actor/action/entity và khoảng thời gian.
///
/// Tương ứng `AuditLogView.vue` của desktop, gồm cả `GET /api/audit/facets` để
/// gợi ý giá trị lọc đã từng xuất hiện. Backend luôn giới hạn theo tenant của
/// người đang đăng nhập nên không cần tham số tenant ở client.
class AuditLogScreen extends ConsumerStatefulWidget {
  const AuditLogScreen({super.key});

  @override
  ConsumerState<AuditLogScreen> createState() => _AuditLogScreenState();
}

class _AuditLogScreenState extends ConsumerState<AuditLogScreen> {
  static const _pageSize = 25;

  AuditFacetsModel _facets = const AuditFacetsModel();
  PagedResult<AuditLogEntryModel> _page = const PagedResult();
  String? _actorType;
  String? _action;
  String? _entityType;
  DateTime? _from;
  DateTime? _to;
  bool _loading = true;
  bool _loadingMore = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _loadFacets();
    _load();
  }

  Future<void> _loadFacets() async {
    try {
      final facets = await ref.read(adminRepositoryProvider).auditFacets();
      if (mounted) setState(() => _facets = facets);
    } catch (_) {
      // Facets chỉ là gợi ý lọc; lỗi ở đây không chặn màn chính.
    }
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final page = await ref
          .read(adminRepositoryProvider)
          .auditLogs(
            actorType: _actorType,
            action: _action,
            entityType: _entityType,
            fromUtc: _from,
            toUtc: _to,
            pageSize: _pageSize,
          );
      if (mounted) setState(() => _page = page);
    } catch (error) {
      if (mounted) setState(() => _error = adminErrorMessage(error));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _loadMore() async {
    if (_loadingMore || !_page.hasMore) return;
    setState(() => _loadingMore = true);
    try {
      final next = await ref
          .read(adminRepositoryProvider)
          .auditLogs(
            actorType: _actorType,
            action: _action,
            entityType: _entityType,
            fromUtc: _from,
            toUtc: _to,
            page: _page.page + 1,
            pageSize: _pageSize,
          );
      if (mounted) setState(() => _page = _page.append(next));
    } catch (error) {
      if (mounted) showAdminSnack(context, adminErrorMessage(error));
    } finally {
      if (mounted) setState(() => _loadingMore = false);
    }
  }

  bool get _hasFilters =>
      _actorType != null ||
      _action != null ||
      _entityType != null ||
      _from != null ||
      _to != null;

  void _clearFilters() {
    setState(() {
      _actorType = null;
      _action = null;
      _entityType = null;
      _from = null;
      _to = null;
    });
    _load();
  }

  Future<void> _pickValue({
    required String title,
    required List<String> options,
    required String? current,
    required ValueChanged<String?> onSelected,
  }) async {
    final picked = await showAppPicker<String>(
      context,
      title: title,
      items: options,
      labelOf: (value) => value,
    );
    if (picked != null) {
      onSelected(picked);
      await _load();
    }
  }

  Future<void> _pickDate({required bool isFrom}) async {
    final initial = (isFrom ? _from : _to) ?? DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: initial,
      firstDate: DateTime(2024),
      lastDate: DateTime.now().add(const Duration(days: 1)),
    );
    if (picked == null) return;
    setState(() {
      if (isFrom) {
        _from = DateTime(picked.year, picked.month, picked.day);
      } else {
        // Bao trùm cả ngày đã chọn: cận trên là 23:59:59 của ngày đó.
        _to = DateTime(picked.year, picked.month, picked.day, 23, 59, 59);
      }
    });
    await _load();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppTopBar(
      title: const Text('Nhật ký kiểm toán'),
      actions: [
        IconButton(
          tooltip: 'Làm mới',
          onPressed: _load,
          icon: const Icon(Icons.refresh),
        ),
        IconButton(
          tooltip: 'Bộ lọc',
          onPressed: _openFilters,
          icon: Icon(
            _hasFilters ? Icons.filter_alt : Icons.filter_alt_outlined,
          ),
        ),
      ],
    ),
    body: RefreshIndicator(
      onRefresh: _load,
      child: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          Text(
            _page.totalCount == 0
                ? 'Chưa có bản ghi nào.'
                : '${_page.totalCount} bản ghi'
                      '${_hasFilters ? ' khớp bộ lọc' : ''}',
            style: Theme.of(context).textTheme.bodySmall,
          ),
          const SizedBox(height: 12),
          if (_hasFilters) _filterChips(),
          if (_error != null)
            AdminBanner(message: _error!, isError: true, onRetry: _load),
          if (_loading)
            const Padding(
              padding: EdgeInsets.all(40),
              child: Center(child: CircularProgressIndicator()),
            )
          else if (_page.items.isEmpty)
            const Padding(
              padding: EdgeInsets.all(40),
              child: Center(child: Text('Không có bản ghi phù hợp.')),
            )
          else ...[
            for (final entry in _page.items)
              _AuditEntryCard(
                entry: entry,
                onFilterBy: (value) => _applyFacetFrom(entry, value),
              ),
            if (_page.hasMore)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: AppPrimaryButton(
                  label: 'Tải thêm',
                  busy: _loadingMore,
                  onPressed: _loadMore,
                ),
              ),
          ],
        ],
      ),
    ),
  );

  /// Bấm vào một nhãn trong bản ghi để lọc nhanh theo chính giá trị đó.
  Future<void> _applyFacetFrom(AuditLogEntryModel entry, String field) async {
    switch (field) {
      case 'actorType':
        setState(() => _actorType = entry.actorType);
      case 'action':
        setState(() => _action = entry.action);
      case 'entityType':
        setState(() => _entityType = entry.entityType);
    }
    await _load();
  }

  Widget _filterChips() => Padding(
    padding: const EdgeInsets.only(bottom: 12),
    child: Wrap(
      spacing: 8,
      runSpacing: 8,
      children: [
        if (_actorType != null)
          _Chip(
            label: 'Actor: $_actorType',
            onClear: () {
              setState(() => _actorType = null);
              _load();
            },
          ),
        if (_action != null)
          _Chip(
            label: 'Hành động: $_action',
            onClear: () {
              setState(() => _action = null);
              _load();
            },
          ),
        if (_entityType != null)
          _Chip(
            label: 'Đối tượng: $_entityType',
            onClear: () {
              setState(() => _entityType = null);
              _load();
            },
          ),
        if (_from != null)
          _Chip(
            label: 'Từ ${_shortDate(_from!)}',
            onClear: () {
              setState(() => _from = null);
              _load();
            },
          ),
        if (_to != null)
          _Chip(
            label: 'Đến ${_shortDate(_to!)}',
            onClear: () {
              setState(() => _to = null);
              _load();
            },
          ),
        _Chip(label: 'Xoá tất cả', onClear: _clearFilters, clearIcon: true),
      ],
    ),
  );

  Future<void> _openFilters() => showModalBottomSheet<void>(
    context: context,
    showDragHandle: true,
    isScrollControlled: true,
    builder: (sheetContext) => ListView(
      shrinkWrap: true,
      padding: const EdgeInsets.fromLTRB(16, 0, 16, 24),
      children: [
        Text(
          'Bộ lọc audit log',
          style: Theme.of(sheetContext).textTheme.titleLarge,
        ),
        const SizedBox(height: 8),
        for (final entry in [
          (
            label: 'Loại actor',
            value: _actorType,
            options: _facets.actorTypes,
            hint: 'Phân loại người/hệ thống gây ra hành động',
            apply: (String? value) => setState(() => _actorType = value),
          ),
          (
            label: 'Hành động',
            value: _action,
            options: _facets.actions,
            hint: 'Ví dụ AI.Tool.Invoke',
            apply: (String? value) => setState(() => _action = value),
          ),
          (
            label: 'Loại đối tượng',
            value: _entityType,
            options: _facets.entityTypes,
            hint: 'Tên công cụ hoặc thực thể bị tác động',
            apply: (String? value) => setState(() => _entityType = value),
          ),
        ])
          ListTile(
            contentPadding: EdgeInsets.zero,
            title: Text(entry.label),
            subtitle: Text(
              entry.value ??
                  (entry.options.isEmpty ? 'Chưa có gợi ý' : entry.hint),
            ),
            trailing: const Icon(Icons.chevron_right),
            enabled: entry.options.isNotEmpty,
            onTap: entry.options.isEmpty
                ? null
                : () async {
                    Navigator.pop(sheetContext);
                    await _pickValue(
                      title: entry.label,
                      options: entry.options,
                      current: entry.value,
                      onSelected: entry.apply,
                    );
                  },
          ),
        const Divider(),
        ListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('Từ ngày'),
          subtitle: Text(_from == null ? 'Không giới hạn' : _shortDate(_from!)),
          trailing: const Icon(Icons.calendar_today_outlined),
          onTap: () async {
            Navigator.pop(sheetContext);
            await _pickDate(isFrom: true);
          },
        ),
        ListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('Đến ngày'),
          subtitle: Text(_to == null ? 'Không giới hạn' : _shortDate(_to!)),
          trailing: const Icon(Icons.event_available_outlined),
          onTap: () async {
            Navigator.pop(sheetContext);
            await _pickDate(isFrom: false);
          },
        ),
        const SizedBox(height: 8),
        Row(
          children: [
            Expanded(
              child: AppSecondaryButton(
                label: 'Xoá lọc',
                icon: Icons.filter_alt_off_outlined,
                onPressed: () {
                  Navigator.pop(sheetContext);
                  _clearFilters();
                },
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: AppPrimaryButton(
                label: 'Áp dụng',
                onPressed: () {
                  Navigator.pop(sheetContext);
                  _load();
                },
              ),
            ),
          ],
        ),
      ],
    ),
  );

  static String _shortDate(DateTime value) =>
      '${value.day.toString().padLeft(2, '0')}/'
      '${value.month.toString().padLeft(2, '0')}/${value.year}';
}

class _Chip extends StatelessWidget {
  const _Chip({
    required this.label,
    required this.onClear,
    this.clearIcon = false,
  });

  final String label;
  final VoidCallback onClear;
  final bool clearIcon;

  @override
  Widget build(BuildContext context) => InputChip(
    label: Text(label),
    onDeleted: onClear,
    deleteIcon: Icon(clearIcon ? Icons.close : Icons.clear, size: 16),
  );
}

class _AuditEntryCard extends StatelessWidget {
  const _AuditEntryCard({required this.entry, required this.onFilterBy});

  final AuditLogEntryModel entry;
  final Future<void> Function(String field) onFilterBy;

  @override
  Widget build(BuildContext context) => AppCard(
    onTap: entry.detailsJson == null ? null : () => _showDetails(context),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          entry.action.isEmpty ? '(không rõ hành động)' : entry.action,
          style: Theme.of(context).textTheme.titleMedium,
        ),
        const SizedBox(height: 4),
        Text(
          '${entry.actorType} • ${entry.entityType}',
          style: Theme.of(context).textTheme.bodySmall,
        ),
        if (entry.createdAtUtc != null)
          Text(
            _timestamp(entry.createdAtUtc!),
            style: Theme.of(context).textTheme.bodySmall,
          ),
        const SizedBox(height: 8),
        Wrap(
          spacing: 6,
          runSpacing: 6,
          children: [
            if (entry.actorType.isNotEmpty)
              ActionChip(
                label: Text('Lọc actor'),
                avatar: const Icon(Icons.person_outline, size: 16),
                onPressed: () => onFilterBy('actorType'),
              ),
            if (entry.action.isNotEmpty)
              ActionChip(
                label: const Text('Lọc hành động'),
                avatar: const Icon(Icons.bolt_outlined, size: 16),
                onPressed: () => onFilterBy('action'),
              ),
            if (entry.entityType.isNotEmpty)
              ActionChip(
                label: const Text('Lọc đối tượng'),
                avatar: const Icon(Icons.category_outlined, size: 16),
                onPressed: () => onFilterBy('entityType'),
              ),
            if (entry.detailsJson != null)
              ActionChip(
                label: const Text('Chi tiết'),
                avatar: const Icon(Icons.data_object, size: 16),
                onPressed: () => _showDetails(context),
              ),
          ],
        ),
        if (entry.correlationId != null)
          Padding(
            padding: const EdgeInsets.only(top: 6),
            child: Text(
              'correlation: ${entry.correlationId}',
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ),
      ],
    ),
  );

  void _showDetails(BuildContext context) => showDialog<void>(
    context: context,
    builder: (_) => AlertDialog(
      title: Text(entry.action),
      content: SingleChildScrollView(
        child: SelectableText(_prettyDetails(entry.detailsJson!)),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context),
          child: const Text('Đóng'),
        ),
      ],
    ),
  );

  /// `detailsJson` là JSON thô; hiển thị đúng nguyên văn nếu không parse được.
  static String _prettyDetails(String raw) {
    final trimmed = raw.trim();
    if (trimmed.isEmpty) return '(rỗng)';
    final buffer = StringBuffer();
    var depth = 0;
    var inString = false;
    for (var i = 0; i < trimmed.length; i++) {
      final char = trimmed[i];
      if (char == '"' && (i == 0 || trimmed[i - 1] != r'\')) {
        inString = !inString;
      }
      if (!inString && (char == '{' || char == '[')) {
        depth++;
        buffer.write('$char\n${'  ' * depth}');
        continue;
      }
      if (!inString && (char == '}' || char == ']')) {
        depth = depth > 0 ? depth - 1 : 0;
        buffer.write('\n${'  ' * depth}$char');
        continue;
      }
      if (!inString && char == ',') {
        buffer.write(',\n${'  ' * depth}');
        continue;
      }
      buffer.write(char);
    }
    return buffer.toString();
  }

  static String _timestamp(DateTime value) {
    final local = value.toLocal();
    String two(int number) => number.toString().padLeft(2, '0');
    return '${two(local.day)}/${two(local.month)}/${local.year} '
        '${two(local.hour)}:${two(local.minute)}:${two(local.second)}';
  }
}
