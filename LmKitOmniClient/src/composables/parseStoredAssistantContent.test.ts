import { describe, expect, it } from 'vitest';
import { cleanThinkingStepText, isSafeWebUrl, parseStoredAssistantContent } from './useChatStream';

/**
 * The history/share side of the `[WEB_SEARCH]` protocol. The orchestrator emits
 * the marker in-band, so it is stored verbatim on the assistant row and has to be
 * split back out on every reload — in ChatView and in the public ShareView, which
 * both go through this one function.
 *
 * The failure this pins is not "the chip is missing": it is the answer being eaten.
 * Every marker stripper on both sides is line-anchored (`[^\n\r]+[\n\r]*`), so a
 * marker that ships without a real terminating newline makes the strip run straight
 * through the prose that follows it.
 */
describe('parseStoredAssistantContent — web references', () => {
  const ANSWER = 'Theo các nguồn đã đọc, câu trả lời là như sau.';

  it('recovers the source URLs and leaves the answer byte-identical', () => {
    const stored =
      '[THINKING]: 🛡️ Kiểm tra bảo mật đầu vào...\n' +
      '[WEB_SEARCH]:https://a.example/x|https://b.example/y\n' +
      ANSWER;

    const parsed = parseStoredAssistantContent(stored);

    expect(parsed.webUrls).toEqual(['https://a.example/x', 'https://b.example/y']);
    expect(parsed.content).toBe(ANSWER);
    expect(parsed.content).not.toContain('[WEB_SEARCH]');
  });

  it('survives the marker arriving straight after a [FILE:] descriptor', () => {
    // Real emission order in AgentOrchestrator: [FILE:] markers (no trailing
    // newline of their own) and then the single [WEB_SEARCH] line.
    const stored =
      '[FILE:{"id":"c.png","name":"chart.png","contentType":"image/png","size":9}]' +
      '[WEB_SEARCH]:https://a.example/x\n' +
      ANSWER;

    const parsed = parseStoredAssistantContent(stored);

    expect(parsed.producedFiles?.map((file) => file.id)).toEqual(['c.png']);
    expect(parsed.webUrls).toEqual(['https://a.example/x']);
    expect(parsed.content).toBe(ANSWER);
  });

  it('is a no-op for a turn that ran no web search', () => {
    const parsed = parseStoredAssistantContent(ANSWER);

    expect(parsed.webUrls).toBeUndefined();
    expect(parsed.content).toBe(ANSWER);
  });

  /**
   * The regression the whole marker suite exists for: a non-verbatim C# literal
   * ending in "\\n" puts backslash + 'n' on the wire instead of a newline, and the
   * line-anchored strip then swallows the answer. Pinned here so the client half of
   * the contract states the same requirement the backend tests do.
   */
  it('would swallow the answer if the marker were not newline-terminated', () => {
    const broken = String.raw`[WEB_SEARCH]:https://a.example/x\n` + ANSWER;

    expect(broken).toContain('\\n');
    expect(broken).not.toContain('\n');
    expect(parseStoredAssistantContent(broken).content).toBe('');
  });

  it('keeps javascript: and data: links out of the reference drawer', () => {
    // ChatView re-filters through isSafeWebUrl before rendering an href; the
    // parser itself is deliberately permissive, so the allowlist is what protects.
    const parsed = parseStoredAssistantContent(
      '[WEB_SEARCH]:https://ok.example/x|javascript:alert(1)|data:text/html,x\n' + ANSWER
    );

    expect(parsed.webUrls?.filter(isSafeWebUrl)).toEqual(['https://ok.example/x']);
  });
});

describe('cleanThinkingStepText — emoji stripping', () => {
  it('strips backend emoji decorations and collapses leftover spaces', () => {
    expect(cleanThinkingStepText('🛡️ Kiểm tra bảo mật đầu vào...')).toBe('Kiểm tra bảo mật đầu vào...');
    expect(cleanThinkingStepText('✅ Đầu vào an toàn')).toBe('Đầu vào an toàn');
    expect(cleanThinkingStepText('✍️ Đang tổng hợp và tạo câu trả lời...')).toBe('Đang tổng hợp và tạo câu trả lời...');
    expect(cleanThinkingStepText('📋 Khởi tạo LM-Kit ReAct agent với công cụ có cấu trúc...')).toBe(
      'Khởi tạo LM-Kit ReAct agent với công cụ có cấu trúc...'
    );
  });

  it('keeps plain Vietnamese text and ellipses intact', () => {
    expect(cleanThinkingStepText('Tìm kiếm ký ức liên quan...')).toBe('Tìm kiếm ký ức liên quan...');
    expect(cleanThinkingStepText('')).toBe('');
  });

  it('parseStoredAssistantContent returns emoji-free thinking steps for stored messages', () => {
    const parsed = parseStoredAssistantContent(
      '[THINKING]: 🧠 Tìm kiếm ký ức liên quan...\n' +
      '[THINKING]: 🧠 Không có ký ức liên quan\n'
    );

    expect(parsed.thinkingSteps).toEqual(['Tìm kiếm ký ức liên quan...', 'Không có ký ức liên quan']);
  });
});
