import { describe, expect, it } from 'vitest';
import { ChatSseParser } from './chatSse';

describe('ChatSseParser', () => {
  it('decodes the newline-terminated web-search marker the orchestrator emits', () => {
    const parser = new ChatSseParser();

    // Build the wire line from literal source characters so the test file itself
    // does not contain an embedded real newline inside the JSON-stringify payload.
    const markerBody = '[WEB_SEARCH]:https://a.example/x|https://b.example/y';
    const wirePayload = markerBody + '\n';
    // JSON.stringify on a string that ends with a real newline produces a quoted
    // value containing a REAL backslash-n escape, so slice off the surrounding
    // JSON quotes and append a real SSE line terminator.
    const wireLine = 'data: ' + JSON.stringify(wirePayload).slice(1, -1) + '\n';

    // Byte-for-byte what AgentOrchestrator now puts on the wire once search_web
    // returned hits: "[WEB_SEARCH]:" + urls.join("|") + "\n". The backend appends a
    // REAL newline (not the two-character escape) as part of its persisted marker
    // protocol, so the decoder must strip line terminators here — otherwise the last
    // URL reaches the reference drawer with the terminator still glued to it.
    const events = parser.push([wireLine, 'data: "Câu trả lời."', ''].join('\n'));

    expect(events).toEqual([
      { type: 'web-search', value: 'https://a.example/x|https://b.example/y' },
      { type: 'content', value: 'Câu trả lời.' }
    ]);
    expect(String(events[0].value).split('|')).toEqual([
      'https://a.example/x',
      'https://b.example/y'
    ]);
  });
});
