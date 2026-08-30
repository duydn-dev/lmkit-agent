import { describe, expect, it } from 'vitest';
import { ChatSseParser } from './chatSse';

describe('ChatSseParser', () => {
  it('preserves an event split across arbitrary network chunks', () => {
    const parser = new ChatSseParser();

    expect(parser.push('data: "Xin ch')).toEqual([]);
    expect(parser.push('ào"\r\n')).toEqual([{ type: 'content', value: 'Xin chào' }]);
  });

  it('classifies control events without exposing agent logs as content', () => {
    const parser = new ChatSseParser();
    const events = parser.push([
      'data: "[THINKING]: đang tìm"',
      'data: "[WEB_SEARCH]:https://example.com|javascript:bad"',
      'data: "[Agent invoked: hidden]"',
      'data: "[HITL_APPROVAL_REQUIRED:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa]"',
      'data: "[DONE]"',
      ''
    ].join('\n'));

    expect(events).toEqual([
      { type: 'thinking', value: 'đang tìm' },
      { type: 'web-search', value: 'https://example.com|javascript:bad' },
      { type: 'agent-log', value: '[Agent invoked: hidden]' },
      { type: 'approval', value: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa' },
      { type: 'done', value: '' }
    ]);
  });

  it('decodes the research-saved marker instead of leaking it as content', () => {
    const parser = new ChatSseParser();
    const events = parser.push([
      'data: "Báo cáo nghiên cứu..."',
      'data: "[RESEARCH_SAVED:bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb]"',
      ''
    ].join('\n'));

    expect(events).toEqual([
      { type: 'content', value: 'Báo cáo nghiên cứu...' },
      { type: 'saved', value: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb' }
    ]);
  });

  it('returns server errors and flushes a final line without newline', () => {
    const parser = new ChatSseParser();

    parser.push('data: "[ERROR]: model unavailable"');

    expect(parser.finish()).toEqual([{ type: 'error', value: 'model unavailable' }]);
    expect(parser.finish()).toEqual([]);
  });

  it('accepts legacy plain data and ignores non-data SSE fields', () => {
    const parser = new ChatSseParser();

    expect(parser.push(': keepalive\nevent: message\ndata: plain text\n')).toEqual([
      { type: 'content', value: 'plain text' }
    ]);
  });
});
