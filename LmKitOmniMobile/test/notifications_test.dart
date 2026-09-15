import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:lmkit_omni_mobile/features/notifications/notifications_screen.dart';

/// Bản giả của controller: giữ nguyên logic cập nhật state nhưng không gọi API.
class _FakeNotifications extends NotificationsController {
  _FakeNotifications(this.items);

  final List<NotificationModel> items;
  final markedRead = <String>[];
  int markAllCalls = 0;

  @override
  Future<List<NotificationModel>> build() async => items;

  @override
  Future<void> markRead(NotificationModel notification) async {
    markedRead.add(notification.id);
    final current = state.asData?.value ?? const <NotificationModel>[];
    state = AsyncData([
      for (final item in current)
        item.id == notification.id
            ? NotificationModel(
                id: item.id,
                title: item.title,
                body: item.body,
                isRead: true,
                createdAt: item.createdAt,
              )
            : item,
    ]);
  }

  @override
  Future<void> markAllRead() async {
    markAllCalls++;
    final current = state.asData?.value ?? const <NotificationModel>[];
    state = AsyncData([
      for (final item in current)
        NotificationModel(
          id: item.id,
          title: item.title,
          body: item.body,
          isRead: true,
          createdAt: item.createdAt,
        ),
    ]);
  }
}

NotificationModel _item(String id, {bool isRead = false}) => NotificationModel(
  id: id,
  title: 'Thông báo $id',
  body: 'Nội dung $id',
  isRead: isRead,
  createdAt: DateTime.now(),
);

Widget _app(Widget child, _FakeNotifications controller) => ProviderScope(
  overrides: [notificationsProvider.overrideWith(() => controller)],
  child: MaterialApp(
    builder: (context, inner) => FTheme(
      data: FTheme.neutral.light.touch,
      child: inner ?? const SizedBox.shrink(),
    ),
    home: child,
  ),
);

void main() {
  test('NotificationModel đọc đúng JSON và mặc định chưa đọc', () {
    final model = NotificationModel.fromJson({
      'id': 'n1',
      'title': 'Tài liệu đã xử lý',
      'body': 'xong',
      'isRead': false,
      'createdAt': '2026-09-14T10:00:00Z',
    });

    expect(model.title, 'Tài liệu đã xử lý');
    expect(model.isRead, isFalse);
    expect(model.createdAt, isNotNull);

    final minimal = NotificationModel.fromJson(const {'id': 'n2'});
    expect(minimal.isRead, isFalse);
    expect(minimal.body, isEmpty);
  });

  testWidgets('chuông hiển thị badge số chưa đọc và ẩn khi đã đọc hết', (
    tester,
  ) async {
    final controller = _FakeNotifications([
      _item('1'),
      _item('2'),
      _item('3', isRead: true),
    ]);

    await tester.pumpWidget(
      _app(
        Scaffold(
          appBar: AppBar(
            title: const Text('CILA AI'),
            actions: const [NotificationBell()],
          ),
        ),
        controller,
      ),
    );
    await tester.pump();

    expect(find.text('2'), findsOneWidget);
    expect(find.byIcon(Icons.notifications), findsOneWidget);
  });

  testWidgets('đọc hết thông báo thì badge biến mất', (tester) async {
    final controller = _FakeNotifications([
      _item('1', isRead: true),
      _item('2', isRead: true),
    ]);

    await tester.pumpWidget(
      _app(
        Scaffold(
          appBar: AppBar(
            title: const Text('CILA AI'),
            actions: const [NotificationBell()],
          ),
        ),
        controller,
      ),
    );
    await tester.pump();

    expect(find.byIcon(Icons.notifications_none), findsOneWidget);
    expect(find.text('0'), findsNothing);
  });

  testWidgets('danh sách hiện thông báo và bấm vào sẽ đánh dấu đã đọc', (
    tester,
  ) async {
    final controller = _FakeNotifications([_item('1'), _item('2')]);

    await tester.pumpWidget(_app(const NotificationsScreen(), controller));
    await tester.pumpAndSettle();

    expect(find.text('Thông báo 1'), findsOneWidget);
    expect(find.text('Thông báo 2'), findsOneWidget);
    expect(find.byIcon(Icons.notifications_active), findsNWidgets(2));

    await tester.tap(find.text('Thông báo 1'));
    await tester.pumpAndSettle();

    expect(controller.markedRead, ['1']);
    // Mục vừa đọc đổi icon và không còn bấm lại được.
    expect(find.byIcon(Icons.notifications_active), findsOneWidget);
    expect(find.byIcon(Icons.notifications_none), findsOneWidget);
  });

  testWidgets('nút đánh dấu tất cả đã đọc gọi API một lần', (tester) async {
    final controller = _FakeNotifications([_item('1'), _item('2')]);

    await tester.pumpWidget(_app(const NotificationsScreen(), controller));
    await tester.pumpAndSettle();

    await tester.tap(find.byIcon(Icons.done_all));
    await tester.pumpAndSettle();

    expect(controller.markAllCalls, 1);
  });

  testWidgets('danh sách rỗng hiển thị thông báo trống', (tester) async {
    final controller = _FakeNotifications(const []);

    await tester.pumpWidget(_app(const NotificationsScreen(), controller));
    await tester.pumpAndSettle();

    expect(find.text('Không có thông báo nào.'), findsOneWidget);
  });
}
