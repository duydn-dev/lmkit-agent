import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:forui/forui.dart';

import '../../app/theme.dart';
import '../../app/ui/app_controls.dart';
import '../../core/auth/auth_provider.dart';
import '../admin/admin_hub_screen.dart';
import '../admin/api_keys_screen.dart';
import '../chat/chat_navigation.dart';
import '../notifications/notifications_screen.dart';
import '../share/shared_chat_screen.dart';
import '../studio/studio_screen.dart';
import '../studio/tools_screen.dart';
import '../workspace/custom_instructions_screen.dart';
import '../workspace/documents_screen.dart';
import '../workspace/memory_screen.dart';
import '../workspace/projects_screen.dart';

/// Bung "danh sách chức năng" — nút ba chấm trên header.
///
/// Trước đây nhóm mục này là tab thứ năm của thanh điều hướng dưới. Bỏ thanh đó
/// đi thì chúng gom hết vào đây, đúng cách web làm: mọi mục nằm trong sidebar
/// (`navGroups` của `AppLayout.vue`), còn vùng chat không bị chia. Nhờ vậy chiều
/// cao màn hình dành trọn cho cuộc trò chuyện, và không còn cảnh thanh dưới tô
/// sáng nhầm mục khi người dùng đang ở một màn đẩy sang.
///
/// [onNewChat] và [onShare] chỉ có khi menu mở từ màn Chat: nhóm "Đoạn chat"
/// hiện ra khi và chỉ khi có hành động thật để gọi.
Future<void> showFunctionMenu(
  BuildContext context, {
  VoidCallback? onNewChat,
  VoidCallback? onShare,
}) => showAppSheet<void>(
  context,
  isScrollControlled: true,
  builder: (sheetContext) =>
      FunctionMenuSheet(onNewChat: onNewChat, onShare: onShare),
);

/// Nội dung danh sách chức năng.
///
/// Cách chia nhóm theo đúng `navGroups` của web, chỉ thêm hai nhóm mà một màn
/// mobile phải có: "Đoạn chat" (hành động của đoạn đang mở) và "Cài đặt" (đổi
/// API URL, link chia sẻ công khai, đăng xuất).
class FunctionMenuSheet extends ConsumerWidget {
  const FunctionMenuSheet({super.key, this.onNewChat, this.onShare});

  final VoidCallback? onNewChat;
  final VoidCallback? onShare;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final user = ref.watch(authControllerProvider).asData?.value?.user;
    final isAdmin = user?.isAdmin == true;
    final navigator = Navigator.of(context);

    // Đóng sheet rồi mới đẩy màn: giữ sheet nằm dưới thì lúc quay lại người dùng
    // gặp lại đúng danh sách vừa chọn — thêm một bước vô nghĩa.
    void push(WidgetBuilder builder) {
      final route = MaterialPageRoute<void>(builder: builder);
      navigator.pop();
      navigator.push(route);
    }

    /// Mở một đoạn chat: đặt yêu cầu cho màn Chat rồi quay về màn gốc (chat).
    void openSession(String sessionId) {
      ref.read(pendingChatSessionProvider.notifier).request(sessionId);
      navigator.popUntil((route) => route.isFirst);
    }

    void backToChat() => navigator.popUntil((route) => route.isFirst);

    final hasChatActions = onNewChat != null || onShare != null;

    return ConstrainedBox(
      // Sheet cao tối đa 85% màn: danh sách này dài hơn một nửa màn hình, mà để
      // nó chiếm trọn thì người dùng mất dấu màn đang mở phía sau.
      constraints: BoxConstraints(
        maxHeight: MediaQuery.sizeOf(context).height * 0.85,
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 4, 8, 0),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    'Danh sách chức năng',
                    style: Theme.of(context).textTheme.titleLarge,
                  ),
                ),
                AppIconButton(
                  icon: Icons.close,
                  tooltip: 'Đóng danh sách chức năng',
                  onPressed: navigator.pop,
                ),
              ],
            ),
          ),
          Flexible(
            child: ListView(
              padding: const EdgeInsets.only(bottom: 24),
              children: [
                _AccountCard(
                  name: user?.fullName ?? 'Người dùng',
                  role: user?.role ?? 'Member',
                  tenant: user?.tenant?.name,
                ),
                if (hasChatActions) ...[
                  const _GroupTitle('Đoạn chat'),
                  if (onNewChat != null)
                    _Tile(
                      icon: Icons.add_comment_outlined,
                      title: 'Đoạn chat mới',
                      subtitle: 'Bắt đầu một cuộc trò chuyện trống',
                      onTap: () {
                        navigator.pop();
                        onNewChat!();
                      },
                    ),
                  if (onShare != null)
                    _Tile(
                      icon: Icons.share_outlined,
                      title: 'Chia sẻ đoạn chat',
                      subtitle: 'Tạo link công khai (chỉ đọc) cho đoạn chat',
                      onTap: () {
                        navigator.pop();
                        onShare!();
                      },
                    ),
                ],
                const _GroupTitle('Không gian làm việc'),
                _Tile(
                  icon: Icons.auto_awesome_outlined,
                  title: 'AI Chat',
                  subtitle: 'Trò chuyện với trợ lý ảo của đơn vị',
                  onTap: backToChat,
                ),
                _Tile(
                  icon: Icons.folder_outlined,
                  title: 'Projects',
                  subtitle: 'Gom phiên chat và hướng dẫn riêng theo dự án',
                  onTap: () =>
                      push((_) => ProjectsScreen(onOpenSession: openSession)),
                ),
                _Tile(
                  icon: Icons.description_outlined,
                  title: 'RAG Documents',
                  subtitle: 'Tài liệu đã nạp cho kho tri thức',
                  onTap: () => push((_) => const DocumentsScreen()),
                ),
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
                const _GroupTitle('AI Studio'),
                _Tile(
                  icon: Icons.smart_toy_outlined,
                  title: 'Agent Studio',
                  subtitle: 'Custom agent, persona, công cụ và tài liệu ghim',
                  onTap: () =>
                      push((_) => StudioScreen(onOpenSession: openSession)),
                ),
                _Tile(
                  icon: Icons.edit_document,
                  title: 'Content Studio',
                  subtitle:
                      'Pipeline nghiên cứu → outline → draft → fact-check',
                  onTap: () => push(
                    (_) =>
                        StudioScreen(initialTab: 5, onOpenSession: openSession),
                  ),
                ),
                _Tile(
                  icon: Icons.bolt_outlined,
                  title: 'Automation Agent',
                  subtitle: 'Chạy mục tiêu tự hành và theo dõi từng bước',
                  onTap: () => push(
                    (_) =>
                        StudioScreen(initialTab: 2, onOpenSession: openSession),
                  ),
                ),
                _Tile(
                  icon: Icons.calendar_month_outlined,
                  title: 'Task Scheduler',
                  subtitle: 'Prompt chạy tự động theo lịch',
                  onTap: () => push((_) => const StudioScreen(initialTab: 1)),
                ),
                _Tile(
                  icon: Icons.compass_calibration_outlined,
                  title: 'Deep Research',
                  subtitle: 'Tổng hợp nhiều nguồn thành báo cáo',
                  onTap: () => push((_) => const StudioScreen(initialTab: 3)),
                ),
                _Tile(
                  icon: Icons.align_horizontal_left,
                  title: 'Text Analytics',
                  subtitle:
                      'Sentiment, phân loại, ngôn ngữ, keyword, embedding',
                  onTap: () => push((_) => const ToolsScreen()),
                ),
                _Tile(
                  icon: Icons.image_outlined,
                  title: 'Vision & OCR',
                  subtitle: 'Phân tích ảnh, OCR, phân loại, tách nền',
                  onTap: () => push((_) => const ToolsScreen(initialTab: 1)),
                ),
                const _GroupTitle('Vận hành'),
                _Tile(
                  icon: Icons.checklist_rtl,
                  title: 'HITL Approvals',
                  subtitle: 'Phê duyệt hành động agent đang chờ',
                  onTap: () => push(
                    (_) =>
                        StudioScreen(initialTab: 4, onOpenSession: openSession),
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
                if (isAdmin) ...[
                  const _GroupTitle('Quản trị'),
                  _Tile(
                    icon: Icons.admin_panel_settings_outlined,
                    title: 'Dashboard quản trị',
                    subtitle:
                        'User, tenant, CSDL, MCP, LoRA, widget, audit log',
                    onTap: () => push((_) => const AdminHubScreen()),
                  ),
                ],
                // Không có mục đổi API URL ở đây: địa chỉ máy chủ là giá trị
                // build-time (`env/<flavor>.json`), không phải thứ người dùng
                // cuối sửa được trong app.
                const _GroupTitle('Cài đặt'),
                _Tile(
                  icon: Icons.link,
                  title: 'Đoạn chat được chia sẻ',
                  subtitle: 'Mở link chia sẻ công khai, không cần đăng nhập',
                  onTap: () => push((_) => const SharedChatScreen()),
                ),
                _Tile(
                  icon: Icons.logout,
                  title: 'Đăng xuất',
                  subtitle:
                      'Thu hồi phiên trên máy chủ và xoá token trên thiết bị',
                  onTap: () {
                    navigator.pop();
                    ref.read(authControllerProvider.notifier).logout();
                  },
                ),
                // Dòng cuối danh sách: nói rõ bản cài này là môi trường thử
                // nghiệm, để người dùng không lấy kết quả ở đây làm căn cứ.
                const _Footnote('Hệ thống thử nghiệm'),
              ],
            ),
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
    padding: const EdgeInsets.fromLTRB(16, 8, 16, 4),
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
                Text(name, style: Theme.of(context).textTheme.titleSmall),
                // Dòng "vai trò • đơn vị" để xuống dòng chứ không cắt: tên đơn
                // vị dài mà bị `…` thì mất đúng phần phân biệt đơn vị này với
                // đơn vị khác.
                Text(
                  tenant == null || tenant!.isEmpty ? role : '$role • $tenant',
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ],
            ),
          ),
        ],
      ),
    ),
  );
}

/// Dòng ghi chú cuối danh sách (nhỏ, nhạt, canh giữa).
class _Footnote extends StatelessWidget {
  const _Footnote(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.fromLTRB(16, 16, 16, 0),
    child: Text(
      text,
      textAlign: TextAlign.center,
      style: Theme.of(
        context,
      ).textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
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
    child: FTile(
      onPress: onTap,
      prefix: Icon(icon, size: 20),
      suffix: trailing ?? const Icon(Icons.chevron_right, size: 18),
      title: Text(title),
      subtitle: Text(subtitle),
    ),
  );
}

class _UnreadBadge extends ConsumerWidget {
  const _UnreadBadge();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final unread = ref.watch(unreadNotificationCountProvider);
    if (unread == 0) return const Icon(Icons.chevron_right, size: 18);
    return AppBadge(
      destructive: true,
      child: Text(unread > 9 ? '9+' : '$unread'),
    );
  }
}
