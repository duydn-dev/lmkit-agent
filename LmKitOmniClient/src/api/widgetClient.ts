/**
 * Public-widget key/token client. Runs inside the SAME-ORIGIN iframe the embed
 * snippet mounts, so plain fetches hit the API without CORS. The embedded
 * page's origin is computed locally (never accepted from the parent) and sent
 * in X-Widget-Origin; a parent page cannot forge it because the values are
 * derived inside the child from window.location.ancestorOrigins /
 * document.referrer.
 */
import { API_BASE_URL } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';

export interface WidgetBranding {
  widgetTitle?: string | null;
  welcomeMessage?: string | null;
  brandColor?: string | null;
  position?: string | null;
}

export interface WidgetToken {
  accessToken: string;
  expiresInMinutes: number;
  widget: WidgetBranding;
}

/** Best-effort embedded-page origin, computed INSIDE the iframe. */
export function detectHostOrigin(): string | null {
  try {
    if (window.location.ancestorOrigins?.length) {
      return window.location.ancestorOrigins[0].toLowerCase();
    }
  } catch {
    /* some browsers gate ancestorOrigins — fall through */
  }
  try {
    if (document.referrer) return new URL(document.referrer).origin.toLowerCase();
  } catch {
    /* malformed referrer */
  }
  // Top-level/developer access: the app's own origin.
  return window.location.origin.toLowerCase();
}

let cachedToken: WidgetToken | null = null;
let exchangePromise: Promise<WidgetToken> | null = null;

/** Clears the cached token (401 handling / logout-of-widget). */
export function resetWidgetToken(): void {
  cachedToken = null;
  exchangePromise = null;
}

async function exchangeKey(rawKey: string): Promise<WidgetToken> {
  const origin = detectHostOrigin();
  if (!origin) throw new Error('Không xác định được origin của trang nhúng.');

  const response = await fetch(`${API_BASE_URL}${ApiFactory.WIDGET.AUTH}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Widget-Key': rawKey, 'X-Widget-Origin': origin }
  });

  if (response.status === 401) throw new Error('Khóa widget không hợp lệ hoặc widget chưa được bật.');
  if (response.status === 403) throw new Error('Trang web này chưa được phép nhúng widget.');
  if (!response.ok) throw new Error(`Trao đổi khóa widget thất bại (${response.status}).`);

  const body = await response.json();
  if (!body?.accessToken) throw new Error('Phản hồi trao đổi khóa không hợp lệ.');
  return body as WidgetToken;
}

/**
 * Returns a valid widget token, exchanging (or re-exchanging after expiry)
 * as needed. Concurrent callers share one exchange.
 */
export async function ensureWidgetToken(rawKey: string): Promise<WidgetToken> {
  if (cachedToken) return cachedToken;
  if (!exchangePromise) {
    exchangePromise = exchangeKey(rawKey)
      .then((token) => {
        cachedToken = token;
        return token;
      })
      .finally(() => {
        exchangePromise = null;
      });
  }
  return exchangePromise;
}

/** Forces a re-exchange (used after a 401 mid-chat, e.g. rotation or expiry). */
export async function refreshWidgetToken(rawKey: string): Promise<WidgetToken> {
  resetWidgetToken();
  return ensureWidgetToken(rawKey);
}

export interface WidgetSendResult {
  answer: string;
}

/**
 * Sends one widget chat turn over SSE and resolves with the guardrailed
 * answer. The backend withholds the FULL answer until the output guardrail
 * passes, so the stream carries a single JSON event.
 */
export async function sendWidgetChat(
  rawKey: string,
  message: string,
  history: { role: string; content: string }[]
): Promise<WidgetSendResult> {
  const send = async (token: WidgetToken): Promise<Response> =>
    fetch(`${API_BASE_URL}${ApiFactory.WIDGET.CHAT}`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Widget-Token': token.accessToken,
        'X-Widget-Origin': detectHostOrigin() ?? ''
      },
      body: JSON.stringify({ message, history })
    });

  let response = await send(await ensureWidgetToken(rawKey));

  // Token expired or rotated: re-exchange once, then retry the send.
  if (response.status === 401) {
    response = await send(await refreshWidgetToken(rawKey));
  }

  if (response.status === 429) throw new Error('Widget đã vượt giới hạn lượt trò chuyện. Vui lòng thử lại sau.');
  if (response.status === 403) throw new Error('Trang web này chưa được phép nhúng widget.');
  if (!response.ok) throw new Error(`Không thể gửi tin nhắn (${response.status}).`);

  const reader = response.body?.getReader();
  if (!reader) throw new Error('Luồng phản hồi không khả dụng.');

  const decoder = new TextDecoder();
  let buffer = '';
  let answer = '';
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });

    const events = buffer.split('\n\n');
    buffer = events.pop() ?? '';
    for (const evt of events) {
      const dataLine = evt.split('\n').find((line) => line.startsWith('data: '));
      if (!dataLine) continue;
      try {
        const payload = JSON.parse(dataLine.slice(6));
        if (typeof payload.answer === 'string') answer = payload.answer;
      } catch {
        /* ignore malformed event */
      }
    }
  }

  return { answer };
}
