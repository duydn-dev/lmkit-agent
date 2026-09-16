import 'package:flutter/material.dart';

import '../chat/chat_screen.dart';

/// Điểm vào của app sau khi đăng nhập.
///
/// **Màn chat là màn gốc duy nhất** — không còn thanh điều hướng dưới. Mọi mục
/// cấp một (Projects, RAG Documents, AI Studio, quản trị, cài đặt…) nằm trong
/// danh sách chức năng của nút ba chấm trên header
/// (`showFunctionMenu`), mở ra bằng cách đẩy sang (`Navigator.push`) nên lúc nào
/// cũng có nút quay lại đúng chỗ.
///
/// Vì sao bỏ thanh dưới:
/// - Nó chiếm 64dp đúng chỗ transcript cần — trên 640dp chiều cao, đó là 10%
///   màn hình dành cho điều hướng trong khi người dùng gần như luôn ở màn chat.
/// - Nó buộc mọi màn con phải "giả" một chỉ số tab: Studio, Tools và Admin là
///   các chỉ số ảo nằm ngoài dải 5 tab nên thanh dưới bị `clamp` và tô sáng
///   nhầm mục.
///
/// Cách tổ chức này cũng đúng như web: sidebar giữ toàn bộ mục điều hướng,
/// vùng chat không bị chia.
class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context) => const ChatScreen();
}
