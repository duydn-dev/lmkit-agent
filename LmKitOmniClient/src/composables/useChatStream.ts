import { onUnmounted } from 'vue';
import type { Ref } from 'vue';
import { http } from '@/api/http';
import { errorMessage, readApiError } from '@/api/errors';
import { ChatSseParser, type ChatStreamEvent } from '@/utils/chatSse';

/**
 * Shared chat message shape used by BOTH the full-page chat (ChatView) and the
 * embeddable widget (ChatWidgetView). The optional fields form the superset of
 * both surfaces: the widget never populates `webUrls` / `attachedFiles`, the
 * full page uses all of them.
 */
export interface ChatMessage {
  role: 'user' | 'assistant' | 'system';
  content: string;
  isTyping?: boolean;
  webUrls?: string[];
  thinkingSteps?: string[];
  attachedFiles?: string[];
  hitlTaskId?: string;
  hitlResolved?: string;
  hitlBusy?: boolean;
  hitlError?: string;
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

  /** Aborts the in-flight stream (if any), cancelling its reader. */
  function abort(): void {
    controller?.abort();
    controller = null;
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

    const parser = new ChatSseParser();
    let streamFinished = false;
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
            await scrollToBottom();
            continue;
          }
          if (event.type === 'approval') {
            assistantMsg.hitlTaskId = event.value;
            await scrollToBottom();
            streamFinished = true;
            break;
          }
          if (event.type === 'agent-log') continue;

          assistantMsg.content += (event as Extract<ChatStreamEvent, { type: 'content' }>).value;
          await scrollToBottom();
        }
        if (done) break;
      }
      if (streamFinished) await reader.cancel();
    } finally {
      localController.signal.removeEventListener('abort', onAbort);
      if (controller === localController) controller = null;
    }
  }

  return { consumeStream, abort };
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
