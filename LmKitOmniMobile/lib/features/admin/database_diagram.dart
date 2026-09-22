/// Bố cục sơ đồ quan hệ (ER) cho mobile — **hàm thuần**, không phụ thuộc Flutter
/// widget, nên đo được bằng test mà không cần dựng màn hình.
///
/// Bảng được xếp thành lưới nhiều cột: mỗi cột tự xếp dọc theo chiều cao thật của
/// thẻ (bảng 3 cột và bảng 20 cột không cùng cỡ), nhờ vậy sơ đồ không có khoảng
/// trống lớn. Toàn bộ khung nằm trong `InteractiveViewer` nên rộng quá màn hình
/// là chuyện bình thường — người dùng kéo/zoom.
library;

import 'dart:math' as math;
import 'dart:ui' show Offset, Path, Rect, Size;

import 'package:flutter/widgets.dart' show BuildContext, MediaQuery;

import 'admin_models.dart';

/// Một thẻ bảng đã đặt chỗ trên sơ đồ.
class DiagramCardLayout {
  const DiagramCardLayout({
    required this.table,
    required this.rect,
    required this.headerHeight,
    required this.rowHeight,
  });

  final SchemaTableModel table;
  final Rect rect;

  /// Chiều cao đã quy đổi theo cỡ chữ hệ thống — thẻ phải cao lên khi người dùng
  /// tăng cỡ chữ, nếu không dòng cột sẽ tràn khỏi thẻ.
  final double headerHeight;
  final double rowHeight;

  /// Toạ độ dòng của một cột trong thẻ (tâm dọc của dòng đó).
  double? rowCenterY(String columnName) {
    final index = table.columns.indexWhere(
      (column) => column.name.toLowerCase() == columnName.toLowerCase(),
    );
    if (index < 0) return null;
    return rect.top + headerHeight + index * rowHeight + rowHeight / 2;
  }

  /// Điểm neo bên phải, ngang đúng dòng của [columnName] (giữa thẻ nếu không có).
  Offset rightAnchor(String columnName) =>
      Offset(rect.right, rowCenterY(columnName) ?? rect.center.dy);

  /// Điểm neo bên trái, ngang đúng dòng của [columnName].
  Offset leftAnchor(String columnName) =>
      Offset(rect.left, rowCenterY(columnName) ?? rect.center.dy);

  Offset get bottomAnchor => Offset(rect.center.dx, rect.bottom);
  Offset get topAnchor => Offset(rect.center.dx, rect.top);
}

/// Một cạnh đã nối hai thẻ.
class DiagramEdgeLayout {
  const DiagramEdgeLayout({
    required this.relation,
    required this.from,
    required this.to,
    required this.vertical,
  });

  final SchemaRelationModel relation;

  /// Đầu ở bảng **con** (bảng đang giữ khoá ngoại).
  final Offset from;

  /// Đầu ở bảng **cha**.
  final Offset to;

  /// Hai thẻ cùng một cột → nối trên-dưới thay vì trái-phải.
  final bool vertical;

  /// Hai điểm điều khiển của đường cong; tách riêng để test kiểm được hướng vẽ.
  ({Offset first, Offset second}) get controlPoints {
    final span = vertical ? (to.dy - from.dy).abs() : (to.dx - from.dx).abs();
    final reach = math.max(32.0, span / 2);
    if (vertical) {
      return (
        first: Offset(from.dx, from.dy + (to.dy >= from.dy ? reach : -reach)),
        second: Offset(to.dx, to.dy - (to.dy >= from.dy ? reach : -reach)),
      );
    }
    return (
      first: Offset(from.dx + (to.dx >= from.dx ? reach : -reach), from.dy),
      second: Offset(to.dx - (to.dx >= from.dx ? reach : -reach), to.dy),
    );
  }

  Path get path {
    final controls = controlPoints;
    return Path()
      ..moveTo(from.dx, from.dy)
      ..cubicTo(
        controls.first.dx,
        controls.first.dy,
        controls.second.dx,
        controls.second.dy,
        to.dx,
        to.dy,
      );
  }
}

/// Bố cục hoàn chỉnh: kích thước khung + vị trí thẻ + các cạnh.
class SchemaDiagramLayout {
  const SchemaDiagramLayout({
    required this.size,
    required this.cards,
    required this.edges,
    this.headerHeight = baseHeaderHeight,
    this.rowHeight = baseRowHeight,
    this.bottomPadding = baseBottomPadding,
  });

  final Size size;
  final List<DiagramCardLayout> cards;
  final List<DiagramEdgeLayout> edges;

  static const double cardWidth = 232;

  /// Số đo gốc (cỡ chữ 1×). Chiều cao thật của thẻ là số đo này nhân hệ số cỡ
  /// chữ — xem [headerHeight]/[rowHeight]/[bottomPadding].
  static const double baseHeaderHeight = 38;
  static const double baseRowHeight = 26;
  static const double baseBottomPadding = 10;
  static const double gapX = 56;
  static const double gapY = 44;
  static const double margin = 24;

  /// Chiều cao header/dòng/đệm đã nhân theo cỡ chữ hệ thống (kẹp trong [1, 1.6]).
  /// Widget vẽ thẻ phải dùng đúng ba con số này, nếu không thẻ và bố cục lệch nhau.
  final double headerHeight;
  final double rowHeight;
  final double bottomPadding;

  /// Số cột của lưới: 1 bảng → 1 cột, 4 bảng → 2, từ 5 bảng → 3 (quá 3 cột thì
  /// thẻ hẹp tới mức tên bảng bị cắt).
  static int columnsFor(int tableCount) {
    if (tableCount <= 1) return 1;
    return math.min(3, math.max(2, math.sqrt(tableCount).ceil()));
  }

  /// Cỡ chữ hệ thống quy đổi thành hệ số chiều cao (kẹp lại để sơ đồ không phình
  /// vô hạn ở cỡ chữ rất lớn).
  static double textScaleOf(BuildContext context) =>
      MediaQuery.textScalerOf(context).scale(1).clamp(1.0, 1.6);

  /// Chiều cao thẻ theo số cột của bảng — thẻ thấp thì không có khoảng trống thừa.
  static double cardHeight(SchemaTableModel table, {double textScale = 1}) =>
      baseHeaderHeight * textScale +
      table.columns.length * baseRowHeight * textScale +
      baseBottomPadding * textScale;

  static SchemaDiagramLayout compute(
    DatabaseSchemaModel schema, {
    double textScale = 1,
  }) {
    final tables = schema.tables;
    final scale = textScale.clamp(1.0, 1.6);
    final headerHeight = baseHeaderHeight * scale;
    final rowHeight = baseRowHeight * scale;
    final bottomPadding = baseBottomPadding * scale;

    if (tables.isEmpty) {
      return SchemaDiagramLayout(
        size: Size.zero,
        cards: const [],
        edges: const [],
        headerHeight: headerHeight,
        rowHeight: rowHeight,
        bottomPadding: bottomPadding,
      );
    }

    final columns = columnsFor(tables.length);
    final columnHeights = List<double>.filled(columns, margin);
    final cards = <DiagramCardLayout>[];

    for (var index = 0; index < tables.length; index++) {
      final table = tables[index];
      final column = index % columns;
      final left = margin + column * (cardWidth + gapX);
      final top = columnHeights[column];
      final rect = Rect.fromLTWH(
        left,
        top,
        cardWidth,
        cardHeight(table, textScale: scale),
      );
      cards.add(
        DiagramCardLayout(
          table: table,
          rect: rect,
          headerHeight: headerHeight,
          rowHeight: rowHeight,
        ),
      );
      columnHeights[column] = rect.bottom + gapY;
    }

    final byId = {for (final card in cards) card.table.qualifiedName: card};
    final edges = <DiagramEdgeLayout>[];
    final cardIndex = {
      for (var i = 0; i < cards.length; i++) cards[i].table.qualifiedName: i,
    };

    for (final relation in schema.drawableRelations) {
      final fromCard = byId[relation.fromTable];
      final toCard = byId[relation.toTable];
      if (fromCard == null || toCard == null) continue;

      final sameColumn =
          cardIndex[relation.fromTable]! % columns ==
          cardIndex[relation.toTable]! % columns;
      if (sameColumn) {
        final fromIsUpper = fromCard.rect.top <= toCard.rect.top;
        edges.add(
          DiagramEdgeLayout(
            relation: relation,
            from: fromIsUpper ? fromCard.bottomAnchor : fromCard.topAnchor,
            to: fromIsUpper ? toCard.topAnchor : toCard.bottomAnchor,
            vertical: true,
          ),
        );
      } else {
        final toTheRight = toCard.rect.center.dx >= fromCard.rect.center.dx;
        edges.add(
          DiagramEdgeLayout(
            relation: relation,
            from: toTheRight
                ? fromCard.rightAnchor(relation.fromColumn)
                : fromCard.leftAnchor(relation.fromColumn),
            to: toTheRight
                ? toCard.leftAnchor(relation.toColumn)
                : toCard.rightAnchor(relation.toColumn),
            vertical: false,
          ),
        );
      }
    }

    final width = margin * 2 + columns * cardWidth + (columns - 1) * gapX;
    final height = columnHeights.reduce(math.max) - gapY + margin;

    return SchemaDiagramLayout(
      size: Size(width, height),
      cards: cards,
      edges: edges,
      headerHeight: headerHeight,
      rowHeight: rowHeight,
      bottomPadding: bottomPadding,
    );
  }
}

/// Ghi chú phải hiện kèm sơ đồ để người đọc không hiểu sai hình vẽ.
/// Rỗng nghĩa là không có gì phải cảnh báo.
List<String> schemaDiagramNotes(DatabaseSchemaModel schema) {
  final notes = <String>[];

  if (schema.truncated) {
    notes.add(
      'Sơ đồ chỉ vẽ ${schema.tableCount}/${schema.totalTableCount} bảng đầu tiên '
      '(theo thứ tự tên) — phần còn lại bị cắt cho dễ đọc.',
    );
  }

  final dangling = schema.externalReferences.length;
  if (dangling > 0) {
    notes.add(
      '$dangling khoá ngoại trỏ tới bảng không nằm trong sơ đồ nên không được vẽ.',
    );
  }

  final unresolved = schema.unresolvedForeignKeys.length;
  if (unresolved > 0) {
    notes.add(
      '$unresolved khoá ngoại không tách được thành cạnh (khoá tổ hợp hoặc thiếu '
      'thông tin).',
    );
  }

  if (schema.provider.toLowerCase() == 'mongo') {
    notes.add(
      'MongoDB không có khoá ngoại: mỗi "bảng" là một collection, cột là các '
      'trường lấy mẫu từ tài liệu.',
    );
  }

  if (!schema.isIndexed) {
    notes.add(
      'Kết nối chưa đánh chỉ mục schema — sơ đồ đọc trực tiếp từ CSDL nên vẫn '
      'xem được.',
    );
  }

  if (schema.tables.isEmpty) {
    notes.add('CSDL này không có bảng nào để vẽ.');
  }

  return notes;
}
