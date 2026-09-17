import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../theme.dart';

/// Watermark trống đồng Đông Sơn mờ phía sau nội dung — tương đương
/// `body::before` của web (`style.css`).
///
/// Ảnh PNG (`assets/images/trongdong.png`) đã bake sẵn alpha ~8% nên widget
/// không bọc thêm `Opacity` — bớt một lớp compositing, và màu hoạ tiết giữ
/// nguyên với mọi nền. Kích thước = 1,3 lần **cạnh dài** màn hình (giống
/// `background-size: 130vmax` của web), căn giữa, không cuộn theo nội dung —
/// hoạ tiết luôn lớn hơn màn nên tràn ra ngoài mép là chủ đích, nó là nền chứ
/// không phải một hình được "đóng khung" cho vừa.
///
/// Dùng bằng cách bọc `body` của `Scaffold`:
/// ```dart
/// body: WatermarkBackground(child: Column(...))
/// ```
/// Con được vẽ ĐÈ LÊN watermark, nên mọi surface trong `child` vẫn giữ nền
/// trắng/đục riêng nếu cần nội dung dễ đọc (card, sheet…).
class WatermarkBackground extends StatelessWidget {
  const WatermarkBackground({super.key, required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context) {
    final size = MediaQuery.sizeOf(context);
    // Lấy **cạnh dài** của màn rồi nhân 1,3 (web: `background-size: 130vmax`):
    // hoạ tiết luôn lớn hơn cả hai chiều màn hình nên trống đồng tràn kín, chỉ
    // phần giữa hiện ra. Ảnh vuông nên width = height = cạnh này.
    final side = math.max(size.height, size.width) * 1.3;

    return ColoredBox(
      // Màu nền thật của app nằm ở ĐÂY, không phải ở Scaffold: mọi Scaffold
      // được đặt `Colors.transparent` (xem `AppTheme.material`) để lớp này lộ
      // ra. Thiếu màu này thì nền trần của MaterialApp (đen) sẽ lộ phía sau.
      color: AppTheme.surface,
      child: Stack(
        fit: StackFit.expand,
        children: [
          // `OverflowBox` là chỗ mấu chốt: `Center`/`Image.asset` thường bị
          // **ràng buộc theo màn** (tối đa 320×640), nên ảnh khai báo 832px bị
          // kẹp về bề ngang màn rồi `BoxFit.contain` thu nhỏ tiếp thành một
          // vòng tròn ~cạnh ngắn — đúng cái "bé tị" người dùng thấy. Ở đây ảnh
          // được cấp ràng buộc vô hạn nên vẽ đúng cỡ `side`, phần tràn ra ngoài
          // bị `Stack` cắt (mặc định `Clip.hardEdge`).
          OverflowBox(
            alignment: Alignment.center,
            minWidth: 0,
            maxWidth: double.infinity,
            minHeight: 0,
            maxHeight: double.infinity,
            child: Image.asset(
              'assets/images/trongdong.png',
              width: side,
              height: side,
              fit: BoxFit.fill,
              // Watermark hoàn toàn trang trí: không nằm trong cây semantics,
              // không chặn mọi cử chỉ của content phía trên.
              excludeFromSemantics: true,
            ),
          ),
          child,
        ],
      ),
    );
  }
}
