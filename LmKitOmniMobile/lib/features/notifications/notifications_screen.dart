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
}

final notificationsProvider =
    AsyncNotifierProvider<NotificationsController, List<NotificationModel>>(
      NotificationsController.new,
    );

/// Số thông báo chưa đọc, dùng cho badge trên AppBar.
final unreadNotificationCountProvider = Provider<int>((ref) {
  final items = ref.watch(notificationsProvider).asData?.value;
  if (items == null) return 0;
  return items.where((item) => !item.isRead).length;
});

/// Nút chuông kèm badge số chưa đọc (tương ứng chuông trên `AppLayout` desktop).
class NotificationBell extends ConsumerWidget {
  const NotificationBell({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final unread = ref.watch(unreadNotificationCountProvider);
    return IconButton(
      tooltip: unread == 0 ? 'Thông báo' : '$unread thông báo chưa đọc',
      onPressed: () => Navigator.of(context).push(
        MaterialPageRoute<void>(builder: (_) => const NotificationsScreen()),
      ),
      icon: Stack(
        clipBehavior: Clip.none,
        children: [
          Icon(unread > 0 ? Icons.notifications : Icons.notifications_none),
          if (unread > 0)
            Positioned(
              right: -4,
              top: -4,
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
        actions: [
          IconButton(
            tooltip: 'Làm mới',
            onPressed: controller.reload,
            icon: const Icon(Icons.refresh),
          ),
          IconButton(
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
            icon: const Icon(Icons.done_all),
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
                    return ListTile(
                      leading: Icon(
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
                      isThreeLine: item.body.isNotEmpty,
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
