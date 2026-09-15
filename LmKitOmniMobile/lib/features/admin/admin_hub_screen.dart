import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';
import 'api_keys_screen.dart';
import 'audit_log_screen.dart';
import 'database_connections_screen.dart';
import 'knowledge_base_screen.dart';
import 'lora_adapters_screen.dart';
import 'mcp_servers_screen.dart';
import 'tenants_screen.dart';
import 'users_screen.dart';
import 'widget_settings_screen.dart';

/// Hub quản trị: gom toàn bộ màn admin của bản desktop vào một chỗ.
class AdminHubScreen extends ConsumerWidget {
  const AdminHubScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final entries =
        <({String title, String subtitle, IconData icon, WidgetBuilder build})>[
          (
            title: 'Người dùng',
            subtitle: 'Tạo tài khoản, phân quyền và khoá/mở khoá người dùng',
            icon: Icons.group_outlined,
            build: (_) => const UsersScreen(),
          ),
          (
            title: 'API Keys',
            subtitle: 'Cấp khóa truy cập API kèm hạn dùng và hạn mức',
            icon: Icons.vpn_key_outlined,
            build: (_) => const ApiKeysScreen(),
          ),
          (
            title: 'Tenant Management',
            subtitle: 'Tạo, sửa, xoá tenant và logo thương hiệu',
            icon: Icons.apartment_outlined,
            build: (_) => const TenantsScreen(),
          ),
          (
            title: 'MCP Servers',
            subtitle: 'Kết nối MCP server cho agent và OAuth 2.0',
            icon: Icons.hub_outlined,
            build: (_) => const McpServersScreen(),
          ),
          (
            title: 'Cơ sở kiến thức',
            subtitle: 'Nạp tài liệu văn bản và truy vấn thử kho tri thức',
            icon: Icons.menu_book_outlined,
            build: (_) => const KnowledgeBaseScreen(),
          ),
          (
            title: 'Database Connections',
            subtitle: 'Kết nối CSDL ngoài, test và đánh lại chỉ mục',
            icon: Icons.storage_outlined,
            build: (_) => const DatabaseConnectionsScreen(),
          ),
          (
            title: 'LoRA Adapters',
            subtitle: 'Tải adapter, bật/tắt và gán cho custom agent',
            icon: Icons.tune_outlined,
            build: (_) => const LoraAdaptersScreen(),
          ),
          (
            title: 'Widget Settings',
            subtitle: 'Cấu hình widget nhúng, origin cho phép và quota',
            icon: Icons.widgets_outlined,
            build: (_) => const WidgetSettingsScreen(),
          ),
          (
            title: 'Nhật ký kiểm toán',
            subtitle:
                'Lọc theo actor, hành động, đối tượng và khoảng thời gian',
            icon: Icons.receipt_long_outlined,
            build: (_) => const AuditLogScreen(),
          ),
        ];

    return Scaffold(
      appBar: AppBar(title: const Text('Quản trị')),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          Text(
            'Các màn quản trị chỉ hiển thị với tài khoản Admin. Mọi thao tác ở đây '
            'ảnh hưởng tới toàn tenant.',
            style: Theme.of(
              context,
            ).textTheme.bodyMedium?.copyWith(color: AppTheme.textMuted),
          ),
          const SizedBox(height: 12),
          for (final entry in entries)
            Card(
              child: ListTile(
                leading: Icon(entry.icon),
                title: Text(entry.title),
                subtitle: Text(entry.subtitle),
                trailing: const Icon(Icons.chevron_right),
                onTap: () => Navigator.of(
                  context,
                ).push(MaterialPageRoute<void>(builder: entry.build)),
              ),
            ),
        ],
      ),
    );
  }
}
