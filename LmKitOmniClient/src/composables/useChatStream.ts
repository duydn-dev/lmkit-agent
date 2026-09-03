import { onUnmounted, ref } from 'vue';
import type { Ref } from 'vue';
import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import { ChatSseParser, type ChatStreamEvent } from '@/utils/chatSse';

/**
 * Shared chat message shape used by BOTH the full-page chat (ChatView) and the
 * embeddable widget (ChatWidgetView). The optional fields form the superset of
 * both surfaces: the widget never populates `webUrls` / `attachedFiles`, the
 * full page uses all of them.
 */
/**
 * A file a tool produced during the turn (e.g. a chart PNG or CSV from
 * run_python), served on demand from the owner-scoped `/api/files/{id}` endpoint.
 * The wire descriptor is emitted by the backend as a `[FILE:{json}]` marker.
 */
export interface ProducedFile {
  id: string;
  name: string;
  contentType: string;
  size: number;
}

export interface ChatMessage {
  role: 'user' | 'assistant' | 'system';
  content: string;
  isTyping?: boolean;
  webUrls?: string[];
  thinkingSteps?: string[];
  /** Model chain-of-thought (DeepSeek-R1 style), shown collapsed and separate from the answer. */
  reasoning?: string;
  attachedFiles?: string[];
  producedFiles?: ProducedFile[];
  hitlTaskId?: string;
  hitlActionName?: string;
  hitlDetails?: string;
  hitlResolved?: string;
  hitlBusy?: boolean;
  hitlError?: string;
}

/**
 * Parses a `[FILE:{json}]` descriptor, tolerating a malformed payload (returns
 * null). SECURITY: only `id` is ever used to build a same-origin URL, and it is
 * used as a path segment via `encodeURIComponent` at render time; `name` is
 * display/download text only.
 */
export function parseProducedFile(json: string): ProducedFile | null {
  try {
    const parsed = JSON.parse(json) as Partial<ProducedFile>;
    if (typeof parsed.id !== 'string' || !parsed.id) return null;
    return {
      id: parsed.id,
      name: typeof parsed.name === 'string' && parsed.name ? parsed.name : parsed.id,
      contentType: typeof parsed.contentType === 'string' ? parsed.contentType : 'application/octet-stream',
      size: typeof parsed.size === 'number' ? parsed.size : 0
    };
  } catch {
    return null;
  }
}

/**
 * Protocol allowlist for web-search links. Only http/https URLs are ever kept;
 * anything else (javascript:, data:, blob:, file:, an unparseable string, ...)
 * is rejected. SECURITY CRITICAL — this is applied both when ingesting stream
 * events and when displaying the reference drawer, so it must stay identical in
 * both places.
 */
export function isSafeWebUrl(urlStr: string): boolean {
  try {
    const url = new URL(urlStr);
    return url.protocol === 'https:' || url.protocol === 'http:';
  } catch {
    return false;
  }
}

/** Strips the appended "attached file content" block from a user message for display. */
export function getCleanUserContent(content: string): string {
  return content.replace(/\n\n--- Nội dung file đính kèm ---[\s\S]*/g, '').trim();
}

export interface StoredAssistantContent {
  /** Message body with all protocol markers removed. */
  content: string;
  webUrls?: string[];
  thinkingSteps?: string[];
  reasoning?: string;
  producedFiles?: ProducedFile[];
}

/**
 * Splits a PERSISTED assistant message (as returned by the messages / public
 * share endpoints) into displayable content plus the `[THINKING]:` /
 * `[WEB_SEARCH]:` metadata embedded by the backend. `[Agent invoked: ...]`
 * log lines are dropped entirely. Shared by ChatView's history loader and the
 * public ShareView so both strip markers identically.
 */
export function parseStoredAssistantContent(raw: string): StoredAssistantContent {
  let content = (raw || '').replace(/\[Agent invoked:.*?\][\n\r]*/g, '');
  let webUrls: string[] | undefined;
  let thinkingSteps: string[] | undefined;
  let reasoning: string | undefined;
  let producedFiles: ProducedFile[] | undefined;

  // [FILE:{json}] markers persisted with the message: rebuild the produced-file
  // list on reload, then strip the markers from the displayed body. Matches
  // non-greedily up to the first ']' — the backend never emits a ']' inside the
  // descriptor JSON (values are id/name/mime/number), and a stray one only
  // truncates that single marker's parse (guarded by parseProducedFile).
  if (content.includes('[FILE:')) {
    const fileMatches = content.match(/\[FILE:(.+?)\]/g);
    if (fileMatches) {
      const parsed = fileMatches
        .map((match) => parseProducedFile(match.slice('[FILE:'.length, -1)))
        .filter((file): file is ProducedFile => file !== null);
      if (parsed.length > 0) producedFiles = parsed;
      content = content.replace(/\[FILE:.+?\][\n\r]*/g, '').trimStart();
    }
  }

  if (content.includes('[THINKING]:')) {
    const thinkingMatches = content.match(/\[THINKING\]:([^\n\r]+)/g);
    if (thinkingMatches) {
      thinkingSteps = thinkingMatches.map((match) => match.replace('[THINKING]:', '').trim());
      content = content.replace(/\[THINKING\]:[^\n\r]+[\n\r]*/g, '').trimStart();
    }
  }

  // Model reasoning (DeepSeek-R1 style) persisted as [REASONING]: fragments — rejoin
  // them into one block, then strip the markers from the displayed answer.
  if (content.includes('[REASONING]:')) {
    const reasoningMatches = content.match(/\[REASONING\]:([^\n\r]+)/g);
    if (reasoningMatches) {
      reasoning = reasoningMatches.map((match) => match.replace('[REASONING]:', '')).join('\n').trim();
      content = content.replace(/\[REASONING\]:[^\n\r]+[\n\r]*/g, '').trimStart();
    }
  }

  if (content.includes('[WEB_SEARCH]:')) {
    const match = content.match(/\[WEB_SEARCH\]:([^\n\r]+)/);
    if (match) {
      webUrls = match[1].split('|').filter((url) => url);
      content = content.replace(/\[WEB_SEARCH\]:[^\n\r]+[\n\r]*/, '').trimStart();
    }
  }

  return { content, webUrls, thinkingSteps, reasoning, producedFiles };
}

export interface ConsumeStreamOptions {
  /** The streaming `fetch` response whose body is read to completion. */
  response: Response;
  /** The reactive assistant message that stream output is appended to. */
  assistantMsg: ChatMessage;
  /** Called after each mutation so the view can keep the transcript scrolled. */
  scrollToBottom: () => void | Promise<void>;
  /**
   * Handler for `[WEB_SEARCH]` events. URLs are already filtered through
   * `isSafeWebUrl` before being handed over. When omitted (the widget), the
   * web-search event is ignored entirely — matching the original widget which
   * skipped web-search display.
   */
  onWebSearch?: (urls: string[]) => void;
}

/**
 * Owns the SSE reading loop shared by both chat surfaces plus an
 * `AbortController` so an in-flight stream is torn down when the component
 * unmounts or a new send starts.
 *
 * Note on the abort mechanism: `http.post` (owned elsewhere) does not expose a
 * `RequestInit`/`signal` seam, so the controller cannot be handed to the
 * underlying `fetch`. Instead the abort signal is wired to cancel the active
 * response-body reader, which tears down the body stream (and therefore the
 * fetch) and unblocks the pending `reader.read()`. If `http` later forwards a
 * signal, the same `controller.signal` can be passed straight through.
 */
export function useChatStream() {
  let controller: AbortController | null = null;

  /**
   * True while `consumeStream` is actively reading a response body. Views use
   * this to swap their send button for a Stop button; it is narrower than a
   * view-level "isGenerating" flag, which also covers the initial POST.
   */
  const isStreaming = ref(false);

  /**
   * Monotonic id of the latest stream. A superseded stream's `finally` must
   * not clear `isStreaming` for the stream that replaced it.
   */
  let streamSeq = 0;

  /** Aborts the in-flight stream (if any), cancelling its reader. */
  function abort(): void {
    controller?.abort();
    controller = null;
  }

  /**
   * Public "Stop generating" action. Cancelling the reader unblocks the
   * pending `reader.read()` with `done: true`, so `consumeStream` returns
   * normally: partial content already appended to the assistant message is
   * kept and all cleanup (isStreaming, listeners) runs in its `finally`.
   */
  function stop(): void {
    abort();
  }

  // Navigating away / unmounting must never leave a reader running.
  onUnmounted(abort);

  async function consumeStream(options: ConsumeStreamOptions): Promise<void> {
    const { response, assistantMsg, scrollToBottom, onWebSearch } = options;

    if (!response.body) throw new Error('Trình duyệt không hỗ trợ streaming response.');

    // Starting a new stream aborts any previous one still running.
    controller?.abort();
    const localController = new AbortController();
    controller = localController;

    const reader = response.body.getReader();
    const decoder = new TextDecoder('utf-8');

    const onAbort = () => { void reader.cancel().catch(() => {}); };
    localController.signal.addEventListener('abort', onAbort);

    assistantMsg.isTyping = false;

    // Auto-scroll is coalesced to at most one call per animation frame, so a fast
    // token stream triggers a single layout flush per frame instead of one per SSE
    // event. The concrete scroll — and its smooth / reduced-motion behaviour — still
    // lives in the caller-supplied `scrollToBottom`; only its cadence changes here.
    let scrollFrame: number | null = null;
    const scheduleScroll = (): void => {
      if (scrollFrame !== null) return;
      scrollFrame = requestAnimationFrame(() => {
        scrollFrame = null;
        void scrollToBottom();
      });
    };

    const parser = new ChatSseParser();
    let streamFinished = false;
    const streamId = ++streamSeq;
    isStreaming.value = true;
    try {
      while (!streamFinished) {
        const { done, value } = await reader.read();
        const events = done
          ? [...parser.push(decoder.decode()), ...parser.finish()]
          : parser.push(decoder.decode(value, { stream: true }));
        for (const event of events) {
          if (event.type === 'done') {
            streamFinished = true;
            break;
          }
          if (event.type === 'error') {
            throw new Error(event.value);
          }

          if (event.type === 'web-search') {
            if (onWebSearch) {
              const urls = event.value.split('|').filter(isSafeWebUrl);
              onWebSearch(urls);
            }
            continue;
          }
          if (event.type === 'thinking') {
            if (!assistantMsg.thinkingSteps) {
              assistantMsg.thinkingSteps = [];
            }
            assistantMsg.thinkingSteps.push(event.value);
            scheduleScroll();
            continue;
          }
          if (event.type === 'reasoning') {
            // Fragments stream in; the terminator is a real newline (trimmed here) and
            // each fragment becomes its own line in the collapsible reasoning panel.
            assistantMsg.reasoning = (assistantMsg.reasoning ?? '') + event.value.replace(/[\r\n]+$/, '') + '\n';
            scheduleScroll();
            continue;
          }
          if (event.type === 'approval') {
            assistantMsg.hitlTaskId = event.value;
            // Fetch the owner-scoped action details (e.g. the SQL a DB write wants
            // to run) so the approval card shows what is actually being approved.
            try {
              const res = await http.get(ApiFactory.TASK_APPROVAL.PENDING);
              if (res.ok) {
                const pending = (await res.json()) as Array<{ id: string; actionName?: string; details?: string }>;
                const match = pending.find((item) => item.id === event.value);
                if (match) {
                  assistantMsg.hitlActionName = match.actionName;
                  assistantMsg.hitlDetails = match.details;
                }
              }
            } catch {
              // The card still works (approve/reject) without the detail preview.
            }
            scheduleScroll();
            streamFinished = true;
            break;
          }
          if (event.type === 'file') {
            const file = parseProducedFile(event.value);
            if (file) {
              if (!assistantMsg.producedFiles) assistantMsg.producedFiles = [];
              assistantMsg.producedFiles.push(file);
              scheduleScroll();
            }
            continue;
          }
          if (event.type === 'saved') continue;
          // Agent-run-only markers never reach chat (no step sink), but ignore them
          // defensively so they can never leak into a chat bubble as raw text.
          if (event.type === 'step' || event.type === 'run-id') continue;
          if (event.type === 'agent-log') continue;

          assistantMsg.content += (event as Extract<ChatStreamEvent, { type: 'content' }>).value;
          scheduleScroll();
        }
        if (done) break;
      }
      if (streamFinished) await reader.cancel();
      // The per-frame throttle may leave a scroll pending; land the final
      // end-of-stream position exactly, as the un-throttled version did.
      await scrollToBottom();
    } finally {
      if (scrollFrame !== null) cancelAnimationFrame(scrollFrame);
      if (streamId === streamSeq) isStreaming.value = false;
      localController.signal.removeEventListener('abort', onAbort);
      if (controller === localController) controller = null;
    }
  }

  return { consumeStream, abort, stop, isStreaming };
}

export interface HitlActionOptions {
  /** The reactive transcript that system messages are appended to. */
  messages: Ref<ChatMessage[]>;
  /** The composer model; seeded with a follow-up prompt after approval. */
  inputMessage: Ref<string>;
  /** The view's send function, invoked to continue the agent after approval. */
  sendMessage: () => Promise<void>;
  /** Builds the system transcript line pushed after a successful approval. */
  approvedSystemMessage: (result: string) => string;
}

/**
 * Human-in-the-loop approve/reject actions shared by both surfaces. The only
 * per-surface difference is the wording of the post-approval system message,
 * which is supplied via `approvedSystemMessage`. All network calls, state
 * transitions, and error strings are identical to the originals.
 */
export function useHitlActions(options: HitlActionOptions) {
  const { messages, inputMessage, sendMessage, approvedSystemMessage } = options;

  const approveTask = async (msg: ChatMessage) => {
    msg.hitlBusy = true;
    msg.hitlError = undefined;
    try {
      const res = await http.post(`/api/TaskApproval/${msg.hitlTaskId}/approve`);
      if (!res.ok) throw new Error(await readApiError(res, 'Phê duyệt thất bại'));
      const response = await res.json();
      const result = typeof response.result === 'string' ? response.result : '';
      msg.hitlResolved = 'Approved';
      messages.value.push({
        role: 'system',
        content: approvedSystemMessage(result)
      });
      inputMessage.value = `Tôi đã phê duyệt hành động trên. Kết quả thực thi là: ${result}. Vui lòng tiếp tục.`;
      await sendMessage();
    } catch (error) {
      msg.hitlError = errorMessage(error, 'Không thể phê duyệt thao tác.');
    } finally {
      msg.hitlBusy = false;
    }
  };

  const rejectTask = async (msg: ChatMessage) => {
    msg.hitlBusy = true;
    msg.hitlError = undefined;
    try {
      const res = await http.post(`/api/TaskApproval/${msg.hitlTaskId}/reject`, { Comment: 'User rejected' });
      if (!res.ok) throw new Error(await readApiError(res, 'Từ chối thất bại'));
      msg.hitlResolved = 'Rejected';
      messages.value.push({
        role: 'system',
        content: 'Đã từ chối hành động.'
      });
    } catch (error) {
      msg.hitlError = errorMessage(error, 'Không thể từ chối thao tác.');
    } finally {
      msg.hitlBusy = false;
    }
  };

  return { approveTask, rejectTask };
}
