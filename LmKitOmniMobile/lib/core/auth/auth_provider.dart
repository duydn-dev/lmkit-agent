import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../config/app_config_provider.dart';
import '../network/api_client.dart';
import '../storage/secure_storage_provider.dart';
import 'auth_models.dart';
import 'auth_repository.dart';
import 'secure_session_store.dart';

final sessionStoreProvider = Provider<SecureSessionStore>(
  (ref) => SecureSessionStore(ref.watch(secureStorageProvider)),
);

final authRepositoryProvider = Provider<AuthRepository>(
  (ref) => AuthRepository(
    config: ref.watch(appConfigProvider),
    store: ref.watch(sessionStoreProvider),
  ),
);

/// Client cho các API cần đăng nhập. Phụ thuộc [appConfigProvider] nên đổi API
/// URL sẽ tự dựng lại Dio với base URL mới.
final apiClientProvider = Provider<ApiClient>((ref) {
  final store = ref.watch(sessionStoreProvider);
  final repository = ref.watch(authRepositoryProvider);
  return ApiClient(
    config: ref.watch(appConfigProvider),
    store: store,
    refresh: repository.refresh,
    onSessionExpired: () {
      store.clear();
      ref.invalidate(authControllerProvider);
    },
  );
});

class AuthController extends AsyncNotifier<AuthSession?> {
  @override
  Future<AuthSession?> build() => ref.read(authRepositoryProvider).restore();

  Future<void> login({required String email, required String password}) async {
    state = const AsyncLoading();
    state = await AsyncValue.guard(
      () => ref
          .read(authRepositoryProvider)
          .login(email: email, password: password),
    );
  }

  Future<void> logout() async {
    await ref.read(authRepositoryProvider).logout();
    state = const AsyncData(null);
  }

  /// Xoá phiên trên thiết bị mà không gọi API.
  ///
  /// Dùng khi token không còn hợp lệ hoặc khi vừa đổi API URL sang server khác.
  Future<void> clearLocalSession() async {
    await ref.read(sessionStoreProvider).clear();
    state = const AsyncData(null);
  }

  Future<void> refreshSession() async {
    final current = state.asData?.value;
    if (current == null) return;
    if (await ref.read(authRepositoryProvider).refresh()) {
      state = AsyncData(await ref.read(authRepositoryProvider).restore());
    } else {
      state = const AsyncData(null);
    }
  }
}

final authControllerProvider =
    AsyncNotifierProvider<AuthController, AuthSession?>(AuthController.new);
