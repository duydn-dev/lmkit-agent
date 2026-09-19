import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:forui/forui.dart';

import '../core/auth/auth_provider.dart';
import '../features/auth/login_screen.dart';
import '../features/home/home_screen.dart';
import 'ui/watermark_background.dart';
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
      // Watermark trống đồng nằm ở tầng app: mọi màn (chat, đăng nhập, các
      // trang đẩy sang…) dùng chung một nền, không phải bọc lại từng Scaffold.
      // Scaffold phải trong suốt — xem `AppTheme.material`.
      // Kiểu thanh hệ thống mặc định của app: **icon tối trên nền sáng**.
      //
      // `AppBar` tự đăng ký một `AnnotatedRegion` cùng loại (icon trắng trên
      // chrome navy), nên các màn có header vẫn giữ icon trắng như trước; lớp
      // này chỉ áp cho những màn **không** có header — trước hết là màn đăng
      // nhập. Thiếu nó, kiểu thanh trạng thái là thứ "di truyền" từ màn trước:
      // đăng xuất từ một màn header navy thì đồng hồ và sóng vẫn là icon trắng,
      // trên nền trắng của màn đăng nhập chúng biến mất hẳn.
      builder: (context, child) => AnnotatedRegion<SystemUiOverlayStyle>(
        value: SystemUiOverlayStyle.dark.copyWith(
          statusBarColor: Colors.transparent,
          systemNavigationBarColor: Colors.transparent,
          systemNavigationBarIconBrightness: Brightness.dark,
        ),
        child: FTheme(
          data: forui,
          child: WatermarkBackground(child: child ?? const SizedBox.shrink()),
        ),
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
