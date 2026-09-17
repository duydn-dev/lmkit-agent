import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:lmkit_omni_mobile/app/theme.dart';
import 'package:lmkit_omni_mobile/app/ui/app_controls.dart';

/// Những widget **chỉ Material mới có tổ tiên `Material`**.
///
/// `FDialog`/`showFSheet` của Forui dựng route riêng, không có `Material`, nên
/// đặt một trong những widget này vào nội dung hộp thoại/sheet là cả hộp thoại
/// ném "No Material widget found" ngay lúc dựng — đúng lỗi đã gặp thật ở hộp
/// thoại "Tạo lịch tự động" và form agent. Test này soi mã nguồn để lỗi đó
/// không lặng lẽ quay lại ở một màn mới.
const _materialOnlyNames = <String>[
  'TextField(',
  'TextFormField(',
  'DropdownButtonFormField(',
  'DropdownButton(',
  'Checkbox(',
  'CheckboxListTile(',
  'SwitchListTile(',
  'RangeSlider(',
  'Slider(',
  'Radio(',
  'AlertDialog(',
  'ElevatedButton(',
  'FilledButton(',
  'OutlinedButton(',
  'TextButton(',
  'ListTile(',
  'Card(',
  'TabBar(',
  'ExpansionTile(',
  'PopupMenuButton',
  'Stepper(',
  'InkWell(',
  'InkResponse(',
  'Material(',
  'CircularProgressIndicator(',
];

/// Tên widget, kèm ràng buộc **không** dính vào đuôi một định danh khác —
/// `AppTextField(` không phải `TextField(`, `AppCard(` không phải `Card(`.
final _materialOnly = [
  for (final name in _materialOnlyNames)
    RegExp('(?:^|[^A-Za-z0-9_\$])${RegExp.escape(name)}'),
];

/// Các hàm mở hộp thoại/sheet của Forui; tham số của chúng là "vùng cấm".
const _openers = <String>[
  'showAppDialog',
  'showAppSheet',
  'showAppPicker',
  'showFDialog',
  'showFSheet',
];

/// Ngoặc tròn của lời gọi bắt đầu tại [start] (đếm cân bằng, có tính chuỗi ký tự).
String _arguments(String source, int start) {
  final open = source.indexOf('(', start);
  var depth = 0;
  var index = open;
  var quote = '';
  while (index < source.length) {
    final char = source[index];
    if (quote.isNotEmpty) {
      if (char == r'\') {
        index += 2;
        continue;
      }
      if (char == quote) quote = '';
    } else if (char == '"' || char == "'") {
      quote = char;
    } else if (char == '(') {
      depth++;
    } else if (char == ')') {
      depth--;
      if (depth == 0) return source.substring(open, index + 1);
    }
    index++;
  }
  return source.substring(open);
}

int _lineOf(String source, int index) =>
    '\n'.allMatches(source.substring(0, index)).length + 1;

void main() {
  test('hộp thoại/sheet Forui không dùng widget chỉ Material mới dựng được', () {
    final offenders = <String>[];

    for (final entity in Directory('lib').listSync(recursive: true)) {
      if (entity is! File || !entity.path.endsWith('.dart')) continue;
      final path = entity.path.replaceAll(r'\', '/');
      // Chính file định nghĩa helper: `showAppDialog` ở đây không phải call site.
      if (path.endsWith('app/ui/app_controls.dart')) continue;

      final source = entity.readAsStringSync();
      for (final opener in _openers) {
        var from = 0;
        while (true) {
          final at = source.indexOf(opener, from);
          if (at < 0) break;
          from = at + opener.length;
          // Bỏ qua `import`/chú thích: chỉ tính lời gọi thật.
          final lineStart = source.lastIndexOf('\n', at) + 1;
          final line = source.substring(lineStart, at);
          if (line.trimLeft().startsWith('//')) continue;

          final body = _arguments(source, at);
          for (var i = 0; i < _materialOnly.length; i++) {
            if (_materialOnly[i].hasMatch(body)) {
              offenders.add(
                '$path:${_lineOf(source, at)} — ${_materialOnlyNames[i]} '
                'trong $opener(...)',
              );
            }
          }
        }
      }
    }

    expect(
      offenders,
      isEmpty,
      reason:
          'Hộp thoại/sheet Forui không có tổ tiên `Material`; hãy dùng widget '
          'của hệ thiết kế (AppTextField, AppSelectTile, AppCheckTile…).\n'
          '${offenders.join('\n')}',
    );
  });

  testWidgets('lỡ đặt widget Material trong hộp thoại thì không màn đỏ', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        child: MaterialApp(
          theme: AppTheme.material(AppTheme.forui()),
          builder: (context, inner) =>
              FTheme(data: AppTheme.forui(), child: inner ?? const SizedBox()),
          home: Scaffold(
            body: Builder(
              builder: (context) => AppPrimaryButton(
                label: 'Mở',
                onPressed: () => showAppDialog<void>(
                  context,
                  title: 'Thử',
                  content: const TextField(
                    decoration: InputDecoration(labelText: 'Ô của Material'),
                  ),
                  actions: [
                    AppSecondaryButton(
                      label: 'Đóng',
                      onPressed: () => Navigator.pop(context),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pump();

    await tester.tap(find.text('Mở'));
    await tester.pumpAndSettle();

    // `SheetSurface`/`AppDialog` bọc sẵn một lớp `Material` trong suốt nên widget
    // Material vẫn dựng được — hỏng giao diện vài pixel còn hơn màn đỏ.
    expect(tester.takeException(), isNull);
    expect(find.text('Ô của Material'), findsOneWidget);
  });
}
