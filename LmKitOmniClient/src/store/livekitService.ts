import { Room, RoomEvent } from 'livekit-client';

export class LivekitService {
  private room: Room | null = null;
  
  public async connect(url: string, token: string): Promise<void> {
    if (this.room) {
      await this.disconnect();
    }
    
    this.room = new Room({
      audioCaptureDefaults: {
        autoGainControl: true,
        echoCancellation: true,
        noiseSuppression: true,
      }
    });

    this.room.on(RoomEvent.TrackSubscribed, (track) => {
      if (track.kind === 'audio') {
        const audioElement = track.attach();
        document.body.appendChild(audioElement);
      }
    });

    await this.room.connect(url, token);
    await this.room.localParticipant.setMicrophoneEnabled(true, {
        deviceId: 'default'
    });
  }
  
  public async disconnect(): Promise<void> {
    if (this.room) {
      this.room.disconnect();
      this.room = null;
    }
  }
}

export const livekitService = new LivekitService();
