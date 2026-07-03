<template>
  <div class="fixed bottom-24 right-8 z-50 flex flex-col items-center gap-3">
    <!-- LiveKit Connection Status -->
    <div v-if="isConnected" class="text-xs font-semibold bg-emerald-100 text-emerald-700 px-3 py-1 rounded-full shadow-sm animate-pulse flex items-center gap-1.5">
      <div class="w-1.5 h-1.5 bg-emerald-500 rounded-full"></div>
      Voice Active
    </div>

    <!-- Main Mic Button -->
    <button 
      @click="toggleVoice" 
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

const isConnected = ref(false);
let room: Room | null = null;

const connectLiveKit = async () => {
  try {
    // Replace with real backend call when Voice WebRTC is fully hooked up.
    const url = import.meta.env.VITE_LIVEKIT_URL || 'ws://localhost:7880';
    
    // Fetch token from backend
    const apiBase = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5240';
    const response = await fetch(`${apiBase}/api/Speech/token?room=omni-room&participant=user-123`);
    if (!response.ok) {
      throw new Error('Failed to fetch LiveKit token');
    }
    const data = await response.json();
    const token = data.token;

    if (!token) {
      throw new Error('Received empty token from backend');
    }

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
        document.body.appendChild(audioElement);
      }
    });

    await room.connect(url, token);
    await room.localParticipant.setMicrophoneEnabled(true, {
        deviceId: 'default'
    });
    
    isConnected.value = true;
  } catch (error) {
    console.error('LiveKit connection error:', error);
  }
};

const disconnectLiveKit = () => {
  if (room) {
    room.disconnect();
    room = null;
  }
  isConnected.value = false;
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
