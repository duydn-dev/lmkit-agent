import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../../app/theme.dart';
import '../../core/auth/auth_provider.dart';
import '../chat/chat_screen.dart';
import '../workspace/custom_instructions_screen.dart';
import '../workspace/documents_screen.dart';
import '../workspace/memory_screen.dart';
import '../workspace/projects_screen.dart';
import '../studio/studio_screen.dart';
import '../studio/tools_screen.dart';
import '../studio/operations_screen.dart';

class HomeScreen extends ConsumerStatefulWidget {
  const HomeScreen({super.key});

  @override
  ConsumerState<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends ConsumerState<HomeScreen> {
  int _selectedIndex = 0;

  static const _primaryItems =
      <({String label, IconData icon, String description})>[
        (
          label: 'AI Chat',
          icon: Icons.auto_awesome,
          description: 'Chat streaming với CILA Agent, file, RAG và HITL.',
        ),
        (
          label: 'Projects',
          icon: Icons.folder_outlined,
          description: 'Gom các phiên chat và instructions theo dự án.',
        ),
        (
          label: 'Documents',
          icon: Icons.description_outlined,
          description: 'Kho tài liệu cá nhân cho hybrid RAG search.',
        ),
        (
          label: 'Agent Memory',
          icon: Icons.psychology_outlined,
          description: 'Xác nhận và quản lý memory của trợ lý.',
        ),
        (
          label: 'Instructions',
          icon: Icons.edit_note_outlined,
          description: 'Cá nhân hóa cách trợ lý phản hồi.',
        ),
      ];

  @override
  Widget build(BuildContext context) {
    final session = ref.watch(authControllerProvider).asData?.value;
    final user = session?.user;
    final screen = switch (_selectedIndex) {
      0 => const ChatScreen(),
      1 => const ProjectsScreen(),
      2 => const DocumentsScreen(),
      3 => const MemoryScreen(),
      4 => const CustomInstructionsScreen(),
      5 => const StudioScreen(),
      6 => const ToolsScreen(),
      _ => const OperationsScreen(),
    };

    return Scaffold(
      appBar: AppBar(
        title: Text(
          user?.tenant?.name.isNotEmpty == true
              ? user!.tenant!.name
              : 'CILA AI',
        ),
        actions: [
          IconButton(
            tooltip: 'Đăng xuất',
            onPressed: () => ref.read(authControllerProvider.notifier).logout(),
            icon: const Icon(Icons.logout),
          ),
        ],
      ),
      drawer: _NavigationDrawer(
        user: user,
        selectedIndex: _selectedIndex,
        onSelect: (index) {
          setState(() => _selectedIndex = index);
          Navigator.of(context).pop();
        },
        onLogout: () => ref.read(authControllerProvider.notifier).logout(),
      ),
      body: screen,
      bottomNavigationBar: NavigationBar(
        selectedIndex: _selectedIndex.clamp(0, _primaryItems.length - 1),
        onDestinationSelected: (index) =>
            setState(() => _selectedIndex = index),
        destinations: [
          for (final entry in _primaryItems)
            NavigationDestination(icon: Icon(entry.icon), label: entry.label),
        ],
      ),
    );
  }
}

class _NavigationDrawer extends StatelessWidget {
  const _NavigationDrawer({
    required this.user,
    required this.selectedIndex,
    required this.onSelect,
    required this.onLogout,
  });

  final dynamic user;
  final int selectedIndex;
  final ValueChanged<int> onSelect;
  final VoidCallback onLogout;

  @override
  Widget build(BuildContext context) => Drawer(
    child: SafeArea(
      child: ListView(
        padding: const EdgeInsets.symmetric(vertical: 16),
        children: [
          ListTile(
            leading: CircleAvatar(
              backgroundColor: AppTheme.brandBlue,
              child: Text(
                (user?.fullName?.isNotEmpty == true ? user.fullName[0] : '?')
                    .toUpperCase(),
                style: const TextStyle(color: Colors.white),
              ),
            ),
            title: Text(user?.fullName ?? 'Người dùng'),
            subtitle: Text(user?.role ?? 'Member'),
          ),
          const Divider(),
          const Padding(
            padding: EdgeInsets.fromLTRB(16, 12, 16, 4),
            child: Text('KHÔNG GIAN LÀM VIỆC'),
          ),
          for (var i = 0; i < _HomeScreenState._primaryItems.length; i++)
            ListTile(
              selected: selectedIndex == i,
              leading: Icon(_HomeScreenState._primaryItems[i].icon),
              title: Text(_HomeScreenState._primaryItems[i].label),
              onTap: () => onSelect(i),
            ),
          const Padding(
            padding: EdgeInsets.fromLTRB(16, 20, 16, 4),
            child: Text('AI STUDIO'),
          ),
          ListTile(
            selected: selectedIndex == 5,
            leading: const Icon(Icons.smart_toy_outlined),
            title: const Text('AI Studio'),
            subtitle: const Text('Agents, lịch, runs, research, phê duyệt'),
            onTap: () => onSelect(5),
          ),
          ListTile(
            selected: selectedIndex == 6,
            leading: const Icon(Icons.build_outlined),
            title: const Text('AI Tools'),
            subtitle: const Text('Text Analysis, Vision và OCR'),
            onTap: () => onSelect(6),
          ),
          if (user?.isAdmin == true)
            ListTile(
              selected: selectedIndex == 7,
              leading: const Icon(Icons.admin_panel_settings_outlined),
              title: const Text('Operations'),
              subtitle: const Text('API keys, users và audit log'),
              onTap: () => onSelect(7),
            ),
          if (user?.isAdmin == true) ...[
            const Padding(
              padding: EdgeInsets.fromLTRB(16, 20, 16, 4),
              child: Text('QUẢN TRỊ'),
            ),
            const ListTile(
              leading: Icon(Icons.admin_panel_settings_outlined),
              title: Text('Admin Dashboard'),
              subtitle: Text('Chỉ dành cho Admin'),
            ),
          ],
          ListTile(
            leading: const Icon(Icons.logout),
            title: const Text('Đăng xuất'),
            onTap: onLogout,
          ),
        ],
      ),
    ),
  );
}
