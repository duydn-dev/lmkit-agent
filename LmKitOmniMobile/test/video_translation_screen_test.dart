import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:lmkit_omni_mobile/features/video_translation/video_translation_screen.dart';

class FakeHandle implements VideoControllerHandle {
  bool initialized = false;
  bool disposed = false;
  final bool failInit;
  FakeHandle({this.failInit = false});

  @override
  Future<void> initialize() async {
    if (failInit) throw StateError('decode error');
    initialized = true;
  }

  @override
  Future<void> dispose() async {
    disposed = true;
  }

  @override
  bool get isInitialized => initialized;

  @override
  String get debugLabel => 'fake';

  @override
  Widget buildPlayer() => Container(key: const Key('fake-player'));
}

class FakeFactory implements VideoControllerFactory {
  final List<FakeHandle> created = [];
  bool failNext = false;
  String? lastFilePath;
  Uri? lastUrl;

  @override
  VideoControllerHandle file(String path) {
    lastFilePath = path;
    final h = FakeHandle(failInit: failNext);
    created.add(h);
    return h;
  }

  @override
  VideoControllerHandle network(Uri uri) {
    lastUrl = uri;
    final h = FakeHandle(failInit: failNext);
    created.add(h);
    return h;
  }
}

class FakePicker implements VideoPickerService {
  String? nextPath;
  @override
  Future<String?> pickVideoPath() async => nextPath;
}

Future<void> pumpScreen(
  WidgetTester tester, {
  required FakePicker picker,
  required FakeFactory factory,
}) async {
  await tester.pumpWidget(MaterialApp(
    home: VideoTranslationScreen(picker: picker, controllerFactory: factory),
  ));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('trạng thái trống: chưa có player, export disabled',
      (tester) async {
    final picker = FakePicker();
    final factory = FakeFactory();
    await pumpScreen(tester, picker: picker, factory: factory);

    expect(find.text('Dịch video'), findsOneWidget);
    expect(find.byKey(const Key('fake-player')), findsNothing);
    final exportBtn =
        tester.widget<FilledButton>(find.byKey(const Key('export-btn')));
    expect(exportBtn.onPressed, isNull);
    expect(find.textContaining('Phụ đề minh họa — AI chưa kết nối'),
        findsNothing);
  });

  testWidgets('nhập URL không tự preview khi chưa bấm nút', (tester) async {
    final picker = FakePicker();
    final factory = FakeFactory();
    await pumpScreen(tester, picker: picker, factory: factory);

    await tester.enterText(
        find.byKey(const Key('video-url-field')), 'https://example.com/v.mp4');
    await tester.pump();
    expect(factory.created, isEmpty);
    expect(find.byKey(const Key('fake-player')), findsNothing);
  });

  testWidgets('Preview URL hợp lệ mới tạo network controller', (tester) async {
    final picker = FakePicker();
    final factory = FakeFactory();
    await pumpScreen(tester, picker: picker, factory: factory);

    await tester.enterText(
        find.byKey(const Key('video-url-field')), 'https://example.com/v.mp4');
    await tester.tap(find.byKey(const Key('preview-btn')));
    await tester.pumpAndSettle();

    expect(factory.lastUrl.toString(), 'https://example.com/v.mp4');
    expect(find.byKey(const Key('fake-player')), findsOneWidget);
    expect(find.textContaining('Phụ đề minh họa — AI chưa kết nối'),
        findsOneWidget);
  });

  testWidgets('URL sai hiện lỗi, không tạo controller', (tester) async {
    final picker = FakePicker();
    final factory = FakeFactory();
    await pumpScreen(tester, picker: picker, factory: factory);

    await tester.enterText(
        find.byKey(const Key('video-url-field')), 'not-a-url');
    await tester.tap(find.byKey(const Key('preview-btn')));
    await tester.pumpAndSettle();

    expect(factory.created, isEmpty);
    expect(find.byKey(const Key('video-error')), findsOneWidget);
  });

  testWidgets('URL Facebook hiện cảnh báo giới hạn', (tester) async {
    final picker = FakePicker();
    final factory = FakeFactory();
    await pumpScreen(tester, picker: picker, factory: factory);

    await tester.enterText(find.byKey(const Key('video-url-field')),
        'https://www.facebook.com/watch/?v=123');
    await tester.tap(find.byKey(const Key('preview-btn')));
    await tester.pumpAndSettle();

    expect(find.textContaining('Facebook/YouTube'), findsOneWidget);
  });

  testWidgets('chọn file dùng picker seam, đổi nguồn dispose cũ',
      (tester) async {
    final picker = FakePicker()..nextPath = '/tmp/clip.mp4';
    final factory = FakeFactory();
    await pumpScreen(tester, picker: picker, factory: factory);

    await tester.tap(find.byKey(const Key('pick-file-btn')));
    await tester.pumpAndSettle();
    expect(factory.lastFilePath, '/tmp/clip.mp4');
    expect(find.byKey(const Key('fake-player')), findsOneWidget);
    final first = factory.created.single as FakeHandle;

    picker.nextPath = '/tmp/clip2.mp4';
    await tester.tap(find.byKey(const Key('pick-file-btn')));
    await tester.pumpAndSettle();
    expect(first.disposed, isTrue);
    expect(factory.created.length, 2);
  });

  testWidgets('lỗi decode hiện media-error', (tester) async {
    final picker = FakePicker();
    final factory = FakeFactory()..failNext = true;
    await pumpScreen(tester, picker: picker, factory: factory);

    await tester.enterText(
        find.byKey(const Key('video-url-field')), 'https://example.com/v.mp4');
    await tester.tap(find.byKey(const Key('preview-btn')));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('media-error')), findsOneWidget);
  });

  testWidgets('locale picker tìm và chọn được Tiếng Việt', (tester) async {
    final picker = FakePicker();
    final factory = FakeFactory();
    await pumpScreen(tester, picker: picker, factory: factory);

    await tester.tap(find.byKey(const Key('language-picker-btn')));
    await tester.pumpAndSettle();
    await tester.enterText(
        find.byKey(const Key('language-search-field')), 'tieng viet');
    await tester.pumpAndSettle();
    await tester.tap(find.textContaining('(vi)').first);
    await tester.pumpAndSettle();

    expect(
        tester
            .widgetList<OutlinedButton>(find.byKey(
                const Key('language-picker-btn')))
            .single
            .child,
        isA<Text>().having((t) => t.data ?? '', 'label',
            contains('Tiếng Việt')));
  });

  testWidgets('rời màn dispose controller', (tester) async {
    final picker = FakePicker();
    final factory = FakeFactory();
    await pumpScreen(tester, picker: picker, factory: factory);

    await tester.enterText(
        find.byKey(const Key('video-url-field')), 'https://example.com/v.mp4');
    await tester.tap(find.byKey(const Key('preview-btn')));
    await tester.pumpAndSettle();
    final handle = factory.created.single as FakeHandle;

    await tester.pumpWidget(const MaterialApp(home: Scaffold()));
    await tester.pumpAndSettle();
    expect(handle.disposed, isTrue);
  });
}
