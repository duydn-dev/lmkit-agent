/**
 * Pure helpers for detecting fenced code blocks (```lang ... ```) inside a
 * CLEANED chat message (protocol markers already stripped by
 * `parseStoredAssistantContent` / the stream consumer). Used by ChatView to
 * decide whether to offer "Mở trong Canvas" on an assistant message, and to
 * pick the block that becomes the artifact content. Kept free of Vue/DOM
 * imports so it is trivially unit-testable.
 */

export interface CodeFenceBlock {
  /** First token of the fence info string (e.g. "python"), or null when absent. */
  language: string | null;
  /** Code inside the fence, without the trailing newline before the closing ```. */
  content: string;
}

const FENCE_PATTERN = /```([^\n\r`]*)\r?\n([\s\S]*?)```/g;

/** Extracts every properly terminated fenced block, in document order. */
export function extractCodeFences(content: string): CodeFenceBlock[] {
  const blocks: CodeFenceBlock[] = [];
  if (!content || !content.includes('```')) return blocks;

  for (const match of content.matchAll(FENCE_PATTERN)) {
    const infoToken = match[1].trim().split(/\s+/)[0];
    blocks.push({
      language: infoToken ? infoToken : null,
      content: match[2].replace(/\r?\n$/, ''),
    });
  }
  return blocks;
}

/**
 * Returns the largest non-empty fenced block of the message (v1 rule for
 * create-from-message), or null when the message has no usable block.
 */
export function largestCodeFence(content: string): CodeFenceBlock | null {
  let largest: CodeFenceBlock | null = null;
  for (const block of extractCodeFences(content)) {
    if (!block.content.trim()) continue;
    if (!largest || block.content.length > largest.content.length) largest = block;
  }
  return largest;
}
