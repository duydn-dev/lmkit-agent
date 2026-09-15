import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:forui/forui.dart';

import '../core/auth/auth_provider.dart';
import '../features/auth/login_screen.dart';
import '../features/home/home_screen.dart';
import 'theme.dart';

class LmKitOmniApp extends ConsumerWidget {
  const LmKitOmniApp({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final auth = ref.watch(authControllerProvider);
    // Chỉ sáng, đúng như web: `style.css` ép `color-scheme: light` và ghi rõ
    // "The application is intentionally light-themed". Theo `ThemeMode.system`
    // sẽ cho ra giao diện khác web trên máy đang bật chế độ tối.
    final forui = AppTheme.forui();

    return MaterialApp(
      title: 'Trợ lý ảo - CILA AI',
      debugShowCheckedModeBanner: false,
      theme: AppTheme.material(forui),
      themeMode: ThemeMode.light,
      builder: (context, child) =>
          FTheme(data: forui, child: child ?? const SizedBox.shrink()),
      home: auth.when(
        loading: () => const _BootScreen(),
        error: (error, _) => LoginScreen(initialError: error.toString()),
        data: (session) =>
            session == null ? const LoginScreen() : const HomeScreen(),
      ),
    );
  }
}

class _BootScreen extends StatelessWidget {
  const _BootScreen();

  @override
  Widget build(BuildContext context) => Scaffold(
    body: Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: const [
          CircularProgressIndicator(),
          SizedBox(height: 16),
          Text('Đang tải cấu hình…'),
        ],
      ),
    ),
  );
}
