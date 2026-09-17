import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/theme.dart';

import '../../app/ui/app_controls.dart';
import 'admin_models.dart';
import 'admin_provider.dart';
import 'admin_repository.dart';
import 'admin_widgets.dart';

/// Quản lý người dùng trong tenant: tạo tài khoản, đổi quyền, khoá/mở khoá.
///
/// Tương ứng `UserManager.vue` của bản desktop. Backend chặn admin tự hạ quyền
/// chính mình, nên thao tác đổi quyền trả lỗi 400 và được hiển thị nguyên văn.
class UsersScreen extends ConsumerWidget {
  const UsersScreen({super.key});

  @override
  Widget build(
    BuildContext context,
    WidgetRef ref,
  ) => AdminListView<AdminUserModel>(
    title: 'Người dùng',
    description:
        'Cấp tài khoản, phân quyền và khoá/mở khoá người dùng trong tenant.',
    searchHint: 'Tìm theo email hoặc họ tên',
    emptyText: 'Chưa có người dùng nào.',
    fetch: (search) => ref
        .read(adminRepositoryProvider)
        .users(search: search)
        .then((page) => page.items),
    fabBuilder: (context, reload) => FloatingActionButton(
      tooltip: 'Tạo tài khoản',
      onPressed: () => _openForm(context, ref, reload),
      child: const Icon(Icons.person_add_alt),
    ),
    itemBuilder: (context, user, reload) =>
        _UserCard(user: user, reload: reload),
  );

  static Future<void> _openForm(
    BuildContext context,
    WidgetRef ref,
    Future<void> Function() reload,
  ) async {
    final created = await showAppDialog<bool>(
      context,
      builder: (_) => const _CreateUserDialog(),
    );
    if (created == true) await reload();
  }
}

class _UserCard extends ConsumerStatefulWidget {
  const _UserCard({required this.user, required this.reload});

  final AdminUserModel user;
  final Future<void> Function() reload;

  @override
  ConsumerState<_UserCard> createState() => _UserCardState();
}

class _UserCardState extends ConsumerState<_UserCard> {
  bool _busy = false;

  Future<void> _run(Future<void> Function() action) async {
    setState(() => _busy = true);
    try {
      await action();
      await widget.reload();
    } catch (error) {
      if (mounted) showAdminSnack(context, adminErrorMessage(error));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _changeRole() async {
    const roleOptions = ['Member', 'Admin'];
    final role = await showAppPicker<String>(
      context,
      title: 'Quyền hạn',
      items: roleOptions,
      labelOf: (option) => option,
      subtitleOf: (option) => option == 'Admin'
          ? 'Toàn quyền quản trị tenant.'
          : 'Chỉ dùng các tính năng nghiệp vụ.',
    );
    if (role == null || role == widget.user.role) return;
    await _run(
      () => ref
          .read(adminRepositoryProvider)
          .updateUserRole(widget.user.id, role),
    );
    if (mounted) {
      showAdminSnack(context, 'Đã đổi quyền ${widget.user.email} → $role');
    }
  }

  Future<void> _toggleStatus() async {
    final activating = !widget.user.isActive;
    final confirmed = await confirmAdminAction(
      context,
      title: activating ? 'Mở khoá tài khoản' : 'Khoá tài khoản',
      message: activating
          ? 'Cho phép ${widget.user.email} đăng nhập lại?'
          : '${widget.user.email} sẽ không đăng nhập được cho tới khi mở khoá.',
      confirmLabel: activating ? 'Mở khoá' : 'Khoá',
    );
    if (!confirmed) return;
    await _run(
      () => ref.read(adminRepositoryProvider).toggleUserStatus(widget.user.id),
    );
    if (mounted) {
      showAdminSnack(
        context,
        activating ? 'Đã mở khoá tài khoản.' : 'Đã khoá tài khoản.',
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final user = widget.user;
    return AppCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      user.fullName.isEmpty ? user.email : user.fullName,
                      style: Theme.of(context).textTheme.titleMedium,
                    ),
                    if (user.fullName.isNotEmpty)
                      Text(
                        user.email,
                        style: Theme.of(context).textTheme.bodySmall,
                      ),
                    const SizedBox(height: 6),
                    Wrap(
                      spacing: 6,
                      runSpacing: 6,
                      children: [
                        _Badge(
                          label: user.role,
                          color: user.isAdmin
                              ? AppTheme.govRed
                              : AppTheme.textMuted,
                        ),
                        _Badge(
                          label: user.isActive ? 'Hoạt động' : 'Đã khoá',
                          color: user.isActive
                              ? AppTheme.success
                              : AppTheme.govRed,
                        ),
                        if (user.isLockedOut)
                          _Badge(
                            label: 'Khoá tạm (sai mật khẩu)',
                            color: AppTheme.warning,
                          ),
                      ],
                    ),
                  ],
                ),
              ),
              if (_busy)
                const SizedBox(
                  width: 20,
                  height: 20,
                  child: CircularProgressIndicator(strokeWidth: 2),
                ),
            ],
          ),
          if (user.isLockedOut)
            Padding(
              padding: const EdgeInsets.only(top: 6),
              child: Text(
                'Sai mật khẩu ${user.failedLoginAttempts} lần — tự mở khoá lúc '
                '${_time(user.lockoutEnd!)}',
                style: Theme.of(
                  context,
                ).textTheme.bodySmall?.copyWith(color: AppTheme.warning),
              ),
            ),
          if (user.createdAt != null)
            Padding(
              padding: const EdgeInsets.only(top: 6),
              child: Text(
                'Tạo ngày ${_date(user.createdAt!)}',
                style: Theme.of(context).textTheme.bodySmall,
              ),
            ),
          const SizedBox(height: 8),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              AppSecondaryButton(
                label: 'Đổi quyền',
                icon: Icons.verified_user_outlined,
                onPressed: _busy ? null : _changeRole,
              ),
              AppSecondaryButton(
                label: user.isActive ? 'Khoá' : 'Mở khoá',
                icon: user.isActive ? Icons.lock_outline : Icons.lock_open,
                onPressed: _busy ? null : _toggleStatus,
              ),
            ],
          ),
        ],
      ),
    );
  }

  static String _date(DateTime value) {
    final local = value.toLocal();
    return '${local.day.toString().padLeft(2, '0')}/'
        '${local.month.toString().padLeft(2, '0')}/${local.year}';
  }

  static String _time(DateTime value) {
    final local = value.toLocal();
    return '${local.hour.toString().padLeft(2, '0')}:'
        '${local.minute.toString().padLeft(2, '0')}';
  }
}

class _Badge extends StatelessWidget {
  const _Badge({required this.label, required this.color});

  final String label;
  final Color color;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
    decoration: BoxDecoration(
      color: color.withValues(alpha: 0.12),
      borderRadius: BorderRadius.circular(999),
      border: Border.all(color: color.withValues(alpha: 0.4)),
    ),
    child: Text(
      label,
      style: Theme.of(context).textTheme.labelMedium?.copyWith(color: color),
    ),
  );
}

class _CreateUserDialog extends ConsumerStatefulWidget {
  const _CreateUserDialog();

  @override
  ConsumerState<_CreateUserDialog> createState() => _CreateUserDialogState();
}

class _CreateUserDialogState extends ConsumerState<_CreateUserDialog> {
  final _email = TextEditingController();
  final _password = TextEditingController();
  final _fullName = TextEditingController();
  String _role = 'Member';
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _email.dispose();
    _password.dispose();
    _fullName.dispose();
    super.dispose();
  }

  /// Khớp quy tắc mật khẩu backend: ≥ 8 ký tự, có hoa, thường và số.
  String? _validate() {
    if (_email.text.trim().isEmpty || !_email.text.contains('@')) {
      return 'Email không hợp lệ.';
    }
    if (_fullName.text.trim().isEmpty) return 'Vui lòng nhập họ và tên.';
    final password = _password.text;
    if (password.length < 8) return 'Mật khẩu tối thiểu 8 ký tự.';
    if (!RegExp(r'[A-Z]').hasMatch(password) ||
        !RegExp(r'[a-z]').hasMatch(password) ||
        !RegExp(r'\d').hasMatch(password)) {
      return 'Mật khẩu cần có chữ hoa, chữ thường và số.';
    }
    return null;
  }

  Future<void> _submit() async {
    final error = _validate();
    if (error != null) {
      setState(() => _error = error);
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await ref
          .read(adminRepositoryProvider)
          .createUser(
            email: _email.text,
            password: _password.text,
            fullName: _fullName.text,
            role: _role,
          );
      if (mounted) Navigator.pop(context, true);
    } catch (failure) {
      if (mounted) {
        setState(() {
          _busy = false;
          _error = adminErrorMessage(failure);
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) => AppDialog(
    title: 'Tạo tài khoản mới',
    content: Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (_error != null) AppAlert(message: _error!, isError: true),
        AdminField(controller: _email, label: 'Email'),
        AdminField(controller: _fullName, label: 'Họ và tên'),
        AdminField(
          controller: _password,
          label: 'Mật khẩu',
          hint: 'Tối thiểu 8 ký tự, gồm chữ hoa, chữ thường và số',
          obscure: true,
        ),
        const SizedBox(height: 4),
        // Ô chọn của hệ thiết kế: `DropdownButtonFormField` của Material cần
        // tổ tiên `Material` mà `AppDialog` (Forui) không có — xem `AppSelectTile`.
        AppSelectTile<String>(
          label: 'Quyền hạn',
          icon: Icons.verified_user_outlined,
          value: _role,
          items: const ['Member', 'Admin'],
          labelOf: (value) => value,
          subtitleOf: (value) => value == 'Admin'
              ? 'Toàn quyền quản trị tenant.'
              : 'Chỉ dùng các tính năng nghiệp vụ.',
          helper: 'Admin có toàn quyền quản trị tenant.',
          enabled: !_busy,
          onChanged: (value) => setState(() => _role = value),
        ),
      ],
    ),
    actions: [
      AppSecondaryButton(
        label: 'Huỷ',
        onPressed: _busy ? null : () => Navigator.pop(context),
      ),
      const SizedBox(width: 10),
      AppPrimaryButton(
        label: 'Tạo tài khoản',
        busy: _busy,
        expand: false,
        onPressed: _submit,
      ),
    ],
  );
}
