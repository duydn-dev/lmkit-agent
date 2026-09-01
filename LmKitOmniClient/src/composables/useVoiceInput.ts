import { computed, onUnmounted, ref } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';

export interface UseVoiceInputOptions {
  /** Receives the trimmed transcription text on success. */
  onTranscript: (text: string) => void;
  /** Receives a user-facing Vietnamese error message on any failure. */
  onError: (message: string) => void;
  /** Hard cap on a single recording; defaults to 60 seconds. */
  maxDurationMs?: number;
}

const TRANSCRIBE_FALLBACK = 'Không thể chuyển giọng nói thành văn bản';

/**
 * Push-to-talk recorder for the chat composer.
 *
 * Captures raw PCM through the Web Audio API and encodes a mono 16-bit WAV
 * blob client-side, because the server's speech decoder (LM-Kit WaveFile)
 * parses WAV/RIFF only — MediaRecorder's webm/opus container is undecodable
 * there. ScriptProcessorNode is deprecated but universally supported and
 * dependency-free; capture failures surface as user-facing errors.
 *
 * Auto-stops after `maxDurationMs`, then uploads the blob as multipart field
 * `audio` (filename voice.wav) to ApiFactory.SPEECH.TRANSCRIBE_UPLOAD using
 * the same authed `http` FormData path as chat file uploads.
 *
 * Must be called inside a component `setup` (it registers onUnmounted cleanup
 * so an in-flight recording is discarded when the view is torn down).
 */
export function useVoiceInput(options: UseVoiceInputOptions) {
  const maxDurationMs = options.maxDurationMs ?? 60_000;

  /** False on browsers without getUserMedia/AudioContext — hide the button. */
  const isSupported = typeof navigator !== 'undefined'
    && typeof window !== 'undefined'
    && !!navigator.mediaDevices?.getUserMedia
    && typeof window.AudioContext !== 'undefined';

  const isRecording = ref(false);
  const isTranscribing = ref(false);
  const elapsedSeconds = ref(0);

  /** Elapsed time formatted as m:ss for the composer's subtle timer. */
  const elapsedLabel = computed(() => {
    const minutes = Math.floor(elapsedSeconds.value / 60);
    const seconds = elapsedSeconds.value % 60;
    return `${minutes}:${String(seconds).padStart(2, '0')}`;
  });

  let stream: MediaStream | null = null;
  let audioContext: AudioContext | null = null;
  let sourceNode: MediaStreamAudioSourceNode | null = null;
  let processorNode: ScriptProcessorNode | null = null;
  let muteNode: GainNode | null = null;
  let sampleRate = 44_100;
  let pcmChunks: Float32Array[] = [];
  let capturedSamples = 0;
  /** Safety net: never buffer more samples than the duration cap allows. */
  let maxSamples = 0;
  let tickTimer: ReturnType<typeof setInterval> | null = null;
  let autoStopTimer: ReturnType<typeof setTimeout> | null = null;
  /** Set when the component unmounts so a pending stop never uploads. */
  let discardOnStop = false;

  const clearTimers = (): void => {
    if (tickTimer !== null) {
      clearInterval(tickTimer);
      tickTimer = null;
    }
    if (autoStopTimer !== null) {
      clearTimeout(autoStopTimer);
      autoStopTimer = null;
    }
  };

  const releaseCapture = (): void => {
    try {
      processorNode?.disconnect();
      sourceNode?.disconnect();
      muteNode?.disconnect();
    } catch {
      // Nodes may already be detached; closing the context finishes cleanup.
    }
    processorNode = null;
    sourceNode = null;
    muteNode = null;
    if (audioContext && audioContext.state !== 'closed') {
      void audioContext.close().catch(() => undefined);
    }
    audioContext = null;
    stream?.getTracks().forEach((track) => track.stop());
    stream = null;
  };

  /** Float32 [-1,1] chunks → 44-byte RIFF/WAVE header + 16-bit PCM LE mono. */
  const encodeWav = (chunks: Float32Array[], totalSamples: number, rate: number): Blob => {
    const pcm = new DataView(new ArrayBuffer(44 + totalSamples * 2));
    const writeAscii = (offset: number, text: string): void => {
      for (let i = 0; i < text.length; i++) pcm.setUint8(offset + i, text.charCodeAt(i));
    };

    const byteRate = rate * 2; // mono, 16-bit
    writeAscii(0, 'RIFF');
    pcm.setUint32(4, 36 + totalSamples * 2, true);
    writeAscii(8, 'WAVE');
    writeAscii(12, 'fmt ');
    pcm.setUint32(16, 16, true);        // PCM chunk size
    pcm.setUint16(20, 1, true);         // audio format: PCM
    pcm.setUint16(22, 1, true);         // channels: mono
    pcm.setUint32(24, rate, true);      // sample rate (context native)
    pcm.setUint32(28, byteRate, true);  // byte rate
    pcm.setUint16(32, 2, true);         // block align
    pcm.setUint16(34, 16, true);        // bits per sample
    writeAscii(36, 'data');
    pcm.setUint32(40, totalSamples * 2, true);

    let offset = 44;
    for (const chunk of chunks) {
      for (let i = 0; i < chunk.length; i++) {
        const clamped = Math.max(-1, Math.min(1, chunk[i]));
        pcm.setInt16(offset, clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff, true);
        offset += 2;
      }
    }
    return new Blob([pcm.buffer], { type: 'audio/wav' });
  };

  const transcribe = async (blob: Blob): Promise<void> => {
    isTranscribing.value = true;
    try {
      const formData = new FormData();
      formData.append('audio', blob, 'voice.wav');
      const response = await http.post(ApiFactory.SPEECH.TRANSCRIBE_UPLOAD, formData);
      if (!response.ok) {
        options.onError(await readApiError(response, TRANSCRIBE_FALLBACK));
        return;
      }
      const data = await response.json() as { text?: unknown };
      const text = typeof data.text === 'string' ? data.text.trim() : '';
      if (text) options.onTranscript(text);
      else options.onError('Không nhận diện được nội dung giọng nói.');
    } catch (cause) {
      options.onError(errorMessage(cause, `${TRANSCRIBE_FALLBACK}.`));
    } finally {
      isTranscribing.value = false;
    }
  };

  const start = async (): Promise<void> => {
    if (!isSupported || isRecording.value || isTranscribing.value) return;
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: { echoCancellation: true, noiseSuppression: true }
      });
    } catch {
      options.onError('Không truy cập được micro.');
      return;
    }

    try {
      audioContext = new AudioContext();
      sampleRate = audioContext.sampleRate;
      sourceNode = audioContext.createMediaStreamSource(stream);
      // 4096-frame mono capture; a zero-gain sink keeps the processor firing
      // in browsers that require a connected destination, without audible echo.
      processorNode = audioContext.createScriptProcessor(4096, 1, 1);
      muteNode = audioContext.createGain();
      muteNode.gain.value = 0;
    } catch {
      releaseCapture();
      options.onError('Trình duyệt không hỗ trợ ghi âm.');
      return;
    }

    pcmChunks = [];
    capturedSamples = 0;
    maxSamples = Math.ceil((maxDurationMs / 1000) * sampleRate);
    processorNode.onaudioprocess = (event: AudioProcessingEvent) => {
      if (!isRecording.value || capturedSamples >= maxSamples) return;
      const input = event.inputBuffer.getChannelData(0);
      const room = maxSamples - capturedSamples;
      const take = Math.min(input.length, room);
      // The engine reuses inputBuffer between callbacks — copy before storing.
      pcmChunks.push(new Float32Array(input.subarray(0, take)));
      capturedSamples += take;
    };
    sourceNode.connect(processorNode);
    processorNode.connect(muteNode);
    muteNode.connect(audioContext.destination);

    isRecording.value = true;
    elapsedSeconds.value = 0;
    tickTimer = setInterval(() => {
      elapsedSeconds.value += 1;
    }, 1000);
    autoStopTimer = setTimeout(() => stop(), maxDurationMs);
  };

  const stop = (): void => {
    if (!isRecording.value) return;
    isRecording.value = false;
    const chunks = pcmChunks;
    const totalSamples = capturedSamples;
    const rate = sampleRate;
    pcmChunks = [];
    capturedSamples = 0;
    clearTimers();
    releaseCapture();
    if (discardOnStop || totalSamples === 0) return;
    void transcribe(encodeWav(chunks, totalSamples, rate));
  };

  const toggle = (): void => {
    if (isRecording.value) stop();
    else void start();
  };

  onUnmounted(() => {
    discardOnStop = true;
    stop();
    clearTimers();
    releaseCapture();
  });

  return { isSupported, isRecording, isTranscribing, elapsedSeconds, elapsedLabel, start, stop, toggle };
}
