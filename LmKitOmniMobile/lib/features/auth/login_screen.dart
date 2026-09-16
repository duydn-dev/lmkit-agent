import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_svg/flutter_svg.dart';

import '../../app/theme.dart';
import '../../app/ui/app_controls.dart';
import '../../core/auth/auth_provider.dart';
import '../../core/config/app_config_provider.dart';
import '../share/shared_chat_screen.dart';

/// Màn đăng nhập, bố cục theo `LoginView.vue`: Quốc huy + tên đơn vị trên cùng,
/// thẻ trắng bo góc, hai ô nhập có icon, nút xanh `gov-blue-dark`.
class LoginScreen extends ConsumerStatefulWidget {
  const LoginScreen({super.key, this.initialError});

  final String? initialError;

  @override
  ConsumerState<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends ConsumerState<LoginScreen> {
  final _formKey = GlobalKey<FormState>();
  final _email = TextEditingController();
  final _password = TextEditingController();

  @override
  void initState() {
    super.initState();
    // Chế độ dữ liệu mẫu: điền sẵn tài khoản demo để vào app chỉ bằng một lần
    // bấm — người xem giao diện không phải gõ gì.
    if (ref.read(appConfigProvider).useMockData) {
      _email.text = 'admin@cila.gov.vn';
      _password.text = 'demo1234';
    }
  }

  @override
  void dispose() {
    _email.dispose();
    _password.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;
    await ref
        .read(authControllerProvider.notifier)
        .login(email: _email.text.trim(), password: _password.text);
  }

  @override
  Widget build(BuildContext context) {
    final auth = ref.watch(authControllerProvider);
    final error = auth.hasError ? auth.error.toString() : widget.initialError;
    final loading = auth.isLoading;

    return Scaffold(
      backgroundColor: AppTheme.surface,
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(20),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  // Khối nhận diện: Quốc huy + tên đơn vị, giống hệt web.
                  SvgPicture.asset(
                    'assets/images/quochuy.svg',
                    height: 88,
                    semanticsLabel: 'Quốc huy Việt Nam',
                  ),
                  const SizedBox(height: 16),
                  // Web: `text-2xl font-bold` cho tiêu đề, `text-sm text-gray-600`
                  // cho tên đơn vị.
                  Text(
                    'Trợ lý ảo - CILA AI',
                    textAlign: TextAlign.center,
                    style: Theme.of(context).textTheme.headlineSmall,
                  ),
                  const SizedBox(height: 6),
                  Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 8),
                    child: Text(
                      'Trung tâm Thông tin lưu trữ và Thư viện tài nguyên môi '
                      'trường quốc gia',
                      textAlign: TextAlign.center,
                      style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: AppTheme.textMuted,
                      ),
                    ),
                  ),
                  const SizedBox(height: 20),
                  Container(
                    padding: const EdgeInsets.all(20),
                    decoration: BoxDecoration(
                      color: Colors.white,
                      borderRadius: BorderRadius.circular(16),
                      border: Border.all(color: AppTheme.border),
                      boxShadow: const [
                        BoxShadow(
                          color: Color(0x14111827),
                          blurRadius: 18,
                          offset: Offset(0, 6),
                        ),
                      ],
                    ),
                    child: Form(
                      key: _formKey,
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          if (ref.watch(appConfigProvider).useMockData) ...[
                            const AppAlert(
                              title: 'Đang ở chế độ dữ liệu mẫu',
                              message:
                                  'Mọi tài khoản đều đăng nhập được. Toàn bộ số '
                                  'liệu bạn thấy là dữ liệu giả dựng sẵn trong '
                                  'app, không gọi ra máy chủ.',
                            ),
                            const SizedBox(height: 4),
                          ],
                          if (error != null) AppErrorBanner(message: error),
                          TextFormField(
                            controller: _email,
                            keyboardType: TextInputType.emailAddress,
                            textInputAction: TextInputAction.next,
                            autofillHints: const [
                              AutofillHints.username,
                              AutofillHints.email,
                            ],
                            decoration: const InputDecoration(
                              labelText: 'Tên tài khoản',
                              prefixIcon: Icon(Icons.person_outline, size: 20),
                            ),
                            validator: (value) =>
                                value == null || value.trim().isEmpty
                                ? 'Vui lòng nhập tên tài khoản.'
                                : null,
                          ),
                          const SizedBox(height: 16),
                          TextFormField(
                            controller: _password,
                            obscureText: true,
                            textInputAction: TextInputAction.done,
                            autofillHints: const [AutofillHints.password],
                            onFieldSubmitted: (_) => _submit(),
                            decoration: const InputDecoration(
                              labelText: 'Mật khẩu',
                              prefixIcon: Icon(Icons.lock_outline, size: 20),
                            ),
                            validator: (value) => value == null || value.isEmpty
                                ? 'Vui lòng nhập mật khẩu.'
                                : null,
                          ),
                          const SizedBox(height: 20),
                          // Nút của design system: giữ nhãn khi đang tải (thay vì
                          // thay cả nút bằng vòng xoay) và nhãn luôn tự cắt gọn.
                          AppPrimaryButton(
                            label: 'Đăng Nhập',
                            icon: Icons.login,
                            busy: loading,
                            onPressed: _submit,
                          ),
                        ],
                      ),
                    ),
                  ),
                  const SizedBox(height: 12),
                  // Máy chủ chỉ **hiển thị**. API URL là giá trị build-time
                  // (`--dart-define-from-file=env/<flavor>.json`): app không có
                  // đường nào đổi nó lúc chạy, nên một lần bấm nhầm cũng không
                  // để lại URL sai trên thiết bị.
                  _ServerRow(label: ref.watch(appConfigProvider).apiBaseUrl),
                  TextButton.icon(
                    onPressed: () => Navigator.of(context).push(
                      MaterialPageRoute<void>(
                        builder: (_) => const SharedChatScreen(),
                      ),
                    ),
                    icon: const Icon(Icons.link, size: 18),
                    label: const Text('Xem đoạn chat được chia sẻ'),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// Dòng hiển thị máy chủ đang cấu hình (chỉ đọc).
///
/// Cố ý không phải nút: người dùng cần biết app đang nói chuyện với máy chủ nào
/// khi báo lỗi cho bộ phận hỗ trợ, nhưng không cần — và không nên — đổi được nó.
class _ServerRow extends StatelessWidget {
  const _ServerRow({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) => Row(
    mainAxisAlignment: MainAxisAlignment.center,
    mainAxisSize: MainAxisSize.min,
    children: [
      const Icon(Icons.dns_outlined, size: 16, color: AppTheme.textMuted),
      const SizedBox(width: 6),
      Flexible(
        child: Text(
          'Máy chủ: $label',
          overflow: TextOverflow.ellipsis,
          maxLines: 1,
          style: Theme.of(context).textTheme.bodySmall,
        ),
      ),
    ],
  );
}
