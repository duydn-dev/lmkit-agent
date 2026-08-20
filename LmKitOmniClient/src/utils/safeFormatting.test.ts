import { describe, expect, it } from 'vitest';
import { formatSafeMessage } from './safeFormatting';

describe('formatSafeMessage', () => {
  it('escapes model-supplied HTML before rendering', () => {
    const formatted = formatSafeMessage('<img src=x onerror="alert(1)">');

    expect(formatted).not.toContain('<img');
    expect(formatted).toContain('&lt;img src=x onerror=&quot;alert(1)&quot;&gt;');
  });

  it('adds only the supported bold and line-break markup', () => {
    expect(formatSafeMessage('**safe**\nnext')).toBe('<strong>safe</strong><br>next');
  });

  it('does not turn escaped attacker markup inside bold text into HTML', () => {
    const formatted = formatSafeMessage('**<script>alert(1)</script>**');

    expect(formatted).toBe('<strong>&lt;script&gt;alert(1)&lt;/script&gt;</strong>');
  });
});
