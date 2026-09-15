import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';
import '../../app/ui/app_controls.dart';
import '../../core/auth/auth_provider.dart';
import '../admin/admin_hub_screen.dart';
import '../admin/api_keys_screen.dart';
import '../notifications/notifications_screen.dart';
import '../settings/api_endpoint_screen.dart';
import '../share/shared_chat_screen.dart';
import '../studio/studio_screen.dart';
import '../studio/tools_screen.dart';
import '../workspace/custom_instructions_screen.dart';
import '../workspace/memory_screen.dart';

/// Tab thứ năm của shell: gom nhóm "Vận hành", "AI Studio" (các công cụ),
/// "Quản trị" và "Cài đặt" — đúng cách chia nhóm của sidebar web
/// (`navGroups` trong `AppLayout.vue`), nhưng chỉ hiển thị những mục mà một màn
/// mobile cần.
///
/// Nhờ vậy thanh điều hướng dưới luôn khớp với màn đang mở: mọi mục ở đây đều
/// là màn đẩy sang (push), không phải tab ngầm, nên không còn trạng thái
/// "đang ở Studio nhưng thanh dưới lại tô sáng mục khác".
class MoreScreen extends ConsumerWidget {
  const MoreScreen({
    super.key,
    required this.onOpenSession,
    required this.onOpenChat,
    required this.onOpenStudio,
    required this.onOpenProjects,
  });

  /// Mở phiên chat trong tab AI Chat (dùng cho "Chat với agent").
  final void Function(String sessionId) onOpenSession;

  /// Chuyển thẳng sang tab AI Chat.
  final VoidCallback onOpenChat;

  /// Mở tab AI Studio của shell.
  final VoidCallback onOpenStudio;

  /// Mở tab Projects của shell.
  final VoidCallback onOpenProjects;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final user = ref.watch(authControllerProvider).asData?.value?.user;
    final isAdmin = user?.isAdmin == true;

    void push(WidgetBuilder builder) =>
        Navigator.of(context).push(MaterialPageRoute<void>(builder: builder));

    return Scaffold(
      appBar: AppBar(title: const Text('Thêm')),
      body: ListView(
        padding: const EdgeInsets.only(bottom: 24),
        children: [
          _AccountCard(
            name: user?.fullName ?? 'Người dùng',
            role: user?.role ?? 'Member',
            tenant: user?.tenant?.name,
          ),
          const _GroupTitle('Vận hành'),
          _Tile(
            icon: Icons.checklist_rtl,
            title: 'HITL Approvals',
            subtitle: 'Phê duyệt hành động agent đang chờ',
            onTap: () => push(
              (_) => StudioScreen(initialTab: 4, onOpenSession: onOpenSession),
            ),
          ),
          _Tile(
            icon: Icons.vpn_key_outlined,
            title: 'API Keys',
            subtitle: 'Cấp khóa truy cập API kèm hạn dùng và hạn mức',
            onTap: () => push((_) => const ApiKeysScreen()),
          ),
          _Tile(
            icon: Icons.notifications_none,
            title: 'Thông báo',
            subtitle: 'Cập nhật từ tài liệu và tác vụ tự động',
            trailing: const _UnreadBadge(),
            onTap: () => push((_) => const NotificationsScreen()),
          ),
          const _GroupTitle('AI Studio'),
          _Tile(
            icon: Icons.smart_toy_outlined,
            title: 'Agent Studio',
            subtitle: 'Custom agent, persona, công cụ và tài liệu ghim',
            onTap: () =>
                push((_) => StudioScreen(onOpenSession: onOpenSession)),
          ),
          _Tile(
            icon: Icons.edit_document,
            title: 'Content Studio',
            subtitle: 'Pipeline nghiên cứu → outline → draft → fact-check',
            onTap: () => push(
              (_) => StudioScreen(initialTab: 5, onOpenSession: onOpenSession),
            ),
          ),
          _Tile(
            icon: Icons.bolt_outlined,
            title: 'Automation Agent',
            subtitle: 'Chạy mục tiêu tự hành và theo dõi từng bước',
            onTap: () => push(
              (_) => StudioScreen(initialTab: 2, onOpenSession: onOpenSession),
            ),
          ),
          _Tile(
            icon: Icons.calendar_month_outlined,
            title: 'Task Scheduler',
            subtitle: 'Prompt chạy tự động theo lịch',
            onTap: () => push((_) => StudioScreen(initialTab: 1)),
          ),
          _Tile(
            icon: Icons.compass_calibration_outlined,
            title: 'Deep Research',
            subtitle: 'Tổng hợp nhiều nguồn thành báo cáo',
            onTap: () => push((_) => StudioScreen(initialTab: 3)),
          ),
          _Tile(
            icon: Icons.align_horizontal_left,
            title: 'Text Analytics',
            subtitle: 'Sentiment, phân loại, ngôn ngữ, keyword, embedding',
            onTap: () => push((_) => const ToolsScreen()),
          ),
          _Tile(
            icon: Icons.image_outlined,
            title: 'Vision & OCR',
            subtitle: 'Phân tích ảnh, OCR, phân loại, tách nền',
            onTap: () => push((_) => const ToolsScreen(initialTab: 1)),
          ),
          const _GroupTitle('Không gian làm việc'),
          _Tile(
            icon: Icons.history,
            title: 'Agent Memory',
            subtitle: 'Xác nhận và quên memory của trợ lý',
            onTap: () => push((_) => const MemoryScreen()),
          ),
          _Tile(
            icon: Icons.edit_note_outlined,
            title: 'Custom Instructions',
            subtitle: 'Cá nhân hóa cách trợ lý phản hồi',
            onTap: () => push((_) => const CustomInstructionsScreen()),
          ),
          _Tile(
            icon: Icons.psychology_alt_outlined,
            title: 'Chat với agent',
            subtitle: 'Mở AI Chat để trò chuyện với agent đã tạo',
            onTap: onOpenChat,
          ),
          _Tile(
            icon: Icons.folder_outlined,
            title: 'Projects & đoạn chat',
            subtitle: 'Gom phiên chat và hướng dẫn riêng theo dự án',
            onTap: onOpenProjects,
          ),
          if (isAdmin) ...[
            const _GroupTitle('Quản trị'),
            _Tile(
              icon: Icons.admin_panel_settings_outlined,
              title: 'Dashboard quản trị',
              subtitle: 'User, tenant, CSDL, MCP, LoRA, widget, audit log',
              onTap: () => push((_) => const AdminHubScreen()),
            ),
          ],
          const _GroupTitle('Cài đặt'),
          _Tile(
            icon: Icons.settings_ethernet,
            title: 'Cấu hình kết nối API',
            subtitle: 'Đổi API URL ngay trên thiết bị, không cần build lại',
            onTap: () => push((_) => const ApiEndpointScreen()),
          ),
          _Tile(
            icon: Icons.link,
            title: 'Đoạn chat được chia sẻ',
            subtitle: 'Mở link chia sẻ công khai, không cần đăng nhập',
            onTap: () => push((_) => const SharedChatScreen()),
          ),
          _Tile(
            icon: Icons.logout,
            title: 'Đăng xuất',
            subtitle: 'Thu hồi phiên trên máy chủ và xoá token trên thiết bị',
            onTap: () => ref.read(authControllerProvider.notifier).logout(),
          ),
        ],
      ),
    );
  }
}

class _AccountCard extends StatelessWidget {
  const _AccountCard({required this.name, required this.role, this.tenant});

  final String name;
  final String role;
  final String? tenant;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.fromLTRB(16, 16, 16, 4),
    child: AppCard(
      child: Row(
        children: [
          CircleAvatar(
            radius: 22,
            backgroundColor: AppTheme.govBlueDark,
            child: Text(
              (name.trim().isEmpty ? '?' : name.trim()[0]).toUpperCase(),
              style: const TextStyle(
                color: Colors.white,
                fontWeight: FontWeight.w700,
              ),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  name,
                  style: Theme.of(context).textTheme.titleSmall,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
                Text(
                  tenant == null || tenant!.isEmpty ? role : '$role • $tenant',
                  style: Theme.of(context).textTheme.bodySmall,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
              ],
            ),
          ),
        ],
      ),
    ),
  );
}

class _GroupTitle extends StatelessWidget {
  const _GroupTitle(this.title);

  final String title;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.fromLTRB(16, 16, 16, 8),
    child: Text(
      // Web: nhãn nhóm menu là `text-[10px] uppercase tracking-widest`; lấy
      // `labelSmall` (11, giãn 0.8) để vẫn đủ nét ở màn mật độ thấp.
      title.toUpperCase(),
      style: Theme.of(context).textTheme.labelSmall,
    ),
  );
}

class _Tile extends StatelessWidget {
  const _Tile({
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.onTap,
    this.trailing,
  });

  final IconData icon;
  final String title;
  final String subtitle;
  final VoidCallback onTap;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
    child: AppCard(
      onTap: onTap,
      child: Row(
        children: [
          Icon(icon, size: 22, color: AppTheme.govBlueDark),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  title,
                  style: const TextStyle(fontWeight: FontWeight.w600),
                ),
                const SizedBox(height: 2),
                Text(subtitle, style: Theme.of(context).textTheme.bodySmall),
              ],
            ),
          ),
          trailing ?? const Icon(Icons.chevron_right, size: 20),
        ],
      ),
    ),
  );
}

class _UnreadBadge extends ConsumerWidget {
  const _UnreadBadge();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final unread = ref.watch(unreadNotificationCountProvider);
    if (unread == 0) return const Icon(Icons.chevron_right, size: 20);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 2),
      decoration: BoxDecoration(
        color: AppTheme.govRed,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Text(
        unread > 9 ? '9+' : '$unread',
        style: const TextStyle(
          color: Colors.white,
          fontSize: AppTheme.badgeSize,
          fontWeight: FontWeight.w700,
        ),
      ),
    );
  }
}
