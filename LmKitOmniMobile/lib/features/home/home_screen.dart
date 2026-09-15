import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';
import '../../core/auth/auth_provider.dart';
import '../chat/chat_navigation.dart';
import '../chat/chat_screen.dart';
import '../more/more_screen.dart';
import '../notifications/notifications_screen.dart';
import '../settings/api_endpoint_screen.dart';
import '../studio/studio_screen.dart';
import '../workspace/documents_screen.dart';
import '../workspace/projects_screen.dart';

/// Shell của app: 5 tab dưới cùng.
///
/// Mỗi tab là một mục cấp một, còn các mục cấp hai (Approvals, Scheduler,
/// Code Studio, quản trị, cài đặt…) nằm trong tab [MoreScreen]. Cách chia này
/// giữ cho thanh dưới **luôn khớp với màn đang mở** — trước đây Studio/Tools/
/// Admin là các chỉ số ảo ngoài dải tab nên thanh dưới bị `clamp` và tô sáng
/// nhầm mục "Custom Instructions".
class HomeScreen extends ConsumerStatefulWidget {
  const HomeScreen({super.key});

  @override
  ConsumerState<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends ConsumerState<HomeScreen> {
  static const _chat = 0;
  static const _projects = 1;
  static const _documents = 2;
  static const _studio = 3;

  int _selectedIndex = _chat;

  /// Mở một đoạn chat cụ thể của dự án: đặt yêu cầu rồi chuyển sang tab Chat.
  void _openChatSession(String sessionId) {
    ref.read(pendingChatSessionProvider.notifier).request(sessionId);
    setState(() => _selectedIndex = _chat);
  }

  void _goTo(int index) => setState(() => _selectedIndex = index);

  @override
  Widget build(BuildContext context) {
    final session = ref.watch(authControllerProvider).asData?.value;
    final user = session?.user;

    final screen = switch (_selectedIndex) {
      _chat => const ChatScreen(),
      _projects => ProjectsScreen(onOpenSession: _openChatSession),
      _documents => const DocumentsScreen(),
      _studio => StudioScreen(onOpenSession: _openChatSession),
      _ => MoreScreen(
        onOpenSession: _openChatSession,
        onOpenChat: () => _goTo(_chat),
        onOpenStudio: () => _goTo(_studio),
        onOpenProjects: () => _goTo(_projects),
      ),
    };

    return Scaffold(
      appBar: AppBar(
        title: Text(
          user?.tenant?.name.isNotEmpty == true
              ? user!.tenant!.name
              : 'Trợ lý ảo - CILA AI',
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
        actions: [
          const NotificationBell(),
          IconButton(
            tooltip: 'Cấu hình kết nối API',
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<bool>(
                builder: (_) => const ApiEndpointScreen(),
              ),
            ),
            icon: const Icon(Icons.settings_ethernet),
          ),
        ],
      ),
      body: screen,
      bottomNavigationBar: DecoratedBox(
        decoration: const BoxDecoration(
          border: Border(top: BorderSide(color: AppTheme.border)),
        ),
        child: NavigationBar(
          selectedIndex: _selectedIndex,
          onDestinationSelected: _goTo,
          destinations: const [
            NavigationDestination(
              icon: Icon(Icons.auto_awesome_outlined),
              selectedIcon: Icon(Icons.auto_awesome),
              label: 'AI Chat',
            ),
            NavigationDestination(
              icon: Icon(Icons.folder_outlined),
              selectedIcon: Icon(Icons.folder),
              label: 'Projects',
            ),
            NavigationDestination(
              icon: Icon(Icons.description_outlined),
              selectedIcon: Icon(Icons.description),
              label: 'Documents',
            ),
            NavigationDestination(
              icon: Icon(Icons.smart_toy_outlined),
              selectedIcon: Icon(Icons.smart_toy),
              label: 'AI Studio',
            ),
            NavigationDestination(
              icon: Icon(Icons.more_horiz),
              selectedIcon: Icon(Icons.more_horiz),
              label: 'Thêm',
            ),
          ],
        ),
      ),
    );
  }
}
