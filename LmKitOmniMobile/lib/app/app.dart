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
    final light = FTheme.neutral.light.touch;
    final dark = FTheme.neutral.dark.touch;

    return MaterialApp(
      title: 'CILA AI',
      debugShowCheckedModeBanner: false,
      theme: AppTheme.materialLight(light),
      darkTheme: dark.toApproximateMaterialTheme(),
      themeMode: ThemeMode.system,
      builder: (context, child) => FTheme(
        data: Theme.of(context).brightness == Brightness.dark ? dark : light,
        child: child ?? const SizedBox.shrink(),
      ),
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
  Widget build(BuildContext context) =>
      const Scaffold(body: Center(child: CircularProgressIndicator()));
}
