import 'package:flutter/material.dart';
import 'package:forui/forui.dart';

import '../../features/notifications/notifications_screen.dart';
import '../theme.dart';
import 'button_frame.dart';

export 'button_frame.dart' show buttonHorizontalPadding;

/// Thanh trên cùng của app: y như `AppBar` của Material, chỉ khác **vùng sáng
/// của nút icon**.
///
/// `AppBar` ép `leading` và `actions` vào ô cao bằng cả thanh (56dp) để tiêu đề
/// thẳng hàng, mà Material lại vẽ nước chạm kín ô đó — đo được **56×56** trên
/// header, nên hover/nhấn thành một khối vuông to gần gấp rưỡi nút bình thường.
/// Bọc mỗi nút trong `Center` là cách duy nhất gỡ ràng buộc chặt đó *mà không hạ
/// vùng chạm*: nút vẽ 40×40 (chuẩn Material 3, xem `AppTheme.iconButtonTheme`)
/// còn vùng bấm vẫn 48×48 nhờ `tapTargetSize: padded`.
/// Ngoài `actions` của màn, header còn có **chuông thông báo** ở vị trí cố định —
/// bên trái cụm hành động — để người dùng không phải đi tìm nó ở từng trang, đúng
/// như thanh header dùng chung của web. Màn nào không nên có (chính màn Thông
/// báo) thì truyền `showNotificationBell: false`.
class AppTopBar extends StatelessWidget implements PreferredSizeWidget {
  const AppTopBar({
    super.key,
    this.title,
    this.leading,
    this.actions,
    this.bottom,
    this.showNotificationBell = true,
  });

  final Widget? title;
  final Widget? leading;
  final List<Widget>? actions;
  final PreferredSizeWidget? bottom;
  final bool showNotificationBell;

  @override
  Size get preferredSize => _bar.preferredSize;

  AppBar get _bar =>
      AppBar(toolbarHeight: AppTheme.appBarHeight, bottom: bottom);

  @override
  Widget build(BuildContext context) {
    // Nút quay lại do `AppBar` tự sinh cũng nằm trong ô 56dp — Material chỉ tự
    // bọc `Center` khi `leading` **là** `IconButton`, còn `BackButton` thì không.
    // Tự dựng lấy khi màn này đẩy được về trước, để mọi nút trên header đều cùng
    // một kích thước; trường hợp còn lại (ngăn kéo, `CloseButton`) vẫn để
    // Material quyết định.
    final resolvedLeading =
        leading ?? (Navigator.of(context).canPop() ? const BackButton() : null);

    return AppBar(
      toolbarHeight: AppTheme.appBarHeight,
      // Khe giữa lề và tiêu đề hẹp lại còn 4: mọi nút trên header đều rộng 48
      // (giữ đúng vùng chạm), nên ở máy 320dp chuông + ba hành động là vừa hết
      // chỗ và tiêu đề "AI Chat" bị cắt còn "AI …". Bớt 12 px ở khe này là đủ để
      // tiêu đề đọc trọn mà không phải bỏ bớt nút nào.
      titleSpacing: 4,
      title: title,
      leading: resolvedLeading == null ? null : Center(child: resolvedLeading),
      actions: [
        if (showNotificationBell)
          const Center(
            child: IconTheme(
              data: IconThemeData(color: Colors.white, size: 20),
              child: NotificationBell(),
            ),
          ),
        for (final action in actions ?? const <Widget>[])
          Center(
            // Nút hành động trên header nằm trên nền navy: `IconTheme` trắng ở
            // đây là thứ giữ icon đúng màu cho mọi nút do màn truyền vào.
            child: IconTheme(
              data: const IconThemeData(color: Colors.white, size: 20),
              child: action,
            ),
          ),
      ],
      bottom: bottom,
    );
  }
}

/// Lớp primitive dùng Forui làm design system cho toàn app.
///
/// Mọi màn hình mới nên dùng các widget ở đây thay vì `FilledButton`,
/// `OutlinedButton`, `Card`, `Alert` của Material — nhờ vậy theme, kích thước
/// chạm và trạng thái disabled/loading chỉ được định nghĩa một lần.
///
/// Material vẫn được dùng cho hạ tầng (`Scaffold`, `AppBar`, `ListView`,
/// `IconButton` trên AppBar) vì Forui không thay thế các lớp đó.
/// Kẹp nội dung nút vào đúng bề ngang còn lại sau lề của `FButton`.
///
/// Trừ thẳng có thể ra số âm khi ô chứa rất hẹp (bảng dữ liệu, ô lưới), mà
/// `BoxConstraints` âm thì assert ngay lúc build.
BoxConstraints _labelConstraints(BoxConstraints constraints) => BoxConstraints(
  maxWidth: (constraints.maxWidth - buttonHorizontalPadding).clamp(
    0.0,
    double.infinity,
  ),
);

/// Nút Forui có nhãn: icon/vòng xoay + nhãn, tự cắt nhãn theo chỗ thực có.
///
/// Ba lớp lồng nhau, mỗi lớp một việc — đổi thứ tự là hỏng (xem [ButtonFrame]):
/// [ButtonFrame] (trả lời truy vấn intrinsic cho hộp thoại) → `LayoutBuilder` (đo
/// bề ngang thực có) → `FButton` (vẽ nút).
class _LabeledButton extends StatelessWidget {
  const _LabeledButton({
    required this.label,
    required this.onPressed,
    this.icon,
    this.busy = false,
    this.spinnerColor,
    this.variant = FButtonVariant.primary,
    this.mainAxisSize = MainAxisSize.max,
  });

  final String label;
  final VoidCallback? onPressed;
  final IconData? icon;
  final bool busy;

  /// Màu vòng xoay khi nút đang xử lý (nút nền navy cần màu chữ trên nền chính).
  final Color? spinnerColor;

  final FButtonVariant variant;
  final MainAxisSize mainAxisSize;

  @override
  Widget build(BuildContext context) => ButtonFrame(
    label: label,
    icon: icon,
    busy: busy,
    child: LayoutBuilder(
      builder: (context, constraints) => FButton(
        variant: variant,
        onPress: busy ? null : onPressed,
        mainAxisSize: mainAxisSize,
        child: ConstrainedBox(
          constraints: _labelConstraints(constraints),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              if (busy)
                SizedBox(
                  height: ButtonFrame.spinnerSize,
                  width: ButtonFrame.spinnerSize,
                  child: CircularProgressIndicator(
                    strokeWidth: 2,
                    color: spinnerColor,
                  ),
                )
              else if (icon != null)
                Icon(icon, size: ButtonFrame.iconSize),
              if (busy || icon != null)
                const SizedBox(width: ButtonFrame.iconGap),
              Flexible(child: Text(label, overflow: TextOverflow.ellipsis)),
            ],
          ),
        ),
      ),
    ),
  );
}

/// Nút hành động chính.
///
/// Lưu ý khi đặt trong một hàng có chia bề ngang (`Expanded`/`Row` chật): để
/// nguyên `expand: true` để nhãn dài tự cắt bằng ellipsis. Đặt `expand: false`
/// trong ngữ cảnh bị ép bề ngang sẽ khiến nhãn tràn ra ngoài khi người dùng tăng
/// cỡ chữ hệ thống.
class AppPrimaryButton extends StatelessWidget {
  const AppPrimaryButton({
    super.key,
    required this.label,
    required this.onPressed,
    this.icon,
    this.busy = false,
    this.expand = true,
  });

  final String label;
  final VoidCallback? onPressed;
  final IconData? icon;

  /// Hiển thị trạng thái đang xử lý: khoá nút và thay nhãn.
  final bool busy;
  final bool expand;

  @override
  Widget build(BuildContext context) {
    final button = _LabeledButton(
      label: label,
      icon: icon,
      busy: busy,
      onPressed: onPressed,
      mainAxisSize: expand ? MainAxisSize.max : MainAxisSize.min,
      // Nút chính nền navy: vòng xoay phải lấy màu chữ trên nền chính, nếu để
      // mặc định thì trùng màu nền và gần như vô hình.
      spinnerColor: Theme.of(context).colorScheme.onPrimary,
    );
    return expand ? SizedBox(width: double.infinity, child: button) : button;
  }
}

/// Nút phụ (viền) theo Forui.
class AppSecondaryButton extends StatelessWidget {
  const AppSecondaryButton({
    super.key,
    required this.label,
    required this.onPressed,
    this.icon,
    this.busy = false,
  });

  final String label;
  final VoidCallback? onPressed;
  final IconData? icon;
  final bool busy;

  @override
  Widget build(BuildContext context) => _LabeledButton(
    label: label,
    icon: icon,
    busy: busy,
    onPressed: onPressed,
    variant: FButtonVariant.outline,
    mainAxisSize: MainAxisSize.min,
  );
}

/// Nút hành động nguy hiểm (xoá/thu hồi) theo Forui.
class AppDestructiveButton extends StatelessWidget {
  const AppDestructiveButton({
    super.key,
    required this.label,
    required this.onPressed,
    this.icon,
  });

  final String label;

  /// `null` để khoá nút (ví dụ trong lúc đang xử lý).
  final VoidCallback? onPressed;
  final IconData? icon;

  @override
  Widget build(BuildContext context) => _LabeledButton(
    label: label,
    icon: icon,
    onPressed: onPressed,
    variant: FButtonVariant.destructive,
    mainAxisSize: MainAxisSize.min,
  );
}

/// Nút chỉ có icon (Forui `FButton.icon`, mặc định variant outline).
class AppIconButton extends StatelessWidget {
  const AppIconButton({
    super.key,
    required this.icon,
    required this.onPressed,
    this.tooltip,
    this.color,
    this.busy = false,
  });

  final IconData icon;
  final VoidCallback? onPressed;
  final String? tooltip;

  /// Màu icon riêng (xanh cho xác nhận, đỏ cho phá huỷ…).
  final Color? color;

  /// Thay icon bằng vòng quay nhỏ khi đang xử lý (tải file…).
  final bool busy;

  @override
  Widget build(BuildContext context) {
    // Màu icon lấy từ `IconTheme` ngay tại đây (chứ không phải bên trong
    // `FButton`) để `AppTopBar` chỉ cần bọc `IconTheme` màu trắng là mọi nút
    // hành động trên header xanh ra đúng màu, không phải sửa từng màn.
    final iconColor =
        color ?? IconTheme.of(context).color ?? AppTheme.govBlueDark;
    final child = busy
        ? SizedBox(
            height: 14,
            width: 14,
            child: CircularProgressIndicator(strokeWidth: 2, color: iconColor),
          )
        : Icon(icon, size: 20, color: iconColor);
    // `ghost`: nút icon **không nền, không viền**. Mặc định cũ của `FButton.icon`
    // là `outline` — một ô sáng bo góc — nên trên thanh header navy nó thành
    // "ô vuông trắng" lạc lõng, còn trong thẻ thì nặng hơn nội dung xung quanh.
    final button = FButton.icon(
      variant: FButtonVariant.ghost,
      onPress: onPressed ?? () {},
      semanticsLabel: tooltip,
      child: child,
    );
    return onPressed == null ? Opacity(opacity: 0.5, child: button) : button;
  }
}

/// Ô nhập liệu Forui có nhãn và thông báo lỗi.
///
/// [onChanged] được phát từ listener trên [controller] thay vì dựa vào callback
/// nội bộ của `FTextFieldControl` — như vậy hành vi không phụ thuộc phiên bản
/// Forui và luôn khớp với giá trị controller thực tế.
///
/// Lưu ý: callback bắn cho **mọi** thay đổi của controller, kể cả khi code tự
/// đặt `controller.text` (ví dụ nút xoá từ khoá). Hai call site hiện tại đều là ô
/// tìm kiếm nên việc làm mới kết quả khi xoá từ khoá là hành vi mong muốn.
class AppTextField extends StatefulWidget {
  const AppTextField({
    super.key,
    required this.controller,
    required this.label,
    this.hint,
    this.error,
    this.maxLines = 1,
    this.obscure = false,
    this.keyboardType,
    this.enabled = true,
    this.autofocus = false,
    this.icon,
    this.onChanged,
    this.onSubmitted,
  });

  final TextEditingController controller;
  final String label;
  final String? hint;
  final String? error;
  final int maxLines;
  final bool obscure;
  final TextInputType? keyboardType;
  final bool enabled;

  /// Icon dẫn trước trong ô (kính lúp cho ô tìm kiếm, ổ khoá cho ô mật khẩu…).
  final IconData? icon;

  /// Tự.focus khi hiện (dùng cho form nhập nhanh trong hộp thoại).
  final bool autofocus;
  final ValueChanged<String>? onChanged;
  final ValueChanged<String>? onSubmitted;

  @override
  State<AppTextField> createState() => _AppTextFieldState();
}

class _AppTextFieldState extends State<AppTextField> {
  /// Giá trị controller đã báo lần cuối — chặn bắn lặp khi nội dung không đổi
  /// (ví dụ chỉ thay đổi vị trí con trỏ, vẫn phát notifyListeners).
  late String _last;

  @override
  void initState() {
    super.initState();
    _last = widget.controller.text;
    widget.controller.addListener(_handleChange);
  }

  @override
  void didUpdateWidget(AppTextField oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.controller != widget.controller) {
      oldWidget.controller.removeListener(_handleChange);
      _last = widget.controller.text;
      widget.controller.addListener(_handleChange);
    }
  }

  @override
  void dispose() {
    widget.controller.removeListener(_handleChange);
    super.dispose();
  }

  void _handleChange() {
    final value = widget.controller.text;
    if (value == _last) return;
    _last = value;
    widget.onChanged?.call(value);
  }

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 12),
    child: FTextField(
      control: FTextFieldControl.managed(controller: widget.controller),
      label: Text(widget.label),
      hint: widget.hint,
      error: widget.error == null ? null : Text(widget.error!),
      maxLines: widget.obscure ? 1 : widget.maxLines,
      obscureText: widget.obscure,
      keyboardType: widget.keyboardType,
      enabled: widget.enabled,
      autofocus: widget.autofocus,
      // Dùng lại `prefixIconBuilder` của Forui để icon cùng cỡ/cùng màu với các
      // ô nhập khác trong app (màn đăng nhập cũng đi đường này).
      prefixBuilder: widget.icon == null
          ? null
          : (context, style, variants) => FTextField.prefixIconBuilder(
              context,
              style,
              variants,
              Icon(widget.icon, size: 20),
            ),
      onSubmit: widget.onSubmitted,
    ),
  );
}

/// Thông báo trạng thái/lỗi theo Forui.
///
/// **Không truyền `title` khi thông báo chỉ có một dòng.** Forui vẽ `title` bằng
/// cỡ chữ tiêu đề của nó (đo được: 16/500), nên trước đây cùng một lỗi hiện ra
/// 16/500 ở màn quản trị nhưng 14/400 ở màn đăng nhập (kiểu `text-sm` của web).
/// Widget này ghim cả hai về đúng thang chữ của web — tiêu đề 16/600, nội dung
/// 14/400 — mà vẫn giữ màu theo biến thể (đỏ khi lỗi, xanh khi thường) do
/// `FAlert` quyết định: chỉ đặt cỡ/nét chữ, không đặt màu.
class AppAlert extends StatelessWidget {
  const AppAlert({
    super.key,
    required this.message,
    this.title,
    this.isError = false,
    this.onRetry,
  });

  final String message;
  final String? title;
  final bool isError;
  final VoidCallback? onRetry;

  /// Cỡ/nét chữ lấy từ thang của app, màu để `FAlert` tự quyết theo biến thể.
  static TextStyle? _metrics(TextStyle? style) => style == null
      ? null
      : TextStyle(
          fontSize: style.fontSize,
          fontWeight: style.fontWeight,
          height: style.height,
          letterSpacing: style.letterSpacing,
        );

  @override
  Widget build(BuildContext context) {
    final texts = Theme.of(context).textTheme;
    final hasTitle = title != null;

    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: FAlert(
        variant: isError ? FAlertVariant.destructive : FAlertVariant.primary,
        title: Text(
          title ?? message,
          style: _metrics(hasTitle ? texts.titleMedium : texts.bodyMedium),
        ),
        subtitle: hasTitle
            ? Text(message, style: _metrics(texts.bodyMedium))
            : null,
        icon: onRetry == null
            ? null
            : FButton.icon(
                variant: FButtonVariant.ghost,
                onPress: onRetry,
                semanticsLabel: 'Thử lại',
                child: const Icon(Icons.refresh, size: 18),
              ),
      ),
    );
  }
}

/// Thẻ nội dung theo Forui.
class AppCard extends StatelessWidget {
  const AppCard({
    super.key,
    required this.child,
    this.padding = const EdgeInsets.all(16),
    this.onTap,
  });

  final Widget child;
  final EdgeInsets padding;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final content = Padding(padding: padding, child: child);
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: FCard(
        // `FCard` là `DecoratedBox`, không phải `Material`. Con của nó mà cần
        // `Material` (`ListTile`, `InkWell`, chip…) sẽ vẽ nước chạm lên lớp
        // Material gần nhất — tức là ra ngoài thẻ, và Flutter còn assert
        // "ListTile background color or ink splashes may be invisible".
        // Một lớp Material trong suốt đặt đúng chỗ này sửa cả hai việc, cho mọi
        // thẻ trong app chứ không riêng màn nào.
        child: Material(
          type: MaterialType.transparency,
          child: onTap == null
              ? content
              : GestureDetector(
                  behavior: HitTestBehavior.opaque,
                  onTap: onTap,
                  child: content,
                ),
        ),
      ),
    );
  }
}

/// Trạng thái rỗng của danh sách: icon mờ, câu giải thích, gợi ý cách tạo dữ liệu.
///
/// Web dùng một mẫu duy nhất (`p-8 text-center text-gray-500 text-sm`); trước đây
/// mỗi màn tự đặt padding 32/40 kèm chữ trần nên cùng là "chưa có gì" mà nhìn
/// khác nhau ở từng chỗ.
///
/// Câu thông báo cố ý **không in đậm**: web vẽ nó bằng `text-sm text-gray-500`
/// (14/400 xám), nên bản trước dùng `titleSmall` (14/600) là sai thang chữ.
class AppEmptyState extends StatelessWidget {
  const AppEmptyState({
    super.key,
    required this.message,
    this.hint,
    this.icon = Icons.inbox_outlined,
  });

  final String message;
  final String? hint;
  final IconData icon;

  @override
  Widget build(BuildContext context) {
    final texts = Theme.of(context).textTheme;

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 40),
      child: Column(
        children: [
          // Icon là phần thêm cho màn hình nhỏ (web chỉ có chữ), nhưng giữ đúng
          // độ nhạt của chữ phụ để không tranh chấp với nội dung thật.
          Icon(icon, size: 40, color: AppTheme.borderStrong),
          const SizedBox(height: 12),
          Text(
            message,
            textAlign: TextAlign.center,
            style: texts.bodyMedium?.copyWith(color: AppTheme.textMuted),
          ),
          if (hint != null) ...[
            const SizedBox(height: 6),
            Text(
              hint!,
              textAlign: TextAlign.center,
              style: texts.bodySmall?.copyWith(color: AppTheme.textMuted),
            ),
          ],
        ],
      ),
    );
  }
}

/// Băng báo lỗi nằm trong luồng thao tác (đăng nhập, gửi tin nhắn, chạy tác vụ).
///
/// Web dùng đúng một kiểu cho mọi chỗ: `rounded-lg border border-red-200
/// bg-red-50 px-4 py-3 text-sm text-red-700`. Trước đây mỗi màn tự dựng một
/// kiểu (`MaterialBanner` với `errorContainer` của Material cho ra tông hồng,
/// không thuộc bảng màu Chính phủ) nên cùng một lỗi lại hiện khác nhau.
class AppErrorBanner extends StatelessWidget {
  const AppErrorBanner({super.key, required this.message, this.onDismiss});

  final String message;
  final VoidCallback? onDismiss;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 12),
    child: Container(
      width: double.infinity,
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      decoration: BoxDecoration(
        color: AppTheme.dangerSurface,
        border: Border.all(color: AppTheme.dangerBorder),
        borderRadius: BorderRadius.circular(AppTheme.radiusSmall),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Icon(Icons.error_outline, size: 18, color: AppTheme.dangerText),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              message,
              style: Theme.of(
                context,
              ).textTheme.bodyMedium?.copyWith(color: AppTheme.dangerText),
            ),
          ),
          if (onDismiss != null)
            IconButton(
              tooltip: 'Đóng thông báo lỗi',
              iconSize: 18,
              visualDensity: VisualDensity.compact,
              onPressed: onDismiss,
              icon: const Icon(Icons.close, color: AppTheme.dangerText),
            ),
        ],
      ),
    ),
  );
}

/// Hiển thị thông báo ngắn (thành công hoặc lỗi) cho người dùng.
void showAppSnack(BuildContext context, String message) {
  ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(message)));
}

/// Hỏi xác nhận một hành động không hoàn tác được.
Future<bool> confirmAppAction(
  BuildContext context, {
  required String title,
  required String message,
  String confirmLabel = 'Xoá',
}) async {
  final confirmed = await showAppDialog<bool>(
    context,
    title: title,
    barrierDismissible: false,
    content: Text(message, style: Theme.of(context).textTheme.bodyMedium),
    actions: [
      AppSecondaryButton(
        label: 'Huỷ',
        onPressed: () => Navigator.pop(context, false),
      ),
      const SizedBox(width: 10),
      AppDestructiveButton(
        label: confirmLabel,
        onPressed: () => Navigator.pop(context, true),
      ),
    ],
  );
  return confirmed == true;
}

/// Mở bottom sheet theo Forui (`showFSheet`) — thay `showModalBottomSheet`
/// của Material, để cùng một ngôn ngữ chuyển động với phần còn lại.
///
/// Nội dung bên trong nên tự thêm padding `EdgeInsets.fromLTRB(16, 20, 16, 24)`
/// và `SafeArea` để không dính mép/vùng cử chỉ hệ thống.
Future<T?> showAppSheet<T>(
  BuildContext context, {
  required WidgetBuilder builder,
  bool isScrollControlled = false,
}) async => _afterExit(
  showFSheet<T>(
    context: context,
    side: FLayout.btt,
    mainAxisMaxRatio: isScrollControlled ? 0.92 : 3 / 4,
    useSafeArea: true,
    builder: (sheetContext) =>
        SheetSurface(child: SafeArea(child: builder(sheetContext))),
  ),
);

/// Thời gian chờ hoạt ảnh thoát của hộp thoại/sheet trước khi trả kết quả.
///
/// Màn gọi thường `controller.dispose()` **ngay sau** `await showAppDialog(…)`,
/// nhưng lúc đó route vẫn đang chạy hoạt ảnh thoát và `FTextField` bên trong
/// còn nghe chính controller đó. Disposed ngay lúc đó làm cây widget vỡ đúng lúc
/// hộp thoại biến mất:
///
/// ```
/// 'package:flutter/src/widgets/framework.dart': Failed assertion:
/// line 6268 pos 12: '_dependents.isEmpty': is not true.
/// ```
///
/// Đã tái hiện 100% trên emulator với "Đổi tên đoạn chat" và "Tạo lịch tự động"
/// (có ô nhập đã focus), và **hết** khi chậm dispose lại — nên chờ hết hoạt ảnh
/// mới trả kết quả là cách chữa gọn nhất, không phải sửa từng màn.
const dialogExitGrace = Duration(milliseconds: 400);

Future<T?> _afterExit<T>(Future<T?> future) async {
  final value = await future;
  await Future<void>.delayed(dialogExitGrace);
  return value;
}

/// Nền đục cho bottom sheet Forui — **bắt buộc** với `showFSheet`.
///
/// `showFSheet` chỉ lo vị trí và chuyển động; nó không vẽ nền cho sheet (khác
/// `showModalBottomSheet` của Material vốn tự tô theo `bottomSheetTheme`). Nếu
/// nội dung không tự tô thì cả tấm sheet trong suốt: chữ của màn phía sau —
/// tiêu đề chat, ô nhập tin nhắn, hoạ tiết trống đồng — lộ thẳng qua danh sách
/// chức năng. Bọc ở đây một lớp để mọi sheet của app dùng chung nền, bo góc
/// trên; nội dung bên trong vẫn có thể đặt card trắng nổi lên trên.
class SheetSurface extends StatelessWidget {
  const SheetSurface({super.key, required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: const BoxDecoration(
      color: AppTheme.surface,
      borderRadius: BorderRadius.vertical(
        top: Radius.circular(AppTheme.radius),
      ),
    ),
    // Lưới an toàn: `FDialog`/`showFSheet` dựng route **không có `Material`**,
    // nên chỉ cần lọt vào đây một `TextField`/`Checkbox`/`Slider` của Material là
    // cả sheet thành màn đỏ "No Material widget found" (đã từng xảy ra với hộp
    // thoại "Tạo lịch tự động" và form agent). Lớp trong suốt này không đổi màu
    // gì của sheet, chỉ trả lại tổ tiên `Material`.
    child: Material(type: MaterialType.transparency, child: child),
  );
}

/// Danh sách chọn một giá trị trong bottom sheet Forui.
///
/// Dùng `showFSheet` (mặc định trượt từ dưới, `FLayout.btt`? — không: `side:
/// FLayout.ttb` là từ trên xuống; dưới lên là `FLayout.btt`. Sheet dưới cần
/// `btt`) thay cho `showModalBottomSheet` của Material để cùng một ngôn ngữ
/// chuyển động với phần còn lại của design system.
Future<T?> showAppPicker<T>(
  BuildContext context, {
  required String title,
  required List<T> items,
  required String Function(T item) labelOf,
  String Function(T item)? subtitleOf,
}) async => _afterExit(
  showFSheet<T>(
    context: context,
    side: FLayout.btt,
    mainAxisMaxRatio: 3 / 4,
    useSafeArea: true,
    builder: (context) => SheetSurface(
      child: SafeArea(
        // `shrinkWrap`: danh sách 2–3 mục thì sheet phải vừa khít nội dung,
        // không kéo dài hết 3/4 màn rồi để trống một khoảng lớn dưới đáy.
        child: ListView(
          shrinkWrap: true,
          padding: const EdgeInsets.fromLTRB(16, 20, 16, 24),
          children: [
            Text(title, style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 12),
            FTileGroup(
              children: [
                for (final item in items)
                  FTile(
                    title: Text(labelOf(item)),
                    subtitle: subtitleOf == null
                        ? null
                        : Text(subtitleOf(item)),
                    onPress: () => Navigator.pop(context, item),
                  ),
              ],
            ),
          ],
        ),
      ),
    ),
  ),
);

/// Hộp thoại theo Forui (`FDialog`) — thay `AlertDialog` của Material.
///
/// Nội dung (`content`) được bọc `SingleChildScrollView` nên form dài vẫn cuộn
/// được khi bàn phím che; hành động xếp phải giống `actions` của AlertDialog,
/// nhưng dùng nút của hệ (`AppSecondaryButton`/`AppPrimaryButton`/…).
///
/// Hiển thị trực tiếp qua [showAppDialog]; widget này vẫn dùng được riêng khi
/// cần dựng hộp thoại trong widget có trạng thái (form với `setState`).
class AppDialog extends StatelessWidget {
  const AppDialog({
    super.key,
    required this.title,
    required this.content,
    this.actions = const [],
  });

  final String title;
  final Widget content;
  final List<Widget> actions;

  @override
  Widget build(BuildContext context) => FDialog(
    style: .delta(
      decoration: .shapeDelta(
        color: Colors.white,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(AppTheme.radius),
          side: const BorderSide(color: AppTheme.border),
        ),
      ),
    ),
    // Xem [SheetSurface] về lý do có lớp `Material` trong suốt: hộp thoại Forui
    // không tự có tổ tiên `Material`, mà form nào lỡ dùng widget Material thì
    // ném lỗi ngay lúc dựng và hộp thoại trắng xoá.
    builder: (context, style) => Material(
      type: MaterialType.transparency,
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(title, style: style.titleTextStyle),
            const SizedBox(height: 8),
            Flexible(child: SingleChildScrollView(child: content)),
            if (actions.isNotEmpty) ...[
              const SizedBox(height: 20),
              // `Wrap` chứ không phải `Row`: nhãn nút tiếng Việt khá dài, ở bề
              // ngang 320dp (máy nhỏ) hai nút cạnh nhau đã tràn ra khỏi hộp thoại
              // (đo được: tràn 114px ở hộp thoại "Tạo tài khoản mới"). Wrap để
              // nút xuống dòng thay vì cắt mất phần đuôi nhãn.
              Wrap(
                alignment: WrapAlignment.end,
                spacing: 10,
                runSpacing: 8,
                children: actions,
              ),
            ],
          ],
        ),
      ),
    ),
  );
}

/// Mở hộp thoại Forui trả về giá trị (`T?`) — thay `showDialog` của Material.
///
/// Hai cách dùng:
/// - Truyền `title` + `content` (+ `actions`): hộp thoại dựng sẵn theo ngôn ngữ
///   chung của [AppDialog].
/// - Truyền `builder`: cho form có trạng thái riêng — widget con tự dựng
///   [AppDialog] bên trong để giữ cùng diện mạo.
Future<T?> showAppDialog<T>(
  BuildContext context, {
  String? title,
  Widget? content,
  WidgetBuilder? builder,
  List<Widget> actions = const [],
  bool barrierDismissible = true,
}) {
  assert(
    builder != null || (title != null && content != null),
    'Cần builder hoặc title + content.',
  );
  return _afterExit(
    showFDialog<T>(
      context: context,
      barrierDismissible: barrierDismissible,
      builder: (dialogContext, style, animation) =>
          builder?.call(dialogContext) ??
          AppDialog(title: title!, content: content!, actions: actions),
    ),
  );
}

/// Hàng ô chọn theo Forui (`FCheckbox` đặt trong [AppTileRaw]) — thay
/// `CheckboxListTile` của Material trong các sheet chọn nhiều mục.
class AppCheckTile extends StatelessWidget {
  const AppCheckTile({
    super.key,
    required this.label,
    required this.value,
    required this.onChanged,
    this.subtitle,
  });

  final String label;

  /// Mô tả phụ dưới nhãn (tuỳ chọn).
  final String? subtitle;
  final bool value;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) => AppTileRaw(
    child: Row(
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(label),
              if (subtitle != null)
                Text(subtitle!, style: Theme.of(context).textTheme.bodySmall),
            ],
          ),
        ),
        const SizedBox(width: 12),
        FCheckbox(value: value, onChange: onChanged),
      ],
    ),
  );
}

/// Tiêu đề nhóm nội dung.
class AppSectionTitle extends StatelessWidget {
  const AppSectionTitle({super.key, required this.title, this.subtitle});

  final String title;
  final String? subtitle;

  /// Tiêu đề khối nội dung: 16/600 (`text-base font-semibold` của web), mô tả
  /// phụ 12/400 màu xám. Dùng chung để mọi màn có cùng nhịp phân cấp.
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 8),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(title, style: Theme.of(context).textTheme.titleMedium),
        if (subtitle != null)
          Padding(
            padding: const EdgeInsets.only(top: 2),
            child: Text(
              subtitle!,
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ),
      ],
    ),
  );
}

/// Ô danh sách theo Forui (`FTile`) — thay `ListTile` của Material.
///
/// Bọc `FTile` đứng một mình (không nằm trong `FTileGroup`) bằng thẻ riêng bo
/// 12 giống `AppCard`, để danh sách "mỗi mục một thẻ" của app giữ nguyên bố cục
/// mà vẫn dùng style Forui. Các ô trong **cùng một khối** nên dùng [AppTileGroup]
/// thay vì đặt nhiều [AppTile] kế nhau — khi đó Forui tự vẽ divider và chỉ bo
/// góc đầu/cuối một lần.
///
/// `title`/`subtitle` của `FTile` không nhận `Expanded`/`FTextField` — với nội
/// dung phức tạp hơn một `Text`, dùng [AppTile.raw].
class AppTile extends StatelessWidget {
  const AppTile({
    super.key,
    required this.title,
    this.subtitle,
    this.prefix,
    this.suffix,
    this.details,
    this.onTap,
    this.onLongPress,
    this.destructive = false,
    this.enabled = true,
  });

  final Widget title;
  final Widget? subtitle;
  final Widget? prefix;
  final Widget? suffix;
  final Widget? details;
  final VoidCallback? onTap;
  final VoidCallback? onLongPress;

  /// Ô hành động phá huỷ (xoá/thu hồi): chữ và icon đỏ quốc kỳ.
  final bool destructive;
  final bool enabled;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 8),
    child: FTile(
      variant: destructive ? FItemVariant.destructive : FItemVariant.primary,
      enabled: enabled,
      onPress: onTap,
      onLongPress: onLongPress,
      prefix: prefix,
      suffix: suffix,
      details: details,
      title: title,
      subtitle: subtitle,
    ),
  );
}

/// Ô chọn một giá trị theo hệ thiết kế — thay `DropdownButtonFormField` của
/// Material trong hộp thoại và sheet.
///
/// **Vì sao cần**: trong hộp thoại/sheet của Forui, các widget Material như
/// `DropdownButtonFormField`/`TextField` **không tra được tổ tiên `Material`**,
/// nên chúng ném "No Material widget found" và cả hộp thoại trắng xoá — đã gặp
/// thật ở hộp thoại "Tạo lịch tự động" và các hộp thoại form của màn quản trị.
/// Ô này dùng [AppTile] + [showAppPicker] (đều của Forui) nên dùng được ở mọi
/// nơi và khớp với các ô chọn khác của app.
///
/// Widget tự giữ giá trị đang hiển thị: chọn xong nhãn đổi ngay mà không cần
/// cha dựng lại (cha vẫn nhận giá trị mới qua [onChanged]).
class AppSelectTile<T> extends StatefulWidget {
  const AppSelectTile({
    super.key,
    required this.label,
    required this.value,
    required this.items,
    required this.labelOf,
    required this.onChanged,
    this.subtitleOf,
    this.helper,
    this.icon,
    this.enabled = true,
  });

  final String label;
  final T value;
  final List<T> items;
  final String Function(T value) labelOf;
  final ValueChanged<T> onChanged;

  /// Mô tả phụ của từng mục trong sheet chọn.
  final String Function(T value)? subtitleOf;

  /// Ghi chú dưới ô chọn (thay `helperText` của Material).
  final String? helper;

  final IconData? icon;
  final bool enabled;

  @override
  State<AppSelectTile<T>> createState() => _AppSelectTileState<T>();
}

class _AppSelectTileState<T> extends State<AppSelectTile<T>> {
  late T _value = widget.value;

  @override
  void didUpdateWidget(AppSelectTile<T> oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.value != oldWidget.value) _value = widget.value;
  }

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      AppTile(
        prefix: widget.icon == null ? null : Icon(widget.icon!),
        title: Text(widget.label),
        subtitle: Text(widget.labelOf(_value)),
        suffix: const Icon(Icons.chevron_right, size: 18),
        enabled: widget.enabled,
        onTap: widget.enabled
            ? () async {
                final picked = await showAppPicker<T>(
                  context,
                  title: widget.label,
                  items: widget.items,
                  labelOf: widget.labelOf,
                  subtitleOf: widget.subtitleOf,
                );
                if (picked == null || !mounted || picked == _value) return;
                setState(() => _value = picked);
                widget.onChanged(picked);
              }
            : null,
      ),
      if (widget.helper != null)
        Padding(
          padding: const EdgeInsets.only(left: 4, bottom: 8),
          child: Text(
            widget.helper!,
            style: Theme.of(
              context,
            ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
          ),
        ),
    ],
  );
}

/// Nhóm ô Forui: divider tự động, chỉ bo góc ở hai đầu khối.
///
/// Chỉ nhận các widget Forui thật (`FTile`, `FTile.raw`…) vì `FTileGroup` của
/// forui 0.25 đòi `FTileMixin`. Danh sách "mỗi mục một thẻ" của app dùng
/// [AppTile] riêng lẻ, không qua nhóm này.
class AppTileGroup extends StatelessWidget {
  const AppTileGroup({super.key, required this.children});

  final List<FTileMixin> children;

  @override
  Widget build(BuildContext context) => FTileGroup(children: children);
}

/// Ô raw cho nội dung phức tạp (form nhúng, hàng nhiều cột) mà `FTile` chuẩn
/// không chứa được — vẫn giữ diện mạo thẻ của app.
class AppTileRaw extends StatelessWidget {
  const AppTileRaw({super.key, required this.child, this.onTap});

  final Widget child;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 8),
    child: FTile.raw(onPress: onTap, child: child),
  );
}

/// Hàng công tắc theo Forui (`FSwitch` đặt trong [AppTileRaw]) — thay
/// `SwitchListTile` của Material trong các dialog form admin.
class AppSwitchTile extends StatelessWidget {
  const AppSwitchTile({
    super.key,
    required this.label,
    required this.value,
    required this.onChanged,
    this.subtitle,
  });

  final String label;

  /// Mô tả phụ dưới nhãn (tuỳ chọn).
  final String? subtitle;
  final bool value;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) => AppTileRaw(
    child: Row(
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(label),
              if (subtitle != null)
                Text(subtitle!, style: Theme.of(context).textTheme.bodySmall),
            ],
          ),
        ),
        const SizedBox(width: 12),
        FSwitch(value: value, onChange: onChanged),
      ],
    ),
  );
}

/// Huy hiệu Forui (`FBadge`) — thay viên nhãn đỏ tự dựng của `_UnreadBadge`.
///
/// App chỉ cần 2 biến thể; số chưa đọc dùng destructive (đỏ quốc kỳ qua theme),
/// nhãn trạng thái dùng primary.
class AppBadge extends StatelessWidget {
  const AppBadge({super.key, required this.child, this.destructive = false});

  final Widget child;
  final bool destructive;

  @override
  Widget build(BuildContext context) => FBadge(
    variant: destructive ? FBadgeVariant.destructive : FBadgeVariant.primary,
    child: Padding(
      padding: const EdgeInsets.symmetric(horizontal: 2),
      child: child,
    ),
  );
}

/// Một mục trong menu ngữ cảnh của [AppMenuButton].
class AppMenuItem {
  const AppMenuItem(this.label, this.onSelected, {this.destructive = false});

  final String label;
  final VoidCallback onSelected;

  /// Mục phá huỷ (xoá/thu hồi) — chữ đỏ theo biến thể destructive của Forui.
  final bool destructive;
}

/// Nút ba chấm mở menu ngữ cảnh theo Forui (`FPopoverMenu.tiles`) — thay
/// `PopupMenuButton` của Material.
///
/// Menu của Forui đòi các nhóm tile thật (`FTileGroupMixin`) nên phần `menu`
/// được dựng bằng một `FTileGroup` ẩn trong builder; mỗi mục là một `FTile`
/// chọn lựa riêng, không bị divider dính giữa các nhóm.
///
/// Nút ba chấm dựng trong `builder` (chứ không phải `child`) vì **nó phải tự mở
/// menu**: `FPopover` không tự bắt cú nhấn của con, nên `FButton.icon` với
/// `onPress` rỗng sẽ nuốt cú nhấn và menu không bao giờ hiện — đúng lỗi đã xảy
/// ra trước đây cho mọi menu dòng (phiên chat, tenant, agent, lịch, MCP…).
class AppMenuButton extends StatelessWidget {
  const AppMenuButton({super.key, required this.items, this.tooltip});

  final List<AppMenuItem> items;
  final String? tooltip;

  @override
  Widget build(BuildContext context) => FPopoverMenu.tiles(
    semanticsLabel: tooltip,
    menuBuilder: (context, controller, menu) => [
      FTileGroup(
        children: [
          for (final item in items)
            FTile(
              variant: item.destructive
                  ? FItemVariant.destructive
                  : FItemVariant.primary,
              title: Text(item.label),
              onPress: () {
                controller.hide();
                item.onSelected();
              },
            ),
        ],
      ),
    ],
    builder: (context, controller, menu) => FButton.icon(
      onPress: controller.toggle,
      semanticsLabel: tooltip,
      child: const Icon(Icons.more_vert, size: 18),
    ),
  );
}
