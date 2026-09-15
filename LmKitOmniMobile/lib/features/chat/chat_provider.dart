import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_provider.dart';
import 'chat_models.dart';
import 'chat_repository.dart';

final chatRepositoryProvider = Provider<ChatRepository>(
  (ref) => ChatRepository(ref.watch(apiClientProvider)),
);

class ChatSessionsController extends AsyncNotifier<List<ChatSessionModel>> {
  @override
  Future<List<ChatSessionModel>> build() =>
      ref.read(chatRepositoryProvider).listSessions();

  Future<void> reload() async {
    state = const AsyncLoading();
    state = await AsyncValue.guard(
      () => ref.read(chatRepositoryProvider).listSessions(),
    );
  }

  Future<ChatSessionModel> create({
    String? projectId,
    bool ephemeral = false,
  }) async {
    final created = await ref
        .read(chatRepositoryProvider)
        .createSession(projectId: projectId, ephemeral: ephemeral);
    final current = state.asData?.value ?? const <ChatSessionModel>[];
    state = AsyncData([created, ...current]);
    return created;
  }

  Future<void> rename(ChatSessionModel session, String title) async {
    await ref.read(chatRepositoryProvider).renameSession(session.id, title);
    final current = state.asData?.value ?? const <ChatSessionModel>[];
    state = AsyncData([
      for (final item in current)
        item.id == session.id
            ? ChatSessionModel(
                id: item.id,
                title: title,
                createdAt: item.createdAt,
                customAgentId: item.customAgentId,
                agentName: item.agentName,
                agentIcon: item.agentIcon,
                projectId: item.projectId,
                isEphemeral: item.isEphemeral,
              )
            : item,
    ]);
  }

  Future<void> remove(ChatSessionModel session) async {
    await ref.read(chatRepositoryProvider).deleteSession(session.id);
    final current = state.asData?.value ?? const <ChatSessionModel>[];
    state = AsyncData(current.where((item) => item.id != session.id).toList());
  }
}

final chatSessionsProvider =
    AsyncNotifierProvider<ChatSessionsController, List<ChatSessionModel>>(
      ChatSessionsController.new,
    );

final chatMessagesProvider = FutureProvider.autoDispose
    .family<List<ChatMessageModel>, String>(
      (ref, sessionId) =>
          ref.read(chatRepositoryProvider).getMessages(sessionId),
    );
