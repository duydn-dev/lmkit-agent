import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';

import '../../app/ui/app_controls.dart';
import '../../core/network/api_exception.dart';
import '../studio/studio_provider.dart';

/// Thông báo trong app (khớp `NotificationDto`): tài liệu đã xử lý xong, tác vụ
/// tự động chạy xong, agent chờ phê duyệt…
class NotificationModel {
  const NotificationModel({
    required this.id,
    required this.title,
    this.body = '',
    this.isRead = false,
    this.createdAt,
  });

  final String id;
  final String title;
  final String body;
  final bool isRead;
  final DateTime? createdAt;

  factory NotificationModel.fromJson(Map<String, dynamic> json) =>
      NotificationModel(
        id: json['id']?.toString() ?? '',
        title: json['title']?.toString() ?? '',
        body: json['body']?.toString() ?? '',
        isRead: json['isRead'] as bool? ?? false,
        createdAt: json['createdAt'] is String
            ? DateTime.tryParse(json['createdAt'] as String)
            : null,
      );
}

/// Danh sách thông báo, mới nhất trước.
class NotificationsController extends AsyncNotifier<List<NotificationModel>> {
  @override
  Future<List<NotificationModel>> build() async {
    final rows = await ref.read(studioRepositoryProvider).notifications();
    return rows.map(NotificationModel.fromJson).toList();
  }

  Future<void> reload() async {
    state = const AsyncLoading();
    state = await AsyncValue.guard(build);
  }

  Future<void> markRead(NotificationModel notification) async {
    if (notification.isRead) return;
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
    try {
      await ref
          .read(studioRepositoryProvider)
          .markNotificationRead(notification.id);
    } catch (error) {
      // Sai thì trả lại trạng thái cũ để badge không nói dối người dùng.
      state = AsyncData(current);
      rethrow;
    }
  }

  Future<void> markAllRead() async {
    final current = state.asData?.value ?? const <NotificationModel>[];
    if (current.every((item) => item.isRead)) return;
    await ref.read(studioRepositoryProvider).markAllNotificationsRead();
    await reload();
  }

  /// Nhịp nền làm mới badge, cùng nhịp 60 giây như web.
  ///
  /// Web chỉ gọi `LIST(unreadOnly)` cho badge vì nó đếm riêng; app lấy luôn
  /// danh sách đầy đủ để badge và màn Thông báo đọc chung một provider — một
  /// nguồn sự thật, đổi lại mỗi nhịp là một request danh sách (thông báo của
  /// một người dùng vốn chỉ vài dòng).
  ///
  /// Khác [reload]: không bật vòng xoay, không làm mất danh sách đang xem, và
  /// **im lặng khi lỗi** — đây là việc chạy nền, không được phép làm phiền người
  /// dùng bằng thông báo lỗi mạng.
  Future<void> poll() async {
    try {
      final rows = await ref.read(studioRepositoryProvider).notifications();
      state = AsyncData(rows.map(NotificationModel.fromJson).toList());
    } catch (_) {
      // Giữ nguyên dữ liệu cũ: lần poll sau sẽ thử lại.
    }
  }
}

final notificationsProvider =
    AsyncNotifierProvider<NotificationsController, List<NotificationModel>>(
      NotificationsController.new,
    );

/// Số thông báo chưa đọc, dùng cho badge trên AppBar.
///
/// Badge **dẫn xuất từ danh sách** thay vì đếm riêng: nhờ vậy đọc một thông báo
/// là badge tự giảm, không có đường nào để chuông nói một đằng còn danh sách nói
/// một nẻo.
final unreadNotificationCountProvider = Provider<int>((ref) {
  final items = ref.watch(notificationsProvider).asData?.value;
  if (items == null) return 0;
  return items.where((item) => !item.isRead).length;
});

/// Khoảng nghỉ giữa hai lần làm mới badge, khớp `NOTIFICATION_POLL_MS` của web.
const notificationPollInterval = Duration(seconds: 60);

/// Nhịp làm mới badge chạy nền, chỉ sống khi có chuông trên màn.
///
/// Bỏ qua khi app không ở tiền cảnh: thông báo của người dùng không cần được
/// đếm trong lúc họ không nhìn, và mỗi nhịp là một vòng mạng thật.
final notificationPollProvider = Provider<void>((ref) {
  final timer = Timer.periodic(notificationPollInterval, (_) {
    // `null` là lúc binding chưa nhận thông báo vòng đời nào — vẫn đang chạy
    // bình thường, nên chỉ bỏ nhịp khi biết chắc app không ở tiền cảnh.
    final lifecycle = WidgetsBinding.instance.lifecycleState;
    if (lifecycle != null && lifecycle != AppLifecycleState.resumed) return;
    unawaited(ref.read(notificationsProvider.notifier).poll());
  });
  ref.onDispose(timer.cancel);
});

/// Nút chuông kèm badge số chưa đọc (tương ứng chuông trên `AppLayout` desktop).
///
/// Đặt trong `actions` của [AppTopBar] là đủ — mọi màn có header đều thấy chuông
/// ở cùng một vị trí, giống thanh header dùng chung của web.
class NotificationBell extends ConsumerWidget {
  const NotificationBell({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // Mở nhịp polling: chỉ màn nào thật sự hiển thị chuông mới chạy.
    ref.watch(notificationPollProvider);
    final unread = ref.watch(unreadNotificationCountProvider);
    return IconButton(
      tooltip: unread == 0 ? 'Thông báo' : '$unread thông báo chưa đọc',
      onPressed: () => Navigator.of(context).push(
        MaterialPageRoute<void>(builder: (_) => const NotificationsScreen()),
      ),
      icon: Stack(
        // Badge nằm trong lòng ô 48dp của nút (icon 24dp còn 12dp mỗi bên) nên
        // không bị nút bên cạnh che, mà cũng không cần tràn ra ngoài — nhưng vẫn
        // phải tắt cắt, vì badge được vẽ ra ngoài khung của chính icon.
        clipBehavior: Clip.none,
        children: [
          Icon(unread > 0 ? Icons.notifications : Icons.notifications_none),
          if (unread > 0)
            Positioned(
              right: -10,
              top: -6,
              child: Container(
                padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 1),
                constraints: const BoxConstraints(minWidth: 16),
                decoration: BoxDecoration(
                  color: AppTheme.govRed,
                  borderRadius: BorderRadius.circular(999),
                ),
                child: Text(
                  unread > 9 ? '9+' : '$unread',
                  textAlign: TextAlign.center,
                  style: const TextStyle(
                    color: Colors.white,
                    fontSize: AppTheme.badgeSize,
                    fontWeight: FontWeight.w700,
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class NotificationsScreen extends ConsumerWidget {
  const NotificationsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(notificationsProvider);
    final controller = ref.read(notificationsProvider.notifier);
    final unread = ref.watch(unreadNotificationCountProvider);

    return Scaffold(
      appBar: AppTopBar(
        title: const Text('Thông báo'),
        // Không gắn thêm chuông: đang ở ngay màn này rồi.
        showNotificationBell: false,
        actions: [
          AppIconButton(
            icon: Icons.refresh,
            tooltip: 'Làm mới',
            onPressed: controller.reload,
          ),
          AppIconButton(
            icon: Icons.done_all,
            tooltip: 'Đánh dấu tất cả đã đọc',
            onPressed: unread == 0
                ? null
                : () async {
                    try {
                      await controller.markAllRead();
                    } catch (error) {
                      if (context.mounted) {
                        showAppSnack(
                          context,
                          error is ApiException
                              ? error.message
                              : error.toString(),
                        );
                      }
                    }
                  },
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: controller.reload,
        child: async.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (error, _) => ListView(
            padding: const EdgeInsets.all(16),
            children: [
              AppAlert(
                message: error is ApiException
                    ? error.message
                    : error.toString(),
                isError: true,
                onRetry: controller.reload,
              ),
            ],
          ),
          data: (items) => items.isEmpty
              ? ListView(
                  children: const [
                    AppEmptyState(
                      icon: Icons.notifications_none,
                      message: 'Không có thông báo nào.',
                      hint:
                          'Thông báo xuất hiện khi tài liệu xử lý xong hoặc tác vụ '
                          'tự động cần bạn phê duyệt.',
                    ),
                  ],
                )
              : ListView.separated(
                  itemCount: items.length,
                  separatorBuilder: (_, _) => const Divider(height: 1),
                  itemBuilder: (context, index) {
                    final item = items[index];
                    return AppTile(
                      prefix: Icon(
                        item.isRead
                            ? Icons.notifications_none
                            : Icons.notifications_active,
                        color: item.isRead ? null : AppTheme.govBlueDark,
                      ),
                      title: Text(
                        item.title,
                        style: TextStyle(
                          fontWeight: item.isRead
                              ? FontWeight.normal
                              : FontWeight.bold,
                        ),
                      ),
                      subtitle: Text(
                        item.body.isEmpty
                            ? _relative(item.createdAt)
                            : '${item.body}\n${_relative(item.createdAt)}',
                      ),
                      onTap: item.isRead
                          ? null
                          : () async {
                              try {
                                await controller.markRead(item);
                              } catch (error) {
                                if (context.mounted) {
                                  showAppSnack(
                                    context,
                                    error is ApiException
                                        ? error.message
                                        : error.toString(),
                                  );
                                }
                              }
                            },
                    );
                  },
                ),
        ),
      ),
    );
  }

  static String _relative(DateTime? value) {
    if (value == null) return '';
    final local = value.toLocal();
    final diff = DateTime.now().difference(local);
    if (diff.inMinutes < 1) return 'vừa xong';
    if (diff.inHours < 1) return '${diff.inMinutes} phút trước';
    if (diff.inDays < 1) return '${diff.inHours} giờ trước';
    if (diff.inDays < 7) return '${diff.inDays} ngày trước';
    String two(int number) => number.toString().padLeft(2, '0');
    return '${two(local.day)}/${two(local.month)}/${local.year}';
  }
}
