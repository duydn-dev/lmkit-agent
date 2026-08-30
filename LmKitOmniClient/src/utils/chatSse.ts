export type ChatStreamEvent =
  | { type: 'content'; value: string }
  | { type: 'thinking'; value: string }
  | { type: 'web-search'; value: string }
  | { type: 'approval'; value: string }
  | { type: 'saved'; value: string }
  | { type: 'error'; value: string }
  | { type: 'agent-log'; value: string }
  | { type: 'done'; value: '' };

/** Incrementally parses the single-line JSON SSE events emitted by ChatController. */
export class ChatSseParser {
  private buffer = '';

  push(chunk: string): ChatStreamEvent[] {
    this.buffer += chunk;
    const events: ChatStreamEvent[] = [];
    let lineEnd: number;
    while ((lineEnd = this.buffer.indexOf('\n')) !== -1) {
      const line = this.buffer.slice(0, lineEnd).replace(/\r$/, '');
      this.buffer = this.buffer.slice(lineEnd + 1);
      const event = parseDataLine(line);
      if (event) events.push(event);
    }
    return events;
  }

  finish(): ChatStreamEvent[] {
    if (!this.buffer) return [];
    const line = this.buffer.replace(/\r$/, '');
    this.buffer = '';
    const event = parseDataLine(line);
    return event ? [event] : [];
  }
}

function parseDataLine(line: string): ChatStreamEvent | null {
  if (!line.startsWith('data:')) return null;
  let raw = line.slice(5);
  if (raw.startsWith(' ')) raw = raw.slice(1);

  let value = raw;
  try {
    const decoded: unknown = JSON.parse(raw);
    if (typeof decoded === 'string') value = decoded;
  } catch {
    // Plain data is accepted for compatibility with older API responses.
  }

  if (value === '[DONE]') return { type: 'done', value: '' };
  if (value.startsWith('[ERROR]:'))
    return { type: 'error', value: value.slice('[ERROR]:'.length).trim() };
  if (value.startsWith('[THINKING]:'))
    return { type: 'thinking', value: value.slice('[THINKING]:'.length).trim() };
  if (value.startsWith('[WEB_SEARCH]:'))
    return { type: 'web-search', value: value.slice('[WEB_SEARCH]:'.length) };
  if (value.startsWith('[HITL_APPROVAL_REQUIRED:') && value.endsWith(']'))
    return { type: 'approval', value: value.slice('[HITL_APPROVAL_REQUIRED:'.length, -1).trim() };
  if (value.startsWith('[RESEARCH_SAVED:') && value.endsWith(']'))
    return { type: 'saved', value: value.slice('[RESEARCH_SAVED:'.length, -1).trim() };
  if (value.startsWith('[Agent invoked:')) return { type: 'agent-log', value };
  return { type: 'content', value };
}
