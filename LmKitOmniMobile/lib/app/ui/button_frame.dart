import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';

/// Khung cho **nội dung nút**: cho nút biết bề ngang thực có để cắt nhãn dài,
/// mà vẫn trả lời được truy vấn intrinsic mà `AlertDialog` cần.
///
/// Vì sao cần một khung riêng thay vì `LayoutBuilder`:
///
/// * `FButton` cấp cho con **ràng buộc vô hạn** (`LayoutBuilder` đặt bên trong
///   nút luôn đọc được `maxWidth = Infinity`), nên nhãn dài ("Nạp vào kho tri
///   thức") tràn khỏi khung nút. Cách chặn duy nhất là đo bề ngang thực có ở
///   **ngoài** nút — tức `LayoutBuilder` bọc quanh `FButton`.
/// * Nhưng `AlertDialog` bọc cả nội dung lẫn hàng nút trong `IntrinsicWidth`,
///   mà `LayoutBuilder` **không trả lời được truy vấn intrinsic**. Bọc ngoài thì
///   mọi hộp thoại quản trị ném "LayoutBuilder does not support returning
///   intrinsic dimensions" ở khổ máy ≥ 360dp cỡ chữ 1×, hàng nút không dựng xong
///   và **nút Lưu biến mất** (bản release không assert nên âm thầm tính sai bề
///   ngang — chỉ lộ ra ở debug và trên máy ảo).
///
/// Khung này trả lời truy vấn intrinsic bằng **kích thước nhãn đo sẵn**
/// (`TextPainter`, đúng cỡ chữ hệ thống) — đúng bằng bề ngang nút cần để hiện
/// đủ nhãn trên một dòng, tức câu trả lời đúng cho hộp thoại — và **không** hỏi
/// tới `LayoutBuilder` bên trong. Phần bố cục thật vẫn do `LayoutBuilder` lo,
/// nên hành vi cắt nhãn ở mọi màn không đổi.
class ButtonFrame extends SingleChildRenderObjectWidget {
  const ButtonFrame({
    super.key,
    required this.label,
    this.icon,
    this.busy = false,
    super.child,
  });

  /// Nhãn nút — dùng để đo kích thước intrinsic.
  final String label;

  /// Icon đầu nút (nếu có), tính vào bề ngang.
  final IconData? icon;

  /// Đang xử lý: chỗ icon là vòng xoay 16px, hẹp hơn icon 18px.
  final bool busy;

  /// Kích thước icon trong nút (khớp `AppPrimaryButton`).
  static const iconSize = 18.0;
  static const iconGap = 8.0;
  static const spinnerSize = 16.0;

  @override
  ButtonFrameRenderObject createRenderObject(BuildContext context) =>
      ButtonFrameRenderObject(
        label: label,
        leadingWidth: _leadingWidth,
        style: DefaultTextStyle.of(context).style,
        textScaler: MediaQuery.textScalerOf(context),
        horizontalPadding: buttonHorizontalPadding,
      );

  @override
  void updateRenderObject(
    BuildContext context,
    ButtonFrameRenderObject renderObject,
  ) {
    final style = DefaultTextStyle.of(context).style;
    final textScaler = MediaQuery.textScalerOf(context);
    final leadingWidth = _leadingWidth;

    final changed =
        renderObject.label != label ||
        renderObject.leadingWidth != leadingWidth ||
        renderObject.style != style ||
        renderObject.textScaler != textScaler;
    if (!changed) return;

    renderObject
      ..label = label
      ..leadingWidth = leadingWidth
      ..style = style
      ..textScaler = textScaler
      ..markNeedsLayout();
  }

  /// Bề ngang phần đứng trước nhãn: icon + khoảng cách, hoặc vòng xoay.
  double get _leadingWidth => busy
      ? spinnerSize + iconGap
      : icon == null
      ? 0
      : iconSize + iconGap;
}

/// Lề ngang mà `FButton` tự thêm quanh nội dung (vùng chạm) — phải trừ ra khi
/// giới hạn bề ngang cho nhãn, nếu không chính hàng bên trong nút sẽ tràn.
///
/// Số này **đo được**, không phải phỏng đoán: ở khổ 320dp nút rộng 288 còn hàng
/// nội dung bên trong rộng 264 → 12 mỗi bên. Chỉnh lại thì chạy
/// `flutter test test/layout_test.dart` (lưới quét toàn màn ở 320dp và 1.3×) để
/// chắc không có nút nào tràn trở lại.
const buttonHorizontalPadding = 24.0;

/// `RenderObject` của [ButtonFrame] — xem giải thích ở đó.
class ButtonFrameRenderObject extends RenderProxyBox {
  ButtonFrameRenderObject({
    required this.label,
    required this.leadingWidth,
    required this.style,
    required this.textScaler,
    required this.horizontalPadding,
  });

  /// Nhãn nút lúc này.
  String label;

  /// Bề ngang phần đứng trước nhãn (icon + khoảng cách).
  double leadingWidth;

  TextStyle style;
  TextScaler textScaler;

  /// Lề ngang `FButton` thêm quanh nội dung.
  final double horizontalPadding;

  TextPainter _measure() => TextPainter(
    text: TextSpan(text: label, style: style),
    textDirection: TextDirection.ltr,
    textScaler: textScaler,
    maxLines: 1,
  )..layout();

  /// Bề ngang nút cần để hiện **đủ** nhãn trên một dòng.
  double get _naturalWidth =>
      _measure().width + leadingWidth + horizontalPadding;

  /// Chiều cao nút cần: chiều cao dòng nhãn cộng lề dọc, tối thiểu bằng vùng
  /// chạm chuẩn của một nút (`FButton` cao 40).
  double get _naturalHeight => math.max(_measure().height + 20, 40);

  @override
  double computeMaxIntrinsicWidth(double height) => _naturalWidth;

  @override
  double computeMinIntrinsicWidth(double height) => _naturalWidth;

  @override
  double computeMaxIntrinsicHeight(double width) => _naturalHeight;

  @override
  double computeMinIntrinsicHeight(double width) => _naturalHeight;

  /// Trả lời cả **dry layout** — `Wrap` đo con bằng đường này, mà `LayoutBuilder`
  /// bên trong cũng không hỗ trợ dry layout ("_RenderLayoutBuilder class does not
  /// support dry layout"). Cần cả hai lối đo thì nút mới nằm được trong mọi ngữ
  /// cảnh: `AlertDialog` hỏi intrinsic, `Wrap` hỏi dry layout.
  @override
  Size computeDryLayout(BoxConstraints constraints) => constraints.constrain(
    Size(_naturalWidth, _naturalHeight),
  );
}
