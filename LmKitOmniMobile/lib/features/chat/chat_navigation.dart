import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Yêu cầu mở một phiên chat từ màn khác (ví dụ bung dự án trong Projects).
///
/// `HomeScreen` đặt giá trị rồi chuyển sang tab Chat; `ChatScreen` đọc giá trị
/// này khi được dựng và mở đúng phiên, sau đó gọi [consume] để tránh mở lại ở
/// lần vào tab sau. Giữ ở dạng id thay vì truyền cả model để nơi gọi không phải
/// phụ thuộc vào hình dạng `ChatSessionModel`.
class PendingChatSession extends Notifier<String?> {
  @override
  String? build() => null;

  void request(String sessionId) => state = sessionId;

  void consume() => state = null;
}

final pendingChatSessionProvider =
    NotifierProvider<PendingChatSession, String?>(PendingChatSession.new);
