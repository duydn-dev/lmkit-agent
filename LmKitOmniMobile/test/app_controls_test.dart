import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:lmkit_omni_mobile/app/theme.dart';
import 'package:lmkit_omni_mobile/app/ui/app_controls.dart';

Widget _wrap(Widget child) => MaterialApp(
  builder: (context, child) => FTheme(
    data: FTheme.neutral.light.touch,
    child: child ?? const SizedBox.shrink(),
  ),
  home: Scaffold(body: child),
);

/// Bọc trong **đúng** theme của app để đo được thang chữ thật.
Widget _wrapApp(Widget child) => MaterialApp(
  theme: AppTheme.material(AppTheme.forui()),
  builder: (context, inner) =>
      FTheme(data: AppTheme.forui(), child: inner ?? const SizedBox.shrink()),
  home: Scaffold(body: child),
);

/// Khổ máy thật phổ thông (411dp ≈ Pixel) ở cỡ chữ 1× — đúng trường hợp lỗi
/// intrinsic của `AlertDialog` xảy ra, khác với 320dp/1.3× của `layout_test`.
void _phoneSize(WidgetTester tester) {
  tester.view.physicalSize = const Size(411, 891);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
}

/// Cỡ chữ / độ đậm đã vẽ thật của một đoạn chữ trên màn.
({double? size, FontWeight? weight}) _measured(
  WidgetTester tester,
  String text,
) {
  for (final element in find.byType(RichText).evaluate()) {
    final paragraph = element.renderObject! as RenderParagraph;
    if (paragraph.text.toPlainText() != text) continue;
    return (
      size: paragraph.text.style?.fontSize,
      weight: paragraph.text.style?.fontWeight,
    );
  }
  fail('Không tìm thấy đoạn chữ "$text" trên màn');
}

void main() {
  testWidgets('AppTextField phát onChanged theo nội dung nhập', (tester) async {
    final controller = TextEditingController();
    final changes = <String>[];

    await tester.pumpWidget(
      _wrap(
        AppTextField(
          controller: controller,
          label: 'Từ khoá',
          onChanged: changes.add,
        ),
      ),
    );

    await tester.enterText(find.byType(EditableText).first, 'hop dong');
    await tester.pump();

    expect(controller.text, 'hop dong');
    expect(changes.last, 'hop dong');

    controller.dispose();
  });

  testWidgets(
    'AppTextField báo cả khi code tự đặt nội dung (nút xoá từ khoá)',
    (tester) async {
      final controller = TextEditingController();
      final changes = <String>[];

      await tester.pumpWidget(
        _wrap(
          AppTextField(
            controller: controller,
            label: 'Từ khoá',
            onChanged: changes.add,
          ),
        ),
      );

      controller.text = 'dat tu code';
      await tester.pump();
      expect(changes, ['dat tu code']);

      // Cùng nội dung (chỉ đổi con trỏ) không báo lặp.
      controller.selection = const TextSelection.collapsed(offset: 0);
      await tester.pump();
      expect(changes, ['dat tu code']);

      controller.dispose();
    },
  );

  testWidgets('AppTextField đổi controller thì không còn nghe controller cũ', (
    tester,
  ) async {
    final first = TextEditingController();
    final second = TextEditingController();
    final changes = <String>[];

    await tester.pumpWidget(
      _wrap(
        AppTextField(
          controller: first,
          label: 'Từ khoá',
          onChanged: changes.add,
        ),
      ),
    );

    await tester.pumpWidget(
      _wrap(
        AppTextField(
          controller: second,
          label: 'Từ khoá',
          onChanged: changes.add,
        ),
      ),
    );

    first.text = 'bo qua';
    await tester.pump();
    expect(changes, isEmpty);

    second.text = 'ghi nhan';
    await tester.pump();
    expect(changes, ['ghi nhan']);

    first.dispose();
    second.dispose();
  });

  testWidgets('AppPrimaryButton gọi onPressed', (tester) async {
    var pressed = 0;

    await tester.pumpWidget(
      _wrap(
        Column(
          children: [
            AppPrimaryButton(label: 'Lưu', onPressed: () => pressed++),
          ],
        ),
      ),
    );

    await tester.tap(find.text('Lưu'));
    // Forui FTappable hẹn timer 100ms sau tap; đẩy đồng hồ giả để dọn sạch.
    await tester.pump(const Duration(milliseconds: 250));
    expect(pressed, 1);
  });

  testWidgets('AppPrimaryButton khi busy hiện vòng xoay và không gọi lại', (
    tester,
  ) async {
    var pressed = 0;

    await tester.pumpWidget(
      _wrap(
        Column(
          children: [
            AppPrimaryButton(
              label: 'Lưu',
              busy: true,
              onPressed: () => pressed++,
            ),
          ],
        ),
      ),
    );

    expect(find.byType(CircularProgressIndicator), findsOneWidget);

    await tester.tap(find.text('Lưu'));
    await tester.pump(const Duration(milliseconds: 250));
    expect(pressed, 0, reason: 'Nút đang busy không được gọi lại');

    // Dọn widget để vòng xoay vô hạn không để lại timer treo.
    await tester.pumpWidget(_wrap(const SizedBox.shrink()));
    await tester.pump();
  });

  testWidgets(
    'nút của app nằm trong AlertDialog vẫn dựng được hàng nút',
    (tester) async {
      // `AlertDialog` bọc nội dung và hàng nút trong `IntrinsicWidth`, mà
      // `LayoutBuilder` — cách chặn bề ngang nhãn trước đây — **không trả lời
      // được truy vấn intrinsic**. Hậu quả trên máy thật (411dp, cỡ chữ 1×): ném
      // "LayoutBuilder does not support returning intrinsic dimensions", hàng
      // nút không dựng xong và **nút Lưu biến mất** khỏi hộp thoại. Test cũ ở
      // 320dp/1.3× không bắt được vì ở khổ đó `OverflowBar` xếp nút theo chiều
      // dọc nên không hỏi intrinsic.
      _phoneSize(tester);

      await tester.pumpWidget(
        _wrapApp(
          Builder(
            builder: (context) => TextButton(
              onPressed: () => showDialog<void>(
                context: context,
                builder: (context) => AlertDialog(
                  title: const Text('Sửa tenant'),
                  content: Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      AppTextField(
                        controller: TextEditingController(text: 'Sở TNMT'),
                        label: 'Tên tenant',
                      ),
                      const SizedBox(height: 12),
                      // Nút trong **nội dung** cũng bị `IntrinsicWidth` hỏi
                      // intrinsic, không chỉ hàng nút.
                      AppSecondaryButton(label: 'Tải logo', onPressed: _noop),
                    ],
                  ),
                  actions: [
                    TextButton(onPressed: () {}, child: const Text('Huỷ')),
                    AppPrimaryButton(
                      label: 'Lưu',
                      expand: false,
                      onPressed: () {},
                    ),
                    AppDestructiveButton(
                      label: 'Xoá tenant',
                      onPressed: () {},
                    ),
                  ],
                ),
              ),
              child: const Text('Mở hộp thoại'),
            ),
          ),
        ),
      );

      await tester.tap(find.text('Mở hộp thoại'));
      await tester.pumpAndSettle();

      expect(find.text('Lưu'), findsOneWidget, reason: 'nút Lưu không dựng ra');
      expect(find.text('Tải logo'), findsOneWidget);
      expect(find.text('Xoá tenant'), findsOneWidget);
      expect(
        tester.takeException(),
        isNull,
        reason: 'hộp thoại quản trị ném lỗi bố cục',
      );
    },
  );

  testWidgets('AppAlert hiển thị thông báo lỗi', (tester) async {
    await tester.pumpWidget(
      _wrap(
        const AppAlert(message: 'Không kết nối được server', isError: true),
      ),
    );

    expect(find.text('Không kết nối được server'), findsOneWidget);
  });

  testWidgets('AppEmptyState không in đậm câu thông báo', (tester) async {
    await tester.pumpWidget(
      _wrapApp(
        const AppEmptyState(
          message: 'Chưa có tài liệu nào.',
          hint: 'Tải tệp lên để bắt đầu.',
        ),
      ),
    );

    expect(_measured(tester, 'Chưa có tài liệu nào.'), (
      size: 14.0,
      weight: FontWeight.w400,
    ));
    expect(_measured(tester, 'Tải tệp lên để bắt đầu.'), (
      size: 12.0,
      weight: FontWeight.w400,
    ));
  });

  testWidgets('AppAlert dùng đúng thang chữ cho tiêu đề và nội dung', (
    tester,
  ) async {
    // Trước đây thông báo một dòng bị Forui vẽ bằng cỡ chữ tiêu đề, nên cùng
    // một lỗi lại to hơn ở màn quản trị so với màn đăng nhập. Cả hai dòng giờ
    // nằm trên cỡ chuẩn 14; tiêu đề chỉ khác độ đậm (w600) và màu.
    await tester.pumpWidget(
      _wrapApp(
        const Column(
          children: [
            AppAlert(message: 'Tải dữ liệu thất bại', isError: true),
            AppAlert(
              title: 'Không tải được',
              message: 'Thử lại sau ít phút.',
              isError: true,
            ),
          ],
        ),
      ),
    );

    expect(_measured(tester, 'Tải dữ liệu thất bại'), (
      size: 14.0,
      weight: FontWeight.w400,
    ));
    expect(_measured(tester, 'Không tải được'), (
      size: 14.0,
      weight: FontWeight.w600,
    ));
    expect(_measured(tester, 'Thử lại sau ít phút.'), (
      size: 14.0,
      weight: FontWeight.w400,
    ));
  });

  testWidgets('AppMenuButton thật sự mở được menu và trả về mục đã chọn', (
    tester,
  ) async {
    // Lỗi đã xảy ra: nút ba chấm là `child` của `FPopoverMenu` với `onPress`
    // rổng, nên nó nuốt cú nhấn và **không menu nào trong app mở được** (phiên
    // chat, tenant, agent, lịch, MCP…). Test này bấm thật vào nút.
    final picked = <String>[];

    await tester.pumpWidget(
      _wrapApp(
        Center(
          child: AppMenuButton(
            tooltip: 'Tuỳ chọn',
            items: [
              AppMenuItem('Sửa', () => picked.add('sua')),
              AppMenuItem('Xoá', () => picked.add('xoa'), destructive: true),
            ],
          ),
        ),
      ),
    );
    await tester.pump();

    expect(find.text('Sửa'), findsNothing);

    await tester.tap(find.byIcon(Icons.more_vert));
    await tester.pumpAndSettle();

    expect(find.text('Sửa'), findsOneWidget);
    expect(find.text('Xoá'), findsOneWidget);

    await tester.tap(find.text('Xoá'));
    await tester.pumpAndSettle();

    expect(picked, ['xoa']);
    expect(find.text('Xoá'), findsNothing, reason: 'chọn xong menu phải đóng lại');
  });
}

void _noop() {}
