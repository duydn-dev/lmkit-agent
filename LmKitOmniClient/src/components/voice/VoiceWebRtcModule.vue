<template>
  <div class="fixed bottom-24 right-8 z-50 flex flex-col items-center gap-3">
    <!-- LiveKit Connection Status -->
    <div v-if="isConnected" class="text-xs font-semibold bg-emerald-100 text-emerald-700 px-3 py-1 rounded-full shadow-sm animate-pulse flex items-center gap-1.5">
      <div class="w-1.5 h-1.5 bg-emerald-500 rounded-full"></div>
      Voice Active
    </div>
    <div v-if="voiceError" role="alert" class="max-w-56 text-xs bg-red-50 text-red-700 border border-red-200 px-3 py-2 rounded-lg">{{ voiceError }}</div>

    <!-- Main Mic Button -->
    <button 
      @click="toggleVoice" 
      :aria-pressed="isConnected"
      :aria-label="isConnected ? 'Ngắt kết nối thoại' : 'Bắt đầu kết nối thoại'"
      class="relative flex items-center justify-center w-14 h-14 rounded-full shadow-xl transition-all duration-300"
      :class="[
        isConnected 
          ? 'bg-red-500 hover:bg-red-600 text-white' 
          : 'bg-chatgpt-brand hover:bg-sky-600 text-white hover:scale-105'
      ]"
    >
      <!-- Ripple effect when connected and speaking -->
      <span v-if="isConnected" class="absolute inset-0 rounded-full bg-red-400 opacity-50 animate-ping"></span>
      
      <i class="text-2xl" :class="isConnected ? 'pi pi-phone' : 'pi pi-microphone'"></i>
    </button>
  </div>
</template>

<script setup lang="ts">
import { ref, onUnmounted } from 'vue';
import { Room, RoomEvent } from 'livekit-client';
import { http } from '@/api/http';
import { errorMessage, readApiError } from '@/api/errors';

const isConnected = ref(false);
const voiceError = ref('');
let room: Room | null = null;

const connectLiveKit = async () => {
  try {
    voiceError.value = '';
    const wsProtocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const url = import.meta.env.VITE_LIVEKIT_URL || `${wsProtocol}//${window.location.hostname}:7880`;
    
    const response = await http.get('/api/Speech/token?room=omni-room');
    if (!response.ok) throw new Error(await readApiError(response, 'Không thể lấy token thoại'));
    const data = await response.json();
    const token = data.token;

    if (!token) throw new Error('Máy chủ không trả về token thoại.');

    room = new Room({
      audioCaptureDefaults: {
        autoGainControl: true,
        echoCancellation: true,
        noiseSuppression: true,
      }
    });

    room.on(RoomEvent.TrackSubscribed, (track) => {
      if (track.kind === 'audio') {
        const audioElement = track.attach();
        audioElement.dataset.lmkitVoice = 'true';
        document.body.appendChild(audioElement);
      }
    });
    room.on(RoomEvent.TrackUnsubscribed, (track) => {
      track.detach().forEach((element) => element.remove());
    });

    await room.connect(url, token);
    await room.localParticipant.setMicrophoneEnabled(true, {
        deviceId: 'default'
    });
    
    isConnected.value = true;
  } catch (error) {
    voiceError.value = errorMessage(error, 'Không thể kết nối thoại. Vui lòng thử lại.');
    disconnectLiveKit();
  }
};

const disconnectLiveKit = () => {
  if (room) {
    room.disconnect();
    room = null;
  }
  isConnected.value = false;
  document.querySelectorAll('audio[data-lmkit-voice]').forEach((element) => element.remove());
};

const toggleVoice = () => {
  if (isConnected.value) {
    disconnectLiveKit();
  } else {
    connectLiveKit();
  }
};

onUnmounted(() => {
  disconnectLiveKit();
});
</script>
