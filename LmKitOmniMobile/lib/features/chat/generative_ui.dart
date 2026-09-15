import 'dart:convert';
import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../app/theme.dart';

/// Một chuỗi dữ liệu trong biểu đồ (`datasets[i]` của Chart.js).
class ChartDataset {
  const ChartDataset({required this.label, required this.values, this.color});

  final String label;
  final List<double> values;
  final Color? color;
}

/// Biểu đồ trợ lý nhúng trong câu trả lời bằng khối `<chart>{json}</chart>`.
///
/// Đây là bản mobile của `GenerativeUiRenderer.vue` bên desktop: cùng đọc JSON
/// theo định dạng Chart.js (`type`, `title`, `data.labels`, `data.datasets`) và
/// tách khối biểu đồ ra khỏi phần chữ của tin nhắn.
class ChartSpec {
  const ChartSpec({
    required this.type,
    required this.labels,
    required this.datasets,
    this.title,
  });

  final String type;
  final String? title;
  final List<String> labels;
  final List<ChartDataset> datasets;

  bool get isPie => type == 'pie' || type == 'doughnut' || type == 'donut';
  bool get isLine => type == 'line' || type == 'area';
  bool get isBar => type == 'bar' || type == 'horizontalbar';

  /// Số điểm dữ liệu lớn nhất trong mọi chuỗi.
  int get pointCount => datasets.fold<int>(
    0,
    (max, dataset) => math.max(max, dataset.values.length),
  );
}

/// Kết quả tách nội dung: phần chữ còn lại và danh sách biểu đồ theo thứ tự.
class GenerativeContent {
  const GenerativeContent({required this.text, this.charts = const []});

  final String text;
  final List<ChartSpec> charts;

  bool get hasCharts => charts.isNotEmpty;
}

final _chartBlock = RegExp(r'<chart>([\s\S]*?)</chart>');

/// Tách mọi khối `<chart>` khỏi [raw]; khối JSON hỏng bị bỏ qua chứ không làm
/// hỏng cả tin nhắn (giống hành vi `console.error` + bỏ qua của desktop).
GenerativeContent parseGenerativeContent(String raw) {
  final charts = <ChartSpec>[];
  for (final match in _chartBlock.allMatches(raw)) {
    final spec = _parseSpec(match.group(1) ?? '');
    if (spec != null) charts.add(spec);
  }
  final text = raw.replaceAll(_chartBlock, '').trim();
  return GenerativeContent(text: text, charts: charts);
}

ChartSpec? _parseSpec(String json) {
  try {
    final decoded = jsonDecode(json.trim());
    if (decoded is! Map) return null;
    final data = decoded['data'] is Map
        ? Map<String, dynamic>.from(decoded['data'] as Map)
        : <String, dynamic>{};

    final labels =
        (data['labels'] as List<dynamic>?)
            ?.map((item) => item?.toString() ?? '')
            .toList() ??
        const <String>[];

    final datasets = <ChartDataset>[];
    for (final entry in data['datasets'] as List<dynamic>? ?? const []) {
      if (entry is! Map) continue;
      final map = Map<String, dynamic>.from(entry);
      final values = <double>[];
      for (final point in map['data'] as List<dynamic>? ?? const []) {
        final value = _number(point);
        if (value != null) values.add(value);
      }
      if (values.isEmpty) continue;
      datasets.add(
        ChartDataset(
          label: map['label']?.toString() ?? 'Chuỗi ${datasets.length + 1}',
          values: values,
          color: _color(
            map['backgroundColor'] ?? map['borderColor'] ?? map['color'],
          ),
        ),
      );
    }
    if (datasets.isEmpty) return null;

    final type = decoded['type']?.toString().toLowerCase() ?? 'bar';
    return ChartSpec(
      type: type,
      title: decoded['title']?.toString(),
      labels: labels,
      datasets: datasets,
    );
  } catch (_) {
    return null;
  }
}

/// Chấp nhận cả số, chuỗi số và điểm `{x, y}` của Chart.js.
double? _number(Object? value) {
  if (value is num) return value.toDouble();
  if (value is String) return double.tryParse(value);
  if (value is Map) return _number(value['y'] ?? value['value']);
  return null;
}

Color? _color(Object? value) {
  if (value is! String) return null;
  var hex = value.trim().replaceFirst('#', '');
  if (hex.length == 3) {
    hex = hex.split('').map((char) => '$char$char').join();
  }
  if (hex.length == 6) hex = 'ff$hex';
  if (hex.length != 8) return null;
  final parsed = int.tryParse(hex, radix: 16);
  return parsed == null ? null : Color(parsed);
}

/// Bảng màu mặc định khi JSON không chỉ định màu.
const _palette = <Color>[
  Color(0xFF2563EB),
  Color(0xFF16A34A),
  Color(0xFFF59E0B),
  Color(0xFFDC2626),
  Color(0xFF7C3AED),
  Color(0xFF0891B2),
];

/// Khung biểu đồ có tiêu đề, chú giải; bấm vào để xem bảng số liệu.
class GenerativeChart extends StatefulWidget {
  const GenerativeChart({super.key, required this.spec});

  final ChartSpec spec;

  @override
  State<GenerativeChart> createState() => _GenerativeChartState();
}

class _GenerativeChartState extends State<GenerativeChart> {
  bool _showTable = false;

  Color _seriesColor(int index, ChartDataset dataset) =>
      dataset.color ?? _palette[index % _palette.length];

  @override
  Widget build(BuildContext context) {
    final spec = widget.spec;
    final scheme = Theme.of(context).colorScheme;
    final supported = spec.isBar || spec.isLine || spec.isPie;
    return Container(
      margin: const EdgeInsets.only(top: 10),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: scheme.surface,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: scheme.outlineVariant),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  spec.title?.isNotEmpty == true
                      ? spec.title!
                      : 'Biểu đồ ${spec.type}',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
              ),
              IconButton(
                tooltip: _showTable ? 'Xem biểu đồ' : 'Xem bảng số liệu',
                iconSize: 18,
                onPressed: () => setState(() => _showTable = !_showTable),
                icon: Icon(
                  _showTable ? Icons.bar_chart : Icons.table_chart_outlined,
                ),
              ),
            ],
          ),
          if (!supported)
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: Text(
                'Loại biểu đồ "${spec.type}" chưa được hỗ trợ trên mobile — '
                'hiển thị dạng bảng.',
                style: Theme.of(context).textTheme.bodySmall,
              ),
            ),
          if (_showTable || !supported)
            _DataTable(spec: spec, colorOf: _seriesColor)
          else ...[
            SizedBox(
              height: 220,
              child: CustomPaint(
                size: Size.infinite,
                painter: spec.isPie
                    ? _PiePainter(
                        spec: spec,
                        colors: [
                          for (var i = 0; i < spec.datasets.length; i++)
                            _seriesColor(i, spec.datasets[i]),
                        ],
                        labelStyle: Theme.of(context).textTheme.bodySmall,
                      )
                    : _AxisPainter(
                        spec: spec,
                        colors: [
                          for (var i = 0; i < spec.datasets.length; i++)
                            _seriesColor(i, spec.datasets[i]),
                        ],
                        gridColor: AppTheme.border,
                        axisColor: AppTheme.textMuted,
                        labelStyle: Theme.of(context).textTheme.bodySmall,
                      ),
              ),
            ),
            const SizedBox(height: 8),
            Wrap(
              spacing: 12,
              runSpacing: 4,
              children: [
                for (var i = 0; i < spec.datasets.length; i++)
                  Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Container(
                        width: 10,
                        height: 10,
                        decoration: BoxDecoration(
                          color: _seriesColor(i, spec.datasets[i]),
                          shape: BoxShape.circle,
                        ),
                      ),
                      const SizedBox(width: 4),
                      Text(
                        spec.datasets[i].label,
                        style: Theme.of(context).textTheme.bodySmall,
                      ),
                    ],
                  ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

class _DataTable extends StatelessWidget {
  const _DataTable({required this.spec, required this.colorOf});

  final ChartSpec spec;
  final Color Function(int index, ChartDataset dataset) colorOf;

  @override
  Widget build(BuildContext context) {
    final rows = math.max(spec.pointCount, spec.labels.length);
    return SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      child: DataTable(
        headingRowHeight: 36,
        dataRowMinHeight: 32,
        dataRowMaxHeight: 40,
        columns: [
          const DataColumn(label: Text('Nhãn')),
          for (var i = 0; i < spec.datasets.length; i++)
            DataColumn(label: Text(spec.datasets[i].label)),
        ],
        rows: [
          for (var row = 0; row < rows; row++)
            DataRow(
              cells: [
                DataCell(
                  Text(
                    row < spec.labels.length ? spec.labels[row] : '${row + 1}',
                  ),
                ),
                for (var i = 0; i < spec.datasets.length; i++)
                  DataCell(
                    Text(
                      _cellText(spec.datasets[i], row),
                      style: TextStyle(color: colorOf(i, spec.datasets[i])),
                    ),
                  ),
              ],
            ),
        ],
      ),
    );
  }

  static String _cellText(ChartDataset dataset, int row) {
    if (row >= dataset.values.length) return '–';
    final value = dataset.values[row];
    return value == value.roundToDouble()
        ? value.toInt().toString()
        : value.toStringAsFixed(2);
  }
}

abstract class _ChartPainter extends CustomPainter {
  _ChartPainter({
    required this.spec,
    required this.colors,
    required this.labelStyle,
  });

  final ChartSpec spec;
  final List<Color> colors;
  final TextStyle? labelStyle;

  double get _maxValue {
    var max = 0.0;
    for (final dataset in spec.datasets) {
      for (final value in dataset.values) {
        max = math.max(max, value);
      }
    }
    return max <= 0 ? 1 : max;
  }

  double get _minValue {
    var min = 0.0;
    for (final dataset in spec.datasets) {
      for (final value in dataset.values) {
        min = math.min(min, value);
      }
    }
    return min;
  }

  void _drawText(
    Canvas canvas,
    String text,
    Offset offset, {
    double maxWidth = 60,
  }) {
    final painter = TextPainter(
      text: TextSpan(text: text, style: labelStyle),
      textDirection: TextDirection.ltr,
      maxLines: 1,
      ellipsis: '…',
    )..layout(maxWidth: maxWidth);
    painter.paint(canvas, offset);
  }

  /// Hiển thị nhãn thưa dần để không chồng chữ khi có nhiều điểm dữ liệu.
  int get _labelStride => math.max(1, (spec.labels.length / 8).ceil());
}

class _AxisPainter extends _ChartPainter {
  _AxisPainter({
    required super.spec,
    required super.colors,
    required super.labelStyle,
    required this.gridColor,
    required this.axisColor,
  });

  final Color gridColor;
  final Color axisColor;

  @override
  void paint(Canvas canvas, Size size) {
    const leftGutter = 34.0;
    const bottomGutter = 22.0;
    final plot = Rect.fromLTRB(
      leftGutter,
      6,
      size.width - 4,
      size.height - bottomGutter,
    );
    if (plot.width <= 0 || plot.height <= 0) return;

    final min = math.min(0.0, _minValue);
    final max = math.max(_maxValue, 0.0);
    final span = (max - min) == 0 ? 1.0 : (max - min);
    double yFor(double value) =>
        plot.bottom - ((value - min) / span) * plot.height;

    final gridPaint = Paint()
      ..color = gridColor
      ..strokeWidth = 1;
    for (var step = 0; step <= 3; step++) {
      final value = min + span * step / 3;
      final y = yFor(value);
      canvas.drawLine(Offset(plot.left, y), Offset(plot.right, y), gridPaint);
      _drawText(
        canvas,
        value == value.roundToDouble()
            ? value.toInt().toString()
            : value.toStringAsFixed(1),
        Offset(0, y - 7),
        maxWidth: leftGutter - 6,
      );
    }

    canvas.drawLine(
      Offset(plot.left, yFor(min)),
      Offset(plot.right, yFor(min)),
      Paint()
        ..color = axisColor
        ..strokeWidth = 1.2,
    );

    final points = math.max(spec.pointCount, 1);
    final slot = plot.width / points;
    final labelStride = _labelStride;

    if (spec.isLine) {
      for (var i = 0; i < spec.datasets.length; i++) {
        final values = spec.datasets[i].values;
        final path = Path();
        for (var index = 0; index < values.length; index++) {
          final x = plot.left + slot * (index + 0.5);
          final y = yFor(values[index]);
          if (index == 0) {
            path.moveTo(x, y);
          } else {
            path.lineTo(x, y);
          }
        }
        canvas.drawPath(
          path,
          Paint()
            ..color = colors[i % colors.length]
            ..strokeWidth = 2
            ..style = PaintingStyle.stroke,
        );
        for (var index = 0; index < values.length; index++) {
          canvas.drawCircle(
            Offset(plot.left + slot * (index + 0.5), yFor(values[index])),
            2.5,
            Paint()..color = colors[i % colors.length],
          );
        }
      }
    } else {
      // Cột: mỗi nhãn là một nhóm, các chuỗi nằm cạnh nhau trong nhóm.
      final seriesCount = spec.datasets.length;
      final barWidth = (slot * 0.7) / seriesCount;
      for (var index = 0; index < points; index++) {
        for (var i = 0; i < seriesCount; i++) {
          final values = spec.datasets[i].values;
          if (index >= values.length) continue;
          final value = values[index];
          final left = plot.left + slot * index + slot * 0.15 + barWidth * i;
          final top = yFor(math.max(value, 0));
          final bottom = yFor(math.min(value, 0));
          canvas.drawRRect(
            RRect.fromRectAndRadius(
              Rect.fromLTRB(
                left,
                math.min(top, bottom),
                left + barWidth * 0.9,
                math.max(top, bottom),
              ),
              const Radius.circular(3),
            ),
            Paint()..color = colors[i % colors.length],
          );
        }
      }
    }

    for (var index = 0; index < spec.labels.length; index += labelStride) {
      _drawText(
        canvas,
        spec.labels[index],
        Offset(plot.left + slot * (index + 0.5) - slot * 0.5, plot.bottom + 4),
        maxWidth: slot * labelStride,
      );
    }
  }

  @override
  bool shouldRepaint(covariant _AxisPainter old) =>
      old.spec != spec || old.colors != colors;
}

class _PiePainter extends _ChartPainter {
  _PiePainter({
    required super.spec,
    required super.colors,
    required super.labelStyle,
  });

  @override
  void paint(Canvas canvas, Size size) {
    final values = spec.datasets.first.values;
    final total = values.fold<double>(0, (sum, value) => sum + value.abs());
    if (total <= 0) return;

    final center = Offset(size.width / 2, size.height / 2);
    final radius = math.min(size.width, size.height) / 2 - 8;
    if (radius <= 0) return;

    var start = -math.pi / 2;
    for (var index = 0; index < values.length; index++) {
      final sweep = (values[index].abs() / total) * 2 * math.pi;
      canvas.drawArc(
        Rect.fromCircle(center: center, radius: radius),
        start,
        sweep,
        true,
        Paint()..color = colors[index % colors.length],
      );
      start += sweep;
    }

    // Lỗ giữa để thành biểu đồ vành khuyên, kèm tổng ở tâm.
    canvas.drawCircle(center, radius * 0.55, Paint()..color = Colors.white);
    _drawText(
      canvas,
      total.round().toString(),
      Offset(center.dx - 12, center.dy - 8),
      maxWidth: 40,
    );

    for (var index = 0; index < spec.labels.length && index < 6; index++) {
      _drawText(
        canvas,
        '${spec.labels[index]}: ${values[index]}',
        Offset(4, 4 + index * 14),
        maxWidth: 120,
      );
    }
  }

  @override
  bool shouldRepaint(covariant _PiePainter old) =>
      old.spec != spec || old.colors != colors;
}
