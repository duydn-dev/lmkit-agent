import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:livekit_client/livekit_client.dart';

import '../../app/theme.dart';

import '../../app/ui/app_controls.dart';
import '../../core/auth/auth_provider.dart';
import '../../core/config/app_config_provider.dart';
import '../../core/network/api_client.dart';
import '../../core/network/api_exception.dart';

/// Token phòng thoại do `GET /api/speech/token` cấp.
class VoiceTokenModel {
  const VoiceTokenModel({
    required this.token,
    this.room = 'omni-room',
    this.agentJoining = false,
    this.agentUnavailableReason,
  });

  final String token;
  final String room;

  /// Server xác nhận agent thật sự sẽ tham gia, không chỉ là đã được yêu cầu.
  final bool agentJoining;
  final String? agentUnavailableReason;

  factory VoiceTokenModel.fromJson(Map<String, dynamic> json) =>
      VoiceTokenModel(
        token: json['token'] as String? ?? '',
        room: json['room'] as String? ?? 'omni-room',
        agentJoining: json['agent'] as bool? ?? false,
        agentUnavailableReason: json['agentUnavailableReason'] as String?,
      );
}

final voiceRoomRepositoryProvider = Provider<VoiceRoomRepository>(
  (ref) => VoiceRoomRepository(ref.watch(apiClientProvider)),
);

/// API phòng thoại: cấp token và thu hồi quyền cho agent.
class VoiceRoomRepository {
  VoiceRoomRepository(this._client);

  final ApiClient _client;

  static const defaultRoom = 'omni-room';

  /// [requestAgent] = `agent=true`: đây là sự đồng ý tường minh để agent phía
  /// server tham gia phòng. Không có nó thì server không ghi âm và không ai nghe.
  Future<VoiceTokenModel> token({
    String room = defaultRoom,
    bool requestAgent = true,
    String? voice,
  }) async {
    final response = await _client.get(
      '/api/speech/token',
      queryParameters: {
        'room': room,
        'agent': requestAgent,
        if (voice != null && voice.isNotEmpty) 'voice': voice,
      },
    );
    return VoiceTokenModel.fromJson(
      Map<String, dynamic>.from(response.data as Map),
    );
  }

  /// Thu hồi đồng ý ngay lập tức: `DELETE /api/speech/agent-session`.
  Future<void> revokeAgent({String room = defaultRoom}) => _client.delete(
    '/api/speech/agent-session',
    queryParameters: {'room': room},
  );
}

enum VoiceStatus { idle, connecting, connected, error }

class VoiceRoomState {
  const VoiceRoomState({
    this.status = VoiceStatus.idle,
    this.muted = false,
    this.agentJoining = false,
    this.agentNote,
    this.room,
    this.participants = 0,
    this.error,
  });

  final VoiceStatus status;
  final bool muted;

  /// Agent phía server có thực sự tham gia phòng hay không.
  final bool agentJoining;

  /// Lý do agent không tham gia (server trả về), nếu có.
  final String? agentNote;
  final String? room;
  final int participants;
  final String? error;

  bool get isConnected => status == VoiceStatus.connected;
  bool get isBusy => status == VoiceStatus.connecting;

  VoiceRoomState copyWith({
    VoiceStatus? status,
    bool? muted,
    bool? agentJoining,
    String? agentNote,
    String? room,
    int? participants,
    String? error,
    bool clearError = false,
  }) => VoiceRoomState(
    status: status ?? this.status,
    muted: muted ?? this.muted,
    agentJoining: agentJoining ?? this.agentJoining,
    agentNote: agentNote ?? this.agentNote,
    room: room ?? this.room,
    participants: participants ?? this.participants,
    error: clearError ? null : (error ?? this.error),
  );
}

/// Điều khiển phòng thoại thời gian thực (LiveKit) cho tab Chat.
///
/// Tương ứng nút micro nổi của desktop: lấy token có `agent=true`, kết nối vào
/// phòng đã được scope theo tenant/user, bật micro, cho phép mute mà không ngắt
/// phòng, và thu hồi quyền agent khi ngắt kết nối.
class VoiceRoomController extends Notifier<VoiceRoomState> {
  Room? _room;
  EventsListener<RoomEvent>? _listener;

  @override
  VoiceRoomState build() {
    ref.onDispose(() => _teardown(revokeAgentConsent: true));
    return const VoiceRoomState();
  }

  Future<void> connect({bool requestAgent = true, String? voice}) async {
    if (state.isConnected || state.isBusy) return;
    state = state.copyWith(status: VoiceStatus.connecting, clearError: true);
    try {
      final config = ref.read(appConfigProvider);
      final token = await ref
          .read(voiceRoomRepositoryProvider)
          .token(requestAgent: requestAgent, voice: voice);
      if (token.token.isEmpty) {
        throw StateError('Server không trả về token thoại.');
      }

      final room = Room(
        roomOptions: const RoomOptions(adaptiveStream: true, dynacast: true),
      );
      _room = room;
      _listener = room.createListener()
        ..on<RoomConnectedEvent>((event) {
          state = state.copyWith(
            status: VoiceStatus.connected,
            room: event.room.name,
            participants: event.room.remoteParticipants.length + 1,
          );
        })
        ..on<RoomDisconnectedEvent>((event) {
          state = state.copyWith(
            status: VoiceStatus.idle,
            room: null,
            participants: 0,
            muted: false,
          );
        })
        ..on<RoomReconnectingEvent>((_) {
          state = state.copyWith(error: 'Mất kết nối, đang thử lại…');
        })
        ..on<RoomReconnectedEvent>((_) {
          state = state.copyWith(clearError: true);
        })
        ..on<ParticipantConnectedEvent>((_) => _refreshParticipants())
        ..on<ParticipantDisconnectedEvent>((_) => _refreshParticipants())
        ..on<AudioPlaybackStatusChanged>((_) {
          // iOS Safari cần thao tác người dùng để phát audio; mobile thì không.
          if (!room.canPlaybackAudio) {
            state = state.copyWith(error: 'Thiết bị đang chặn phát âm thanh.');
          }
        });

      await room.connect(config.livekitUrl, token.token);
      await room.localParticipant?.setMicrophoneEnabled(true);

      state = state.copyWith(
        status: VoiceStatus.connected,
        room: token.room,
        agentJoining: token.agentJoining,
        agentNote: token.agentUnavailableReason,
        participants: room.remoteParticipants.length + 1,
      );
    } catch (error) {
      await _teardown(revokeAgentConsent: true);
      state = state.copyWith(
        status: VoiceStatus.error,
        error: error is ApiException ? error.message : error.toString(),
      );
    }
  }

  /// Tắt/bật micro mà không ngắt phòng (agent vẫn ở lại).
  Future<void> toggleMute() async {
    final participant = _room?.localParticipant;
    if (participant == null) return;
    final nextMuted = !state.muted;
    await participant.setMicrophoneEnabled(!nextMuted);
    state = state.copyWith(muted: nextMuted);
  }

  Future<void> disconnect() async {
    await _teardown(revokeAgentConsent: true);
    state = const VoiceRoomState();
  }

  void _refreshParticipants() {
    final room = _room;
    if (room == null) return;
    state = state.copyWith(participants: room.remoteParticipants.length + 1);
  }

  /// Ngắt phòng và (mặc định) thu hồi luôn quyền của agent để server không còn
  /// chờ trong phòng. Im lặng với lỗi mạng: ngắt phía client đã thành công.
  Future<void> _teardown({required bool revokeAgentConsent}) async {
    final listener = _listener;
    _listener = null;
    listener?.dispose();

    final room = _room;
    _room = null;
    if (room != null) {
      try {
        await room.disconnect();
      } catch (_) {
        // Phòng đã đóng hoặc mạng đã mất — không cần báo lỗi cho người dùng.
      }
      try {
        await room.dispose();
      } catch (_) {
        // dispose() chỉ giải phóng tài nguyên native.
      }
    }

    if (revokeAgentConsent) {
      try {
        await ref.read(voiceRoomRepositoryProvider).revokeAgent();
      } catch (_) {
        // Grant cũng tự hết hạn theo TTL ở server.
      }
    }
  }
}

final voiceRoomProvider = NotifierProvider<VoiceRoomController, VoiceRoomState>(
  VoiceRoomController.new,
);

/// Màn phòng thoại: kết nối, mute, ngắt kết nối và nói rõ agent có tham gia hay
/// không (server luôn trả về sự thật này thay vì chỉ phản ánh yêu cầu).
class VoiceRoomScreen extends ConsumerWidget {
  const VoiceRoomScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final state = ref.watch(voiceRoomProvider);
    final controller = ref.read(voiceRoomProvider.notifier);
    final scheme = Theme.of(context).colorScheme;

    return Scaffold(
      appBar: AppTopBar(title: const Text('Thoại thời gian thực')),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          AppCard(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Icon(
                      state.isConnected ? Icons.mic : Icons.mic_off,
                      color: state.isConnected
                          ? AppTheme.success
                          : scheme.outline,
                    ),
                    const SizedBox(width: 8),
                    Text(
                      state.isConnected
                          ? (state.muted ? 'Đang tắt micro' : 'Đang mở micro')
                          : state.isBusy
                          ? 'Đang kết nối…'
                          : 'Chưa kết nối',
                      style: Theme.of(context).textTheme.titleMedium,
                    ),
                  ],
                ),
                if (state.room != null)
                  Padding(
                    padding: const EdgeInsets.only(top: 4),
                    child: Text(
                      'Phòng: ${state.room}',
                      style: Theme.of(context).textTheme.bodySmall,
                    ),
                  ),
                if (state.isConnected)
                  Padding(
                    padding: const EdgeInsets.only(top: 4),
                    child: Text(
                      '${state.participants} người trong phòng',
                      style: Theme.of(context).textTheme.bodySmall,
                    ),
                  ),
                Padding(
                  padding: const EdgeInsets.only(top: 8),
                  child: Text(
                    state.agentJoining
                        ? 'Agent AI đã tham gia phòng và có thể nghe bạn nói.'
                        : 'Không có agent AI trong phòng'
                              '${state.agentNote == null ? '' : ' — ${state.agentNote}'}',
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: state.agentJoining
                          ? AppTheme.success
                          : AppTheme.textMuted,
                    ),
                  ),
                ),
                if (state.error != null)
                  Padding(
                    padding: const EdgeInsets.only(top: 8),
                    child: AppAlert(message: state.error!, isError: true),
                  ),
                const SizedBox(height: 12),
                if (!state.isConnected)
                  AppPrimaryButton(
                    label: 'Bắt đầu thoại',
                    icon: Icons.call,
                    busy: state.isBusy,
                    onPressed: () => controller.connect(),
                  )
                else ...[
                  AppPrimaryButton(
                    label: state.muted ? 'Bật micro' : 'Tắt micro',
                    icon: state.muted ? Icons.mic : Icons.mic_off,
                    onPressed: controller.toggleMute,
                  ),
                  const SizedBox(height: 8),
                  AppDestructiveButton(
                    label: 'Ngắt kết nối',
                    icon: Icons.call_end,
                    onPressed: () async {
                      await controller.disconnect();
                      if (context.mounted) Navigator.of(context).maybePop();
                    },
                  ),
                ],
              ],
            ),
          ),
          const SizedBox(height: 8),
          Text(
            'Khi kết nối, app yêu cầu server cấp token cho phòng của chính bạn '
            'kèm đồng ý cho agent AI tham gia. Ngắt kết nối sẽ thu hồi đồng ý đó '
            'ngay lập tức.',
            style: Theme.of(context).textTheme.bodySmall,
          ),
        ],
      ),
    );
  }
}
