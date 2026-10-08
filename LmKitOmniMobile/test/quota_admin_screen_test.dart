import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:lmkit_omni_mobile/app/theme.dart';
import 'package:lmkit_omni_mobile/app/ui/app_controls.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_models.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_provider.dart';
import 'package:lmkit_omni_mobile/core/config/app_config.dart';
import 'package:lmkit_omni_mobile/core/config/app_config_provider.dart';
import 'package:lmkit_omni_mobile/features/admin/quota_admin_screen.dart';

/// Màn Hạn mức & Token — kiểm cả đường ĐỌC lẫn đường GHI.
///
/// `mock_screens_test.dart` chỉ chứng minh màn *dựng được* với dữ liệu mẫu. Ở đây bấm thật:
/// mở hộp thoại, nhập, bấm Lưu/Cấp thêm và đọc kết quả hiện ra. Đây đúng là vùng mà lỗi
/// "hộp thoại Forui không có tổ tiên Material" từng làm hỏng màn, và cũng là chỗ duy nhất
/// chứng minh được nút Lưu thực sự gửi request chứ không chỉ vẽ ra.
///
/// Màn chạy trên `MockHttpAdapter` nên đi trọn đường code thật (provider → repository →
/// Dio → parse) mà không cần máy chủ; xem `mock_adapter_test.dart`.
const _mockConfig = AppConfig(
  apiBaseUrl: 'http://localhost:5032',
  webBaseUrl: 'http://localhost:5173',
  flavor: 'test-mock',
  connectTimeout: Duration(seconds: 20),
  receiveTimeout: Duration(seconds: 300),
  sendTimeout: Duration(seconds: 120),
  logHttp: false,
  useMockData: true,
);

const _tenantName =
    'Trung tâm Thông tin lưu trữ và Thư viện tài nguyên môi trường quốc gia';

final _session = AuthSession(
  accessToken: 'access',
  refreshToken: 'refresh',
  accessTokenExpiresAt: DateTime(2030),
  refreshTokenExpiresAt: DateTime(2030),
  user: UserModel(
    id: 'u1',
    email: 'admin@cila.gov.vn',
    fullName: 'Nguyễn Văn Duy',
    role: 'Admin',
    tenantId: 't1',
    tenant: const TenantBranding(
      name: _tenantName,
      agentName: 'Trợ lý CILA',
      logoUrl: '/api/tenant-branding/logo?v=demo',
    ),
  ),
);

class _FakeAuth extends AuthController {
  @override
  Future<AuthSession?> build() async => _session;
}

/// Cao hơn màn điện thoại để cả bốn đơn vị và hai khối đều nằm trong tầm nhìn: test này kiểm
/// hành vi, còn bố cục ở hai cỡ màn hẹp đã do `mock_screens_test.dart` lo.
Future<void> _pump(WidgetTester tester) async {
  tester.view.physicalSize = const Size(430, 1600);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);

  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        bootstrapAppConfigProvider.overrideWithValue(_mockConfig),
        authControllerProvider.overrideWith(_FakeAuth.new),
      ],
      child: MaterialApp(
        theme: AppTheme.material(AppTheme.forui()),
        builder: (context, inner) =>
            FTheme(data: AppTheme.forui(), child: inner ?? const SizedBox()),
        home: const QuotaAdminScreen(),
      ),
    ),
  );

  // Mỗi request mẫu có độ trễ 80ms; chờ đủ để danh sách gói + đơn vị tải xong.
  for (var i = 0; i < 12; i++) {
    await tester.pump(const Duration(milliseconds: 120));
  }
}

/// Chờ một thao tác GHI đi hết đường: hộp thoại đóng, request mẫu trả về (80ms) và danh sách
/// được tải lại.
///
/// `pumpAndSettle` một mình là KHÔNG đủ: nó dừng ngay khi không còn khung hình nào được lên
/// lịch, mà request mẫu chỉ giữ một `Timer` — timer không làm `pumpAndSettle` chờ. Thiếu bước
/// bơm theo thời gian ở đây thì mọi chốt kiểm thông báo thành công đều đỏ dù sản phẩm đúng.
Future<void> _settle(WidgetTester tester) async {
  for (var i = 0; i < 12; i++) {
    await tester.pump(const Duration(milliseconds: 120));
  }
  await tester.pumpAndSettle();
}

/// Menu ba chấm của MỘT dòng, tìm theo tên đơn vị để không phụ thuộc thứ tự trong cây.
Finder _rowMenu(String rowText) => find.descendant(
  of: find.ancestor(
    of: find.text(rowText),
    matching: find.byType(AppTile),
  ),
  matching: find.byIcon(Icons.more_vert),
);

void main() {
  // Bộ lưu phiên trong bộ nhớ là điều kiện BẮT BUỘC để màn tải được dữ liệu: thiếu nó thì
  // kênh platform của `flutter_secure_storage` không có người trả lời, request treo ở bước
  // đọc token và màn quay vô tận mà KHÔNG ném lỗi nào — đúng kiểu hỏng im lặng mà test này
  // phải bắt được (đã gặp thật khi viết test này).
  setUp(() => FlutterSecureStorage.setMockInitialValues({}));

  testWidgets('hiện gói và hạn mức của mọi đơn vị từ dữ liệu mẫu', (
    tester,
  ) async {
    await _pump(tester);

    expect(tester.takeException(), isNull);
    expect(find.text('Gói cơ quan — 5 triệu token'), findsWidgets);
    expect(find.text('Gói nội bộ không giới hạn'), findsWidgets);
    // Gói đã ngừng vẫn phải hiện: nó là câu trả lời cho "vì sao không gán được gói này".
    expect(find.text('Gói dùng thử 2025'), findsOneWidget);

    // `monthlyTokenLimit == 0` là KHÔNG GIỚI HẠN, không phải "0 token/tháng".
    expect(find.textContaining('Không giới hạn'), findsWidgets);

    // Vượt trần: đọc được cả số dùng, trần và phần trăm.
    expect(find.textContaining('5.240.000/5.000.000'), findsOneWidget);
    expect(find.textContaining('(104%)'), findsOneWidget);

    // Đơn vị chưa gán gói: nhánh chữ riêng, không được hiện như gói bình thường.
    expect(find.textContaining('Chưa gán gói'), findsOneWidget);

    // Đơn vị gói không giới hạn vẫn khoe được token đã dùng — nếu chỉ hiện "không giới hạn"
    // thì đơn vị đốt nhiều token nhất lại là đơn vị trông rẻ nhất.
    expect(find.textContaining('đã dùng 4.120.400'), findsOneWidget);

    // Grant còn lại và số dư nằm cùng dòng với gói.
    expect(find.textContaining('grant +380.000'), findsOneWidget);
    expect(find.textContaining('số dư 1.000.000'), findsOneWidget);
  });

  testWidgets('Thêm gói: mở hộp thoại, Lưu thì báo thành công', (tester) async {
    await _pump(tester);

    await tester.ensureVisible(find.text('Thêm gói'));
    await tester.tap(find.text('Thêm gói'));
    await tester.pumpAndSettle();

    // Hộp thoại của Forui dựng ở route riêng, không có `Material` bao ngoài: mở được tới
    // đây mà không ném là điều kiện đầu tiên, và `AppTextField` là widget của hệ thiết kế
    // (không phải `TextField` Material) nên đúng là thứ được phép nằm trong đó.
    expect(find.byType(AppTextField), findsNWidgets(2));
    expect(tester.takeException(), isNull);

    await tester.enterText(find.byType(AppTextField).at(0), 'Gói thử nghiệm');
    await tester.enterText(find.byType(AppTextField).at(1), '1200000');
    await tester.tap(find.text('Lưu'));
    await _settle(tester);

    expect(find.text('Đã thêm gói.'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('Thêm gói: bỏ trống tên thì báo lỗi và KHÔNG gửi request', (
    tester,
  ) async {
    await _pump(tester);

    await tester.ensureVisible(find.text('Thêm gói'));
    await tester.tap(find.text('Thêm gói'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Lưu'));
    await _settle(tester);

    expect(find.text('Vui lòng nhập tên gói.'), findsOneWidget);
    // Chốt hai chiều: dữ liệu mẫu LUÔN trả thành công cho mọi thao tác ghi, nên nếu lớp kiểm
    // tra phía màn không chạy thì thông báo thành công sẽ hiện ra ở đây.
    expect(find.text('Đã thêm gói.'), findsNothing);
  });

  testWidgets('Thêm gói: hạn mức không phải số thì bị chặn tại màn', (
    tester,
  ) async {
    await _pump(tester);

    await tester.ensureVisible(find.text('Thêm gói'));
    await tester.tap(find.text('Thêm gói'));
    await tester.pumpAndSettle();
    await tester.enterText(find.byType(AppTextField).at(0), 'Gói sai hạn mức');
    await tester.enterText(find.byType(AppTextField).at(1), 'một triệu');
    await tester.tap(find.text('Lưu'));
    await _settle(tester);

    expect(
      find.text('Hạn mức phải là số không âm (0 = không giới hạn).'),
      findsOneWidget,
    );
    expect(find.text('Đã thêm gói.'), findsNothing);
  });

  testWidgets('Cấp thêm token: grant nạp TRƯỚC khi mở hộp thoại', (tester) async {
    await _pump(tester);

    final menu = _rowMenu(_tenantName);
    await tester.ensureVisible(menu);
    await tester.tap(menu);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Cấp thêm token'));
    await tester.pumpAndSettle();

    // Danh sách grant phải có sẵn ngay lúc hộp thoại hiện: không vòng xoay (widget chỉ
    // Material mới dựng được — sẽ ném trong hộp thoại Forui) và không nhảy một nhịp.
    expect(find.text('Các grant đã cấp'), findsOneWidget);
    expect(find.byType(CircularProgressIndicator), findsNothing);
    expect(find.textContaining('Bù hạn mức quý IV'), findsOneWidget);
    // Tên dòng grant là số token còn nguyên của lần cấp, không phải phần còn lại.
    expect(find.text('500.000 token'), findsOneWidget);
    expect(find.text('250.000 token'), findsOneWidget);
    expect(find.text('100.000 token'), findsOneWidget);
    // Grant đã tiêu một phần vẫn hiện số còn lại; grant không hạn ghi rõ "vô hạn".
    expect(find.textContaining('còn 380.000'), findsOneWidget);
    expect(find.textContaining('còn 250.000 · vô hạn'), findsOneWidget);
    // Grant đã hết hạn vẫn nằm trong danh sách để người quản trị thấy đã cấp gì.
    expect(find.textContaining('Đợt tập huấn tháng 6'), findsOneWidget);

    await tester.enterText(find.byType(AppTextField).at(0), '250000');
    await tester.tap(find.text('Cấp thêm'));
    await _settle(tester);

    expect(find.text('Đã cấp thêm token.'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('Cấp thêm token: số token 0 bị chặn tại màn', (tester) async {
    await _pump(tester);

    final menu = _rowMenu(_tenantName);
    await tester.ensureVisible(menu);
    await tester.tap(menu);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Cấp thêm token'));
    await tester.pumpAndSettle();

    await tester.enterText(find.byType(AppTextField).at(0), '0');
    await tester.tap(find.text('Cấp thêm'));
    await _settle(tester);

    expect(find.text('Số token phải lớn hơn 0.'), findsOneWidget);
    expect(find.text('Đã cấp thêm token.'), findsNothing);
  });

  testWidgets('Gỡ gói: hỏi xác nhận trước khi gọi API', (tester) async {
    await _pump(tester);

    final menu = _rowMenu(_tenantName);
    await tester.ensureVisible(menu);
    await tester.tap(menu);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Gỡ gói'));
    await tester.pumpAndSettle();

    // Xác nhận của app chỉ có MỘT hộp thoại dùng chung, nên chữ ở đây phải khớp nó.
    expect(find.textContaining('Gỡ gói khỏi'), findsOneWidget);

    await tester.tap(find.text('Xoá'));
    await _settle(tester);

    expect(find.text('Đã gỡ gói.'), findsOneWidget);
  });

  testWidgets('gói đã ngừng dùng không hiện hành động "Ngừng dùng gói"', (
    tester,
  ) async {
    await _pump(tester);

    final menu = _rowMenu('Gói dùng thử 2025');
    await tester.ensureVisible(menu);
    await tester.tap(menu);
    await tester.pumpAndSettle();

    // Menu vẫn có "Sửa gói"; thao tác ngừng dùng chỉ có nghĩa với gói đang dùng nên bị ẩn,
    // thay vì hiện ra rồi để server từ chối.
    expect(find.text('Sửa gói'), findsOneWidget);
    expect(find.text('Ngừng dùng gói'), findsNothing);
  });
}
