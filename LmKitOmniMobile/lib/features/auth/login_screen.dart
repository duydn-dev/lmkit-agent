import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:forui/forui.dart';

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
      // Không đặt `backgroundColor` để nền trống đồng của app hiện ra sau thẻ
      // đăng nhập (giống web); mọi Scaffold khác cũng vậy — xem `AppTheme`.
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
                    height: 64,
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
                      // Đa tenant: màn đăng nhập chỉ nêu BỘ chủ quản chung,
                      // không nêu tên một đơn vị/tenant cụ thể.
                      'Bộ Nông nghiệp và Môi trường',
                      textAlign: TextAlign.center,
                      style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: AppTheme.textMuted,
                      ),
                    ),
                  ),
                  const SizedBox(height: 20),
                  // Thẻ đăng nhập theo design system: viền phẳng bo 12, nền
                  // trắng — không bóng riêng (FCard của Forui không dùng bóng,
                  // đúng như mọi thẻ khác trong app).
                  AppCard(
                    padding: const EdgeInsets.all(20),
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
                          FTextFormField(
                            control: FTextFieldControl.managed(
                              controller: _email,
                            ),
                            keyboardType: TextInputType.emailAddress,
                            textInputAction: TextInputAction.next,
                            autofillHints: const [
                              AutofillHints.username,
                              AutofillHints.email,
                            ],
                            label: const Text('Tên tài khoản'),
                            // Web (`LoginView.vue`) có cả nhãn *và* placeholder
                            // cùng nội dung: nhãn đứng trên, chữ mờ nằm trong ô
                            // để ô trống vẫn biết mình dùng làm gì.
                            hint: 'Tên tài khoản',
                            prefixBuilder: (context, style, variants) =>
                                FTextField.prefixIconBuilder(
                                  context,
                                  style,
                                  variants,
                                  const Icon(Icons.person_outline, size: 20),
                                ),
                            validator: (value) =>
                                value == null || value.trim().isEmpty
                                ? 'Vui lòng nhập tên tài khoản.'
                                : null,
                          ),
                          const SizedBox(height: 16),
                          FTextFormField.password(
                            control: FTextFieldControl.managed(
                              controller: _password,
                            ),
                            textInputAction: TextInputAction.done,
                            autofillHints: const [AutofillHints.password],
                            label: const Text('Mật khẩu'),
                            hint: 'Mật khẩu',
                            // Cùng vai trò với icon người ở ô tên tài khoản: cho
                            // người dùng một điểm neo thị giác, và icon không bị
                            // nhảy khi ô hiện thông báo lỗi.
                            prefixBuilder: (context, style, obscure, variants) =>
                                FTextField.prefixIconBuilder(
                                  context,
                                  style,
                                  variants,
                                  const Icon(Icons.lock_outline, size: 20),
                                ),
                            onSubmit: (_) => _submit(),
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
                  // API URL là giá trị build-time
                  // (`--dart-define-from-file=env/<flavor>.json`) và không hiển
                  // thị ở đây nữa: người dùng cuối không cần thấy địa chỉ máy chủ.
                  TextButton.icon(
                    onPressed: () => Navigator.of(context).push(
                      MaterialPageRoute<void>(
                        builder: (_) => const SharedChatScreen(),
                      ),
                    ),
                    icon: const Icon(Icons.link, size: 18),
                    label: const Text('Xem đoạn chat được chia sẻ'),
                  ),
                  const SizedBox(height: 4),
                  // Cùng dòng ghi chú với màn "Danh sách chức năng": đây là môi
                  // trường thử nghiệm, không phải bản vận hành chính thức.
                  Text(
                    'Hệ thống thử nghiệm',
                    textAlign: TextAlign.center,
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: AppTheme.textMuted,
                    ),
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
