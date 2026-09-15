import 'package:flutter_test/flutter_test.dart';
import 'package:lmkit_omni_mobile/features/chat/voice_room.dart';

void main() {
  group('VoiceTokenModel', () {
    test('đọc token, phòng và việc agent tham gia', () {
      final token = VoiceTokenModel.fromJson({
        'token': 'jwt',
        'room': 'tenant-user-omni-room',
        'agent': true,
      });

      expect(token.token, 'jwt');
      expect(token.room, 'tenant-user-omni-room');
      expect(token.agentJoining, isTrue);
      expect(token.agentUnavailableReason, isNull);
    });

    test('agent không tham gia thì giữ lý do server trả về', () {
      final token = VoiceTokenModel.fromJson({
        'token': 'jwt',
        'agent': false,
        'agentUnavailableReason':
            'The live voice agent is disabled on this server (Voice:LiveAgentEnabled).',
      });

      expect(token.agentJoining, isFalse);
      expect(token.agentUnavailableReason, contains('disabled'));
    });

    test('thiếu field thì dùng giá trị an toàn', () {
      final token = VoiceTokenModel.fromJson(const {});

      expect(token.token, isEmpty);
      expect(token.room, 'omni-room');
      expect(token.agentJoining, isFalse);
    });
  });

  group('VoiceRoomState', () {
    test('mặc định là chưa kết nối', () {
      const state = VoiceRoomState();

      expect(state.status, VoiceStatus.idle);
      expect(state.isConnected, isFalse);
      expect(state.isBusy, isFalse);
      expect(state.muted, isFalse);
      expect(state.participants, 0);
    });

    test('copyWith cập nhật từng phần và giữ giá trị cũ', () {
      const state = VoiceRoomState(
        status: VoiceStatus.connected,
        room: 'room-1',
        agentJoining: true,
        error: 'lỗi cũ',
      );

      final muted = state.copyWith(muted: true);

      expect(muted.muted, isTrue);
      expect(muted.status, VoiceStatus.connected);
      expect(muted.room, 'room-1');
      expect(muted.agentJoining, isTrue);
      // Lỗi cũ vẫn còn nếu không yêu cầu xoá.
      expect(muted.error, 'lỗi cũ');
    });

    test('clearError xoá lỗi dù không truyền error mới', () {
      final cleared = const VoiceRoomState(
        error: 'lỗi',
      ).copyWith(clearError: true);

      expect(cleared.error, isNull);
    });

    test('connecting được coi là đang bận, không phải đã kết nối', () {
      const state = VoiceRoomState(status: VoiceStatus.connecting);

      expect(state.isBusy, isTrue);
      expect(state.isConnected, isFalse);
    });

    test('trạng thái error không được coi là kết nối', () {
      const state = VoiceRoomState(status: VoiceStatus.error, error: 'boom');

      expect(state.isConnected, isFalse);
      expect(state.isBusy, isFalse);
      expect(state.error, 'boom');
    });
  });
}
