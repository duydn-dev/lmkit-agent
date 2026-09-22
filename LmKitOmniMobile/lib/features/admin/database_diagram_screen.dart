import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';
import '../../app/ui/app_controls.dart';
import 'admin_models.dart';
import 'admin_provider.dart';
import 'admin_repository.dart';
import 'database_diagram.dart';

/// Sơ đồ quan hệ (ER) của một kết nối CSDL ngoài.
///
/// Mobile không render Mermaid được, nên sơ đồ được vẽ trực tiếp bằng Flutter từ
/// **cùng dữ liệu** mà web dùng (`/schema` trả về bảng/cột/khoá + cạnh FK): thẻ
/// bảng là widget thật (chọn/đọc được bằng trình đọc màn hình), đường quan hệ do
/// [SchemaDiagramLayout] tính và vẽ bằng `CustomPaint`. Toàn bộ khung nằm trong
/// `InteractiveViewer` — sơ đồ ER vốn rộng hơn màn hình điện thoại.
class DatabaseDiagramScreen extends ConsumerStatefulWidget {
  const DatabaseDiagramScreen({
    super.key,
    required this.connectionId,
    this.connectionName,
  });

  final String connectionId;
  final String? connectionName;

  @override
  ConsumerState<DatabaseDiagramScreen> createState() =>
      _DatabaseDiagramScreenState();
}

class _DatabaseDiagramScreenState extends ConsumerState<DatabaseDiagramScreen> {
  DatabaseSchemaModel? _schema;
  bool _loading = true;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    if (!mounted) return;
    setState(() {
      _loading = _schema == null;
      _error = null;
    });
    try {
      final schema = await ref
          .read(adminRepositoryProvider)
          .databaseSchema(widget.connectionId);
      if (mounted) setState(() => _schema = schema);
    } catch (error) {
      if (mounted) setState(() => _error = adminErrorMessage(error));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppTopBar(
        title: Text(widget.connectionName ?? 'Sơ đồ CSDL'),
        actions: [
          AppIconButton(
            icon: Icons.refresh,
            tooltip: 'Đọc lại schema',
            onPressed: _load,
            busy: _loading,
          ),
        ],
      ),
      body: _body(),
    );
  }

  Widget _body() {
    final schema = _schema;
    if (schema != null) return _SchemaView(schema: schema);
    if (_loading) return const Center(child: CircularProgressIndicator());
    if (_error != null) {
      return Padding(
        padding: const EdgeInsets.all(16),
        child: AppAlert(message: _error!, isError: true, onRetry: _load),
      );
    }
    return const SizedBox.shrink();
  }
}

class _SchemaView extends StatelessWidget {
  const _SchemaView({required this.schema});

  final DatabaseSchemaModel schema;

  @override
  Widget build(BuildContext context) {
    final texts = Theme.of(context).textTheme;
    // Thẻ cao theo cỡ chữ hệ thống: tăng cỡ chữ mà giữ nguyên chiều cao thẻ thì
    // dòng cột sẽ tràn khỏi thẻ.
    final layout = SchemaDiagramLayout.compute(
      schema,
      textScale: SchemaDiagramLayout.textScaleOf(context),
    );
    final notes = schemaDiagramNotes(schema);

    // Phần đầu/cuối được chặn trần chiều cao và tự cuộn bên trong: ở cỡ chữ hệ
    // thống lớn (1.3× trở lên) chúng cao lên, và nếu không chặn thì `Column` tổng
    // sẽ tràn — trong khi khối sơ đồ chỉ cần `Expanded` co lại là đủ.
    final viewport = MediaQuery.sizeOf(context).height;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        ConstrainedBox(
          constraints: BoxConstraints(maxHeight: viewport * 0.45),
          child: SingleChildScrollView(
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 8),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    crossAxisAlignment: WrapCrossAlignment.center,
                    children: [
                      _MetaChip(
                        icon: Icons.storage,
                        label: schema.provider.isEmpty
                            ? 'Không rõ'
                            : schema.provider,
                      ),
                      _MetaChip(
                        icon: Icons.table_chart_outlined,
                        label: '${schema.tableCount} bảng',
                      ),
                      _MetaChip(
                        icon: Icons.link,
                        label: '${schema.relations.length} quan hệ khoá ngoại',
                      ),
                      _MetaChip(
                        icon: schema.isIndexed
                            ? Icons.verified_outlined
                            : Icons.pending_outlined,
                        label: schema.isIndexed
                            ? 'Đã đánh chỉ mục'
                            : 'Chưa đánh chỉ mục',
                        color: schema.isIndexed
                            ? AppTheme.success
                            : AppTheme.warningText,
                      ),
                    ],
                  ),
                  if (notes.isNotEmpty) ...[
                    const SizedBox(height: 10),
                    AppAlert(message: notes.join('\n')),
                  ],
                ],
              ),
            ),
          ),
        ),
        Expanded(
          child: layout.cards.isEmpty
              ? const AppEmptyState(
                  message: 'CSDL này không có bảng nào để vẽ.',
                  icon: Icons.schema_outlined,
                )
              : InteractiveViewer(
                  // Sơ đồ rộng hơn màn hình là bình thường: kéo để xem, chụm để
                  // zoom. `boundaryMargin` cho phép kéo quá mép một chút để đọc
                  // thẻ nằm sát biên.
                  constrained: false,
                  minScale: 0.35,
                  maxScale: 2.5,
                  boundaryMargin: const EdgeInsets.all(240),
                  child: SizedBox(
                    width: layout.size.width,
                    height: layout.size.height,
                    child: Stack(
                      children: [
                        Positioned.fill(
                          child: CustomPaint(
                            painter: _RelationPainter(
                              edges: layout.edges,
                              color: AppTheme.borderStrong,
                            ),
                          ),
                        ),
                        for (final card in layout.cards)
                          Positioned.fromRect(
                            rect: card.rect,
                            child: _TableCard(
                              table: card.table,
                              headerHeight: card.headerHeight,
                              rowHeight: card.rowHeight,
                              bottomPadding: layout.bottomPadding,
                            ),
                          ),
                      ],
                    ),
                  ),
                ),
        ),
        ConstrainedBox(
          constraints: BoxConstraints(maxHeight: viewport * 0.3),
          child: SingleChildScrollView(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                const _DiagramLegend(),
                if (schema.externalReferences.isNotEmpty)
                  Padding(
                    padding: const EdgeInsets.fromLTRB(16, 0, 16, 12),
                    child: Text(
                      'Trỏ tới bảng ngoài sơ đồ: '
                      '${schema.externalReferences.map((relation) => '${relation.fromTable}.${relation.fromColumn} → ${relation.toTable}').join('; ')}',
                      style: texts.bodySmall,
                    ),
                  ),
              ],
            ),
          ),
        ),
      ],
    );
  }
}

/// Thẻ một bảng: tên + danh sách cột kèm nhãn khoá.
class _TableCard extends StatelessWidget {
  const _TableCard({
    required this.table,
    required this.headerHeight,
    required this.rowHeight,
    required this.bottomPadding,
  });

  final SchemaTableModel table;

  /// Chiều cao đã quy đổi theo cỡ chữ hệ thống (lấy từ bố cục) — thẻ và bố cục
  /// phải dùng **cùng** con số, nếu không điểm neo của đường quan hệ lệch dòng.
  final double headerHeight;
  final double rowHeight;
  final double bottomPadding;

  @override
  Widget build(BuildContext context) {
    final texts = Theme.of(context).textTheme;
    return DecoratedBox(
      decoration: BoxDecoration(
        color: Colors.white,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: AppTheme.borderStrong),
        boxShadow: const [
          BoxShadow(
            color: Color(0x14000000),
            blurRadius: 4,
            offset: Offset(0, 2),
          ),
        ],
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Container(
            height: headerHeight,
            alignment: Alignment.centerLeft,
            padding: const EdgeInsets.symmetric(horizontal: 10),
            decoration: const BoxDecoration(
              color: AppTheme.govBlueDark,
              borderRadius: BorderRadius.vertical(top: Radius.circular(9)),
            ),
            child: Row(
              children: [
                const Icon(Icons.table_chart, size: 14, color: Colors.white),
                const SizedBox(width: 6),
                Expanded(
                  child: Text(
                    table.qualifiedName,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: texts.labelMedium?.copyWith(
                      color: Colors.white,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ),
              ],
            ),
          ),
          for (final column in table.columns)
            SizedBox(
              height: rowHeight,
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 10),
                child: Row(
                  children: [
                    Icon(
                      column.isPrimaryKey
                          ? Icons.key
                          : column.isForeignKey
                          ? Icons.link
                          : Icons.remove,
                      size: 12,
                      color: column.isPrimaryKey
                          ? AppTheme.govRed
                          : AppTheme.textMuted,
                    ),
                    const SizedBox(width: 6),
                    Expanded(
                      child: Text(
                        column.name,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: texts.bodySmall,
                      ),
                    ),
                    Flexible(
                      child: Text(
                        column.keyLabel.isNotEmpty
                            ? column.keyLabel
                            : column.dataType,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        textAlign: TextAlign.right,
                        style: texts.labelSmall?.copyWith(
                          color: column.keyLabel.isNotEmpty
                              ? AppTheme.govBlueDark
                              : AppTheme.textMuted,
                          fontWeight: column.keyLabel.isNotEmpty
                              ? FontWeight.w700
                              : null,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ),
          SizedBox(height: bottomPadding),
        ],
      ),
    );
  }
}

class _RelationPainter extends CustomPainter {
  const _RelationPainter({required this.edges, required this.color});

  final List<DiagramEdgeLayout> edges;
  final Color color;

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.6
      ..strokeCap = StrokeCap.round;
    final dot = Paint()..color = AppTheme.govRed;

    for (final edge in edges) {
      canvas.drawPath(edge.path, paint);
      // Đầu ở bảng con là nơi khoá ngoại được khai báo — chấm đỏ đánh dấu đúng đầu đó.
      canvas.drawCircle(edge.from, 3, dot);
    }
  }

  @override
  bool shouldRepaint(_RelationPainter oldDelegate) =>
      oldDelegate.edges != edges || oldDelegate.color != color;
}

class _DiagramLegend extends StatelessWidget {
  const _DiagramLegend();

  @override
  Widget build(BuildContext context) {
    final texts = Theme.of(context).textTheme;
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 0, 16, 10),
      child: Wrap(
        spacing: 16,
        runSpacing: 6,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          _LegendItem(
            icon: Icons.key,
            color: AppTheme.govRed,
            label: 'Khoá chính',
            style: texts.labelSmall,
          ),
          _LegendItem(
            icon: Icons.link,
            color: AppTheme.textMuted,
            label: 'Khoá ngoại',
            style: texts.labelSmall,
          ),
          _LegendItem(
            icon: Icons.horizontal_rule,
            color: AppTheme.borderStrong,
            label: 'Quan hệ (đầu đỏ = bảng giữ khoá ngoại)',
            style: texts.labelSmall,
          ),
          Text('Kéo để di chuyển · chụm để zoom', style: texts.labelSmall),
        ],
      ),
    );
  }
}

/// Mục chú giải: icon + chữ **trên cùng một dòng chữ** (`WidgetSpan` của `Text`).
///
/// Không dùng `Row(Icon, Text)`: `Row` bên trong `Wrap` chỉ nhận bề rộng tối đa
/// của khung, mà chữ thì không tự co — mục chú giải dài sẽ tràn ngang (đã bị bắt
/// bởi test bố cục ở khổ 320dp). `Text.rich` thì ngắt dòng được như mọi đoạn chữ.
class _LegendItem extends StatelessWidget {
  const _LegendItem({
    required this.icon,
    required this.color,
    required this.label,
    this.style,
  });

  final IconData icon;
  final Color color;
  final String label;
  final TextStyle? style;

  @override
  Widget build(BuildContext context) => Text.rich(
    TextSpan(
      style: style,
      children: [
        WidgetSpan(
          alignment: PlaceholderAlignment.middle,
          child: Icon(icon, size: 12, color: color),
        ),
        const TextSpan(text: ' '),
        TextSpan(text: label),
      ],
    ),
  );
}

class _MetaChip extends StatelessWidget {
  const _MetaChip({required this.icon, required this.label, this.color});

  final IconData icon;
  final String label;
  final Color? color;

  /// `Text.rich` thay vì `Row(Icon, Text)`: chữ trong `Wrap` nhận trần bề rộng và
  /// phải tự ngắt dòng được, nếu không chip dài ("2 quan hệ khoá ngoại" ở cỡ chữ
  /// 1.3×) sẽ tràn ngang.
  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
    decoration: BoxDecoration(
      color: AppTheme.surfaceMuted,
      borderRadius: BorderRadius.circular(20),
      border: Border.all(color: AppTheme.border),
    ),
    child: Text.rich(
      TextSpan(
        style: Theme.of(
          context,
        ).textTheme.labelSmall?.copyWith(color: color ?? AppTheme.textMuted),
        children: [
          WidgetSpan(
            alignment: PlaceholderAlignment.middle,
            child: Icon(icon, size: 13, color: color ?? AppTheme.textMuted),
          ),
          const TextSpan(text: ' '),
          TextSpan(text: label),
        ],
      ),
    ),
  );
}
