import 'package:flutter_test/flutter_test.dart';
import 'package:lmkit_omni_mobile/features/chat/generative_ui.dart';

void main() {
  group('parseGenerativeContent', () {
    test('tách biểu đồ khỏi phần chữ', () {
      const raw =
          'Doanh thu theo tháng:\n'
          '<chart>{"type":"bar","title":"Doanh thu","data":{"labels":["T1","T2"],'
          '"datasets":[{"label":"2026","data":[10,20]}]}}</chart>\n'
          'Nhận xét: tăng trưởng tốt.';

      final content = parseGenerativeContent(raw);

      expect(content.hasCharts, isTrue);
      expect(content.charts.single.type, 'bar');
      expect(content.charts.single.title, 'Doanh thu');
      expect(content.charts.single.labels, ['T1', 'T2']);
      expect(content.charts.single.datasets.single.values, [10, 20]);
      expect(content.text.contains('<chart>'), isFalse);
      expect(content.text.contains('Doanh thu theo tháng'), isTrue);
      expect(content.text.contains('tăng trưởng tốt'), isTrue);
    });

    test('nhiều biểu đồ giữ đúng thứ tự', () {
      const raw =
          '<chart>{"type":"line","data":{"labels":["a"],"datasets":'
          '[{"label":"x","data":[1]}]}}</chart>'
          '<chart>{"type":"pie","data":{"labels":["b"],"datasets":'
          '[{"label":"y","data":[2]}]}}</chart>';

      final content = parseGenerativeContent(raw);

      expect(content.charts.map((chart) => chart.type), ['line', 'pie']);
      expect(content.text, isEmpty);
    });

    test('bỏ qua JSON hỏng nhưng giữ phần chữ', () {
      const raw = 'Trước\n<chart>{không phải json}</chart>\nSau';

      final content = parseGenerativeContent(raw);

      expect(content.hasCharts, isFalse);
      expect(content.text, contains('Trước'));
      expect(content.text, contains('Sau'));
    });

    test('bỏ qua chart không có dataset hợp lệ', () {
      const raw =
          '<chart>{"type":"bar","data":{"labels":["a"],"datasets":[]}}</chart>';

      expect(parseGenerativeContent(raw).hasCharts, isFalse);
    });

    test('đọc được dữ liệu dạng chuỗi và điểm {x,y}', () {
      const raw =
          '<chart>{"type":"line","data":{"labels":["a","b"],"datasets":'
          '[{"label":"s","data":["12.5",{"y":7}]}]}}</chart>';

      final dataset = parseGenerativeContent(raw).charts.single.datasets.single;

      expect(dataset.values, [12.5, 7.0]);
      expect(dataset.label, 's');
    });

    test('nhận màu hex 3/6/8 ký tự và bỏ qua màu không hợp lệ', () {
      const raw =
          '<chart>{"type":"bar","data":{"datasets":['
          '{"label":"a","data":[1],"backgroundColor":"#fff"},'
          '{"label":"b","data":[2],"backgroundColor":"#16A34A"},'
          '{"label":"c","data":[3],"color":"rgb(1,2,3)"}]}}</chart>';

      final datasets = parseGenerativeContent(raw).charts.single.datasets;

      expect(datasets[0].color!.toARGB32(), 0xFFFFFFFF);
      expect(datasets[1].color!.toARGB32(), 0xFF16A34A);
      expect(datasets[2].color, isNull);
    });

    test('nhận diện loại biểu đồ', () {
      ChartSpec specOf(String type) => parseGenerativeContent(
        '<chart>{"type":"$type","data":{"labels":["a"],"datasets":'
        '[{"label":"s","data":[1]}]}}</chart>',
      ).charts.single;

      expect(specOf('bar').isBar, isTrue);
      expect(specOf('horizontalBar').isBar, isTrue);
      expect(specOf('line').isLine, isTrue);
      expect(specOf('doughnut').isPie, isTrue);
      expect(specOf('radar').isBar, isFalse);
      expect(specOf('radar').isPie, isFalse);
    });

    test('tin nhắn không có chart trả về nguyên văn', () {
      final content = parseGenerativeContent('Chỉ là văn bản bình thường.');

      expect(content.hasCharts, isFalse);
      expect(content.text, 'Chỉ là văn bản bình thường.');
    });
  });
}
