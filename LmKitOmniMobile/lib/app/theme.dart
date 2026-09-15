import 'package:flutter/material.dart';
import 'package:forui/forui.dart';

/// Design system của app, đối chiếu 1–1 với token trong
/// `LmKitOmniClient/src/style.css` (`@theme` của Tailwind v4).
///
/// Ba trụ cố định của nhận diện khối Chính phủ trên web:
/// - **Đỏ quốc kỳ** `gov-red #b81f33` cho viền tiêu điểm và trạng thái nguy hiểm.
/// - **Vàng sao** `gov-yellow #ffcd00` cho điểm nhấn phụ.
/// - **Xanh nước biển đậm** `gov-blue-dark #1e3a8a` cho toàn bộ chrome (AppBar,
///   nút chính, mục đang chọn) — web dùng đúng token này cho `header` và nút
///   `Đăng nhập`.
///
/// App **chỉ có theme sáng**, đúng như web: `style.css` ghi rõ
/// "The application is intentionally light-themed" và ép `color-scheme: light`
/// để chế độ tối của hệ điều hành không đổi màu input. Mobile giữ nguyên quy tắc
/// đó thay vì theo `ThemeMode.system`.
class AppTheme {
  AppTheme._();

  // ------------------------------------------------------------- nhận diện CP

  /// Đỏ quốc kỳ — viền tiêu điểm, trạng thái nguy hiểm.
  static const govRed = Color(0xFFB81F33);
  static const govRedDark = Color(0xFF7F1826);

  /// Vàng sao — điểm nhấn phụ.
  static const govYellow = Color(0xFFFFCD00);

  /// Xanh nước biển đậm — chrome thống nhất (header, sidebar, nút chính).
  static const govBlueDark = Color(0xFF1E3A8A);
  static const govBlueDarker = Color(0xFF172554);

  // ------------------------------------------------------------- nền / chữ

  /// `--color-chatgpt-dark`: nền toàn app.
  static const surface = Color(0xFFF8FAFC);

  /// `--color-chatgpt-light`: nền phụ (hover, chip).
  static const surfaceMuted = Color(0xFFF1F5F9);

  static const textPrimary = Color(0xFF111827);
  static const textMuted = Color(0xFF4B5563);
  static const border = Color(0xFFE2E8F0);
  static const borderStrong = Color(0xFFCBD5E1);

  // ------------------------------------------------------- màu trạng thái

  /// Các token ngữ nghĩa dùng thay cho `Colors.green/orange/...` rải rác, để
  /// trạng thái trông đồng nhất ở mọi màn.
  static const success = Color(0xFF065F46);
  static const successSurface = Color(0xFFECFDF5);
  static const warning = Color(0xFF78350F);
  static const warningSurface = Color(0xFFFFFBEB);
  static const info = govBlueDark;
  static const infoSurface = Color(0xFFEFF6FF);

  /// Web: chip nguồn/tệp đính kèm dùng `bg-blue-50 border-blue-100 text-blue-700`.
  static const infoBorder = Color(0xFFDBEAFE);
  static const infoText = Color(0xFF1D4ED8);

  /// Web: băng lỗi trong form dùng `bg-red-50 border-red-200 text-red-700`
  /// (ví dụ `LoginView.vue`), khác với `gov-red` dùng cho nút phá huỷ.
  static const dangerSurface = Color(0xFFFEF2F2);
  static const dangerBorder = Color(0xFFFECACA);
  static const dangerText = Color(0xFFB91C1C);

  /// Web: băng cảnh báo `bg-orange-50 border-orange-200 text-orange-700`
  /// (thẻ phê duyệt HITL trong Chat).
  static const warningBorder = Color(0xFFFED7AA);
  static const warningText = Color(0xFFC2410C);

  /// Web: thẻ phê duyệt HITL — `bg-orange-50 border-orange-200`.
  static const hitlSurface = Color(0xFFFFF7ED);
  static const hitlBorder = warningBorder;
  static const hitlText = warningText;

  /// Font duy nhất của app — trùng web (`--font-sans: 'Be Vietnam Pro'`).
  static const fontFamily = 'Be Vietnam Pro';

  /// Font cho khối mã/nội dung artifact.
  ///
  /// Web dùng `font-mono` của Tailwind; Flutter không có font mono mặc định nên
  /// phải khai báo tên theo từng hệ điều hành kèm danh sách dự phòng — nếu chỉ
  /// ghi `monospace`, iOS sẽ im lặng rơi về font hệ thống thường.
  static const monoFamily = 'monospace';
  static const monoFallback = <String>['Menlo', 'Consolas', 'Roboto Mono'];

  /// Chiều cao header của web (`h-14` = 56px).
  static const appBarHeight = 56.0;

  /// Bán kính thẻ/ô nhập dùng chung (web: `rounded-xl`/`rounded-lg`).
  static const radius = 12.0;
  static const radiusSmall = 8.0;

  /// Lề ngang chuẩn của mọi trang (web: `px-5` cho header, `p-5` cho nội dung).
  static const pagePadding = EdgeInsets.all(16);

  /// Bảng màu Chính phủ áp lên Forui để nút/thẻ/chip đồng bộ với Material.
  static FColors colors() => FTheme.neutral.light.touch.colors.copyWith(
    background: surface,
    foreground: textPrimary,
    primary: govBlueDark,
    primaryForeground: Colors.white,
    secondary: surfaceMuted,
    secondaryForeground: govBlueDark,
    muted: surfaceMuted,
    mutedForeground: textMuted,
    destructive: govRed,
    destructiveForeground: Colors.white,
    error: govRedDark,
    errorForeground: Colors.white,
    card: Colors.white,
    border: border,
  );

  /// Theme Forui dùng cho cả app (design system chính).
  ///
  /// Forui không cho đổi `colors`/`typography` qua `copyWith`, nên phải tạo
  /// `FThemeData` mới từ bảng màu Chính phủ — cách làm chính thức trong tài liệu
  /// Forui. Chỉ có theme sáng, đúng như web.
  static FThemeData forui() {
    final tokens = colors();
    return FThemeData(
      debugLabel: 'CILA government light',
      touch: true,
      colors: tokens,
      typography: FTypography(
        display: FTypeface.inherit(
          colors: tokens,
          touch: true,
          fontFamily: fontFamily,
        ),
        body: FTypeface.inherit(
          colors: tokens,
          touch: true,
          fontFamily: fontFamily,
        ),
      ),
    );
  }

  /// Theme Material — lớp hạ tầng (Scaffold, AppBar, ListTile, TextField…).
  ///
  /// Web dùng font Be Vietnam Pro cho mọi chữ và chrome xanh `gov-blue-dark`
  /// cho header, nên hai thứ đó được đặt ở đây thay vì ở từng màn.
  static ThemeData material(FThemeData theme) {
    final base = theme.toApproximateMaterialTheme();
    final textTheme = _textTheme(base.textTheme);

    return base.copyWith(
      brightness: Brightness.light,
      scaffoldBackgroundColor: surface,
      canvasColor: surface,
      splashFactory: InkSparkle.splashFactory,
      textTheme: textTheme,
      primaryTextTheme: textTheme.apply(
        bodyColor: Colors.white,
        displayColor: Colors.white,
      ),
      colorScheme: base.colorScheme.copyWith(
        brightness: Brightness.light,
        primary: govBlueDark,
        onPrimary: Colors.white,
        secondary: govBlueDark,
        onSecondary: Colors.white,
        surface: Colors.white,
        onSurface: textPrimary,
        error: govRed,
        onError: Colors.white,
        outline: borderStrong,
        outlineVariant: border,
      ),
      // Viền tiêu điểm đỏ quốc kỳ, đúng `:focus-visible` của web.
      focusColor: govRed,
      appBarTheme: AppBarTheme(
        backgroundColor: govBlueDark,
        foregroundColor: Colors.white,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        scrolledUnderElevation: 0,
        toolbarHeight: appBarHeight,
        centerTitle: false,
        titleTextStyle: const TextStyle(
          fontFamily: fontFamily,
          fontSize: appBarTitleSize,
          fontWeight: FontWeight.w700,
          color: Colors.white,
        ),
        iconTheme: const IconThemeData(color: Colors.white, size: 22),
        actionsIconTheme: const IconThemeData(color: Colors.white, size: 22),
      ),
      cardTheme: CardThemeData(
        color: Colors.white,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        // Mọi danh sách trong app là một cột thẻ (Card trong ListView/Column),
        // nên khoảng cách dưới được đặt ở theme để không màn nào phải tự nhớ.
        margin: const EdgeInsets.only(bottom: 12),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(radius),
          side: const BorderSide(color: border),
        ),
      ),
      dividerTheme: const DividerThemeData(
        color: border,
        thickness: 1,
        space: 1,
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: Colors.white,
        isDense: true,
        contentPadding: const EdgeInsets.symmetric(
          horizontal: 12,
          vertical: 14,
        ),
        labelStyle: const TextStyle(color: textMuted),
        hintStyle: const TextStyle(color: Color(0xFF9CA3AF)),
        helperStyle: const TextStyle(color: textMuted, fontSize: 12),
        border: _inputBorder(borderStrong),
        enabledBorder: _inputBorder(borderStrong),
        disabledBorder: _inputBorder(border),
        focusedBorder: _inputBorder(govBlueDark, width: 1.6),
        errorBorder: _inputBorder(govRed),
        focusedErrorBorder: _inputBorder(govRed, width: 1.6),
      ),
      listTileTheme: const ListTileThemeData(
        iconColor: govBlueDark,
        contentPadding: EdgeInsets.symmetric(horizontal: 16, vertical: 2),
      ),
      dialogTheme: DialogThemeData(
        backgroundColor: Colors.white,
        surfaceTintColor: Colors.transparent,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(radius),
        ),
        titleTextStyle: const TextStyle(
          fontFamily: fontFamily,
          fontSize: 18,
          fontWeight: FontWeight.w600,
          color: textPrimary,
        ),
        contentTextStyle: const TextStyle(
          fontFamily: fontFamily,
          fontSize: 14,
          color: textPrimary,
        ),
      ),
      bottomSheetTheme: const BottomSheetThemeData(
        backgroundColor: Colors.white,
        surfaceTintColor: Colors.transparent,
        showDragHandle: true,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
        ),
      ),
      navigationBarTheme: NavigationBarThemeData(
        backgroundColor: Colors.white,
        surfaceTintColor: Colors.transparent,
        indicatorColor: govBlueDark.withValues(alpha: 0.12),
        elevation: 0,
        height: 64,
        // Nhãn tab: 12 (`text-xs`) — bước nhỏ nhất trên thang chữ vẫn đọc được
        // ở 320dp và khớp `text-xs` mà web dùng cho nhãn phụ.
        labelTextStyle: WidgetStateProperty.resolveWith(
          (states) => TextStyle(
            fontFamily: fontFamily,
            fontSize: 12,
            fontWeight: states.contains(WidgetState.selected)
                ? FontWeight.w600
                : FontWeight.w500,
            color: states.contains(WidgetState.selected)
                ? govBlueDark
                : textMuted,
          ),
        ),
        iconTheme: WidgetStateProperty.resolveWith(
          (states) => IconThemeData(
            size: 22,
            color: states.contains(WidgetState.selected)
                ? govBlueDark
                : textMuted,
          ),
        ),
      ),
      tabBarTheme: const TabBarThemeData(
        labelColor: Colors.white,
        unselectedLabelColor: Color(0xFFCBD5E1),
        indicatorColor: govYellow,
        indicatorSize: TabBarIndicatorSize.tab,
        dividerColor: Colors.transparent,
        labelStyle: TextStyle(
          fontFamily: fontFamily,
          fontSize: 14,
          fontWeight: FontWeight.w600,
        ),
        unselectedLabelStyle: TextStyle(
          fontFamily: fontFamily,
          fontSize: 14,
          fontWeight: FontWeight.w500,
        ),
      ),
      // Nhãn dạng viên của web là `bg-gray-100 text-xs rounded-full` — nền xám
      // nhạt, không phải trắng như mặc định của Material (nền trắng làm viên nhãn
      // chìm vào thẻ trắng bên dưới).
      chipTheme: base.chipTheme.copyWith(
        backgroundColor: surfaceMuted,
        selectedColor: govBlueDark.withValues(alpha: 0.12),
        side: const BorderSide(color: border),
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        labelStyle: const TextStyle(
          fontFamily: fontFamily,
          fontSize: 12,
          fontWeight: FontWeight.w500,
          color: textPrimary,
        ),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(999)),
      ),
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          backgroundColor: govBlueDark,
          foregroundColor: Colors.white,
          minimumSize: const Size(0, 44),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(radiusSmall),
          ),
          textStyle: const TextStyle(
            fontFamily: fontFamily,
            fontWeight: FontWeight.w600,
          ),
        ),
      ),
      textButtonTheme: TextButtonThemeData(
        style: TextButton.styleFrom(
          foregroundColor: govBlueDark,
          textStyle: const TextStyle(
            fontFamily: fontFamily,
            fontWeight: FontWeight.w600,
          ),
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          foregroundColor: govBlueDark,
          side: const BorderSide(color: borderStrong),
          minimumSize: const Size(0, 44),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(radiusSmall),
          ),
        ),
      ),
      snackBarTheme: SnackBarThemeData(
        behavior: SnackBarBehavior.floating,
        backgroundColor: textPrimary,
        contentTextStyle: const TextStyle(
          fontFamily: fontFamily,
          color: Colors.white,
        ),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(radiusSmall),
        ),
      ),
      progressIndicatorTheme: const ProgressIndicatorThemeData(
        color: govBlueDark,
        linearTrackColor: surfaceMuted,
      ),
      switchTheme: SwitchThemeData(
        thumbColor: WidgetStateProperty.resolveWith(
          (states) => states.contains(WidgetState.selected)
              ? Colors.white
              : const Color(0xFFF1F5F9),
        ),
        trackColor: WidgetStateProperty.resolveWith(
          (states) => states.contains(WidgetState.selected)
              ? govBlueDark
              : borderStrong,
        ),
      ),
      popupMenuTheme: PopupMenuThemeData(
        color: Colors.white,
        surfaceTintColor: Colors.transparent,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(radius),
          side: const BorderSide(color: border),
        ),
        textStyle: const TextStyle(
          fontFamily: fontFamily,
          fontSize: 14,
          color: textPrimary,
        ),
      ),
      drawerTheme: const DrawerThemeData(
        backgroundColor: Colors.white,
        surfaceTintColor: Colors.transparent,
        width: 300,
      ),
    );
  }

  static OutlineInputBorder _inputBorder(Color color, {double width = 1}) =>
      OutlineInputBorder(
        borderRadius: BorderRadius.circular(radiusSmall),
        borderSide: BorderSide(color: color, width: width),
      );

  /// Thang chữ duy nhất của app, lấy đúng các bước mà web đang dùng.
  ///
  /// Web viết bằng Tailwind nên cỡ chữ rơi vào vài giá trị cố định; bảng dưới
  /// là ánh xạ 1–1 sang token Material để mọi màn dùng chung thay vì tự đặt
  /// `TextStyle(fontSize: …)`:
  ///
  /// | Token | px | Nguồn bên web |
  /// |---|---|---|
  /// | `displaySmall` | 30 | `text-3xl font-bold` |
  /// | `headlineSmall` | 24 | `text-2xl font-bold` (tiêu đề đăng nhập) |
  /// | `titleLarge` | 20 | `text-xl font-bold` (tiêu đề trang) |
  /// | `titleMedium` | 16 | `text-base font-semibold` (tiêu đề thẻ/mục) |
  /// | `titleSmall` | 14 | `text-sm font-semibold` |
  /// | `bodyLarge` | 16 | `text-base` (nội dung tin nhắn) |
  /// | `bodyMedium` | 14 | `text-sm` (thân mặc định của gần 500 chỗ) |
  /// | `bodySmall` | 12 | `text-xs` (chú thích, mô tả phụ) |
  /// | `labelLarge` | 14 | `text-sm font-medium` (nhãn nút) |
  /// | `labelMedium` | 12 | `text-xs font-medium` |
  /// | `labelSmall` | 11 | `text-[11px] font-semibold uppercase tracking-wider`
  /// (nhãn siêu nhỏ: khối suy luận, huy hiệu) |
  ///
  /// Hai giá trị không nằm trên thang: `appBarTitleSize` (18) là dung hòa cho
  /// bề ngang 320dp, và `badgeSize` (10) theo huy hiệu `text-[9px]` của web.
  static TextTheme _textTheme(TextTheme base) {
    TextStyle token(
      TextStyle? source, {
      required double size,
      FontWeight weight = FontWeight.w400,
      double? height,
      Color? color,
      double? letterSpacing,
    }) => (source ?? const TextStyle()).copyWith(
      fontFamily: fontFamily,
      fontSize: size,
      fontWeight: weight,
      height: height,
      color: color,
      letterSpacing: letterSpacing,
    );

    return base
        .apply(fontFamily: fontFamily)
        .copyWith(
          displaySmall: token(
            base.displaySmall,
            size: 30,
            weight: FontWeight.w700,
          ),
          headlineSmall: token(
            base.headlineSmall,
            size: 24,
            weight: FontWeight.w700,
          ),
          titleLarge: token(
            base.titleLarge,
            size: 20,
            weight: FontWeight.w700,
            color: textPrimary,
          ),
          titleMedium: token(
            base.titleMedium,
            size: 16,
            weight: FontWeight.w600,
            color: textPrimary,
          ),
          titleSmall: token(
            base.titleSmall,
            size: 14,
            weight: FontWeight.w600,
            color: textPrimary,
          ),
          // Tin nhắn chat cần dòng thoáng hơn chữ thường (web: `text-base` với
          // khoảng cách dòng 1.6 trong khối trả lời).
          bodyLarge: token(
            base.bodyLarge,
            size: 16,
            height: 1.6,
            color: textPrimary,
          ),
          bodyMedium: token(
            base.bodyMedium,
            size: 14,
            height: 1.45,
            color: textPrimary,
          ),
          bodySmall: token(
            base.bodySmall,
            size: 12,
            height: 1.35,
            color: textMuted,
          ),
          labelLarge: token(base.labelLarge, size: 14, weight: FontWeight.w600),
          labelMedium: token(
            base.labelMedium,
            size: 12,
            weight: FontWeight.w500,
          ),
          // `tracking-wider` của Tailwind = 0.05em; web dùng `tracking-widest`
          // (0.1em) cho nhãn nhóm nên nhãn siêu nhỏ lấy 0.08em.
          labelSmall: token(
            base.labelSmall,
            size: 11,
            weight: FontWeight.w600,
            letterSpacing: 0.8,
            color: textMuted,
          ),
        );
  }

  /// Cỡ tiêu đề trên AppBar. Web đặt tiêu đề trang ở `text-xl` (20) nhưng nằm
  /// trong vùng nội dung; trên điện thoại 320dp, 20px cộng nút quay lại và hai
  /// nút hành động sẽ bị cắt, nên lấy 18 (`text-lg`) và luôn cắt bằng ellipsis.
  static const appBarTitleSize = 18.0;

  /// Huy hiệu số (thông báo chưa đọc) — web dùng `text-[9px]`.
  static const badgeSize = 10.0;
}
