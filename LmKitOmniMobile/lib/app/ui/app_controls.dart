import 'package:flutter/material.dart';
import 'package:forui/forui.dart';

import '../theme.dart';

/// Lớp primitive dùng Forui làm design system cho toàn app.
///
/// Mọi màn hình mới nên dùng các widget ở đây thay vì `FilledButton`,
/// `OutlinedButton`, `Card`, `Alert` của Material — nhờ vậy theme, kích thước
/// chạm và trạng thái disabled/loading chỉ được định nghĩa một lần.
///
/// Material vẫn được dùng cho hạ tầng (`Scaffold`, `AppBar`, `ListView`,
/// `IconButton` trên AppBar) vì Forui không thay thế các lớp đó.
/// Lề ngang mà `FButton` tự thêm quanh nội dung (padding vùng chạm) — phải trừ
/// ra khi giới hạn bề ngang cho nhãn, nếu không chính hàng bên trong nút sẽ tràn.
///
/// Số này **đo được**, không phải phỏng đoán: ở khổ 320dp nút rộng 288 còn hàng
/// nội dung bên trong rộng 264 → 12 mỗi bên. Chỉnh lại thì chạy
/// `flutter test test/layout_test.dart` (lưới quét toàn màn ở 320dp và 1.3×) để
/// chắc không có nút nào tràn trở lại.
const buttonHorizontalPadding = 24.0;

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
    final button = LayoutBuilder(
      builder: (context, constraints) => FButton(
        onPress: busy ? null : onPressed,
        mainAxisSize: expand ? MainAxisSize.max : MainAxisSize.min,
        // Chặn bề ngang của nội dung đúng bằng chỗ thực có. Không làm việc này
        // thì với nút co theo nội dung, nhãn dài ("Nạp vào kho tri thức") tràn ra
        // ngoài khung — đã bắt được ở màn Cơ sở kiến thức và API Keys.
        child: ConstrainedBox(
          constraints: _labelConstraints(constraints),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              if (busy)
                SizedBox(
                  height: 16,
                  width: 16,
                  // Nút chính nền navy: vòng xoay phải lấy màu chữ trên nền chính,
                  // nếu để mặc định thì trùng màu nền và gần như vô hình.
                  child: CircularProgressIndicator(
                    strokeWidth: 2,
                    color: Theme.of(context).colorScheme.onPrimary,
                  ),
                )
              else if (icon != null)
                Icon(icon, size: 18),
              if (busy || icon != null) const SizedBox(width: 8),
              Flexible(child: Text(label, overflow: TextOverflow.ellipsis)),
            ],
          ),
        ),
      ),
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
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) => FButton(
      variant: FButtonVariant.outline,
      onPress: busy ? null : onPressed,
      child: ConstrainedBox(
        constraints: _labelConstraints(constraints),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (icon != null) ...[
              Icon(icon, size: 18),
              const SizedBox(width: 8),
            ],
            Flexible(child: Text(label, overflow: TextOverflow.ellipsis)),
          ],
        ),
      ),
    ),
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
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) => FButton(
      variant: FButtonVariant.destructive,
      onPress: onPressed,
      mainAxisSize: MainAxisSize.min,
      child: ConstrainedBox(
        constraints: _labelConstraints(constraints),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (icon != null) ...[
              Icon(icon, size: 18),
              const SizedBox(width: 8),
            ],
            Flexible(child: Text(label, overflow: TextOverflow.ellipsis)),
          ],
        ),
      ),
    ),
  );
}

/// Nút chỉ có icon (Forui `FButton.icon`, mặc định variant outline).
class AppIconButton extends StatelessWidget {
  const AppIconButton({
    super.key,
    required this.icon,
    required this.onPressed,
    this.tooltip,
  });

  final IconData icon;
  final VoidCallback? onPressed;
  final String? tooltip;

  @override
  Widget build(BuildContext context) {
    final button = FButton.icon(
      onPress: onPressed ?? () {},
      semanticsLabel: tooltip,
      child: Icon(icon, size: 18),
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
        child: onTap == null
            ? content
            : GestureDetector(
                behavior: HitTestBehavior.opaque,
                onTap: onTap,
                child: content,
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
  final confirmed = await showDialog<bool>(
    context: context,
    builder: (context) => AlertDialog(
      title: Text(title),
      content: Text(message),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context, false),
          child: const Text('Huỷ'),
        ),
        FilledButton(
          onPressed: () => Navigator.pop(context, true),
          child: Text(confirmLabel),
        ),
      ],
    ),
  );
  return confirmed == true;
}

/// Danh sách chọn một giá trị trong bottom sheet.
Future<T?> showAppPicker<T>(
  BuildContext context, {
  required String title,
  required List<T> items,
  required String Function(T item) labelOf,
  String Function(T item)? subtitleOf,
}) => showModalBottomSheet<T>(
  context: context,
  showDragHandle: true,
  builder: (context) => ListView(
    padding: const EdgeInsets.all(16),
    children: [
      Text(title, style: Theme.of(context).textTheme.titleLarge),
      const SizedBox(height: 8),
      for (final item in items)
        ListTile(
          title: Text(labelOf(item)),
          subtitle: subtitleOf == null ? null : Text(subtitleOf(item)),
          onTap: () => Navigator.pop(context, item),
        ),
    ],
  ),
);

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
