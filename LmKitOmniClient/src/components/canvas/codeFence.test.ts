import { describe, expect, it } from 'vitest';
import { extractCodeFences, largestCodeFence } from './codeFence';

describe('extractCodeFences', () => {
  it('returns an empty array for content without fences', () => {
    expect(extractCodeFences('Xin chào, không có mã ở đây.')).toEqual([]);
    expect(extractCodeFences('')).toEqual([]);
  });

  it('parses the language token and the block body', () => {
    const blocks = extractCodeFences('Trước\n```python\nprint("hi")\n```\nSau');
    expect(blocks).toEqual([{ language: 'python', content: 'print("hi")' }]);
  });

  it('treats a missing info string as a null language', () => {
    const blocks = extractCodeFences('```\nplain\n```');
    expect(blocks).toEqual([{ language: null, content: 'plain' }]);
  });

  it('keeps only the first token of the fence info string', () => {
    const blocks = extractCodeFences('```ts title=a.ts\nconst x = 1;\n```');
    expect(blocks).toEqual([{ language: 'ts', content: 'const x = 1;' }]);
  });

  it('handles CRLF line endings', () => {
    const blocks = extractCodeFences('```js\r\nlet a = 1;\r\n```');
    expect(blocks).toEqual([{ language: 'js', content: 'let a = 1;' }]);
  });

  it('ignores an unterminated fence', () => {
    expect(extractCodeFences('```js\nlet a = 1;')).toEqual([]);
  });

  it('extracts multiple blocks in order', () => {
    const blocks = extractCodeFences('```js\na\n```\ntext\n```py\nbb\n```');
    expect(blocks.map((block) => block.language)).toEqual(['js', 'py']);
  });
});

describe('largestCodeFence', () => {
  it('returns null when there is no usable block', () => {
    expect(largestCodeFence('không có mã')).toBeNull();
  });

  it('ignores whitespace-only blocks', () => {
    expect(largestCodeFence('```js\n   \n```')).toBeNull();
  });

  it('picks the largest block when there are several', () => {
    const content = '```js\nshort\n```\n```python\nmột khối dài hơn nhiều\n```';
    expect(largestCodeFence(content)).toEqual({
      language: 'python',
      content: 'một khối dài hơn nhiều',
    });
  });
});
