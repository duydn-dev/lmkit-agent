import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../network/api_client.dart';
import 'auth_models.dart';
import 'auth_repository.dart';
import 'secure_session_store.dart';

final secureStorageProvider = Provider<FlutterSecureStorage>(
  (_) => const FlutterSecureStorage(),
);

final sessionStoreProvider = Provider<SecureSessionStore>(
  (ref) => SecureSessionStore(ref.watch(secureStorageProvider)),
);

final authRepositoryProvider = Provider<AuthRepository>((ref) {
  late final AuthRepository repository;
  final store = ref.watch(sessionStoreProvider);
  final client = ApiClient(store: store, refresh: () => repository.refresh());
  repository = AuthRepository(client: client, store: store);
  return repository;
});

final apiClientProvider = Provider<ApiClient>((ref) {
  final repository = ref.watch(authRepositoryProvider);
  return repository.client;
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
