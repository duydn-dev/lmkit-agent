import { describe, expect, it } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

const sourceRoot = join(process.cwd(), 'src');

function vueFiles(directory: string): string[] {
  return readdirSync(directory).flatMap((entry) => {
    const path = join(directory, entry);
    return statSync(path).isDirectory()
      ? vueFiles(path)
      : path.endsWith('.vue')
        ? [path]
        : [];
  });
}

function matches(pattern: RegExp): string[] {
  return vueFiles(sourceRoot).flatMap((path) => {
    const source = readFileSync(path, 'utf8');
    return pattern.test(source) ? [relative(process.cwd(), path)] : [];
  });
}

describe('accessibility source guardrails', () => {
  it('does not use non-semantic div or span click targets', () => {
    expect(matches(/<(?:div|span)\b[^>]*\s@click(?:\.|=)/i)).toEqual([]);
  });

  it('does not use placeholder links', () => {
    expect(matches(/href=["']#["']/i)).toEqual([]);
  });

  it('does not suppress keyboard focus indicators', () => {
    expect(matches(/(?:outline-none|focus:outline-none|focus:ring-0)/i)).toEqual([]);
  });

  it('gives icon-only native buttons an accessible name', () => {
    const violations = vueFiles(sourceRoot).flatMap((path) => {
      const source = readFileSync(path, 'utf8');
      const iconOnlyButtons = source.matchAll(/<button\b([^>]*)>\s*<i\b[^>]*><\/i>\s*<\/button>/gis);

      return Array.from(iconOnlyButtons)
        .filter((match) => !/(?:aria-label|aria-labelledby)=/.test(match[1]))
        .map(() => relative(process.cwd(), path));
    });

    expect(violations).toEqual([]);
  });

  it('gives icon-only PrimeVue buttons an accessible name', () => {
    const violations = vueFiles(sourceRoot).flatMap((path) => {
      const source = readFileSync(path, 'utf8');
      const primeButtons = source.matchAll(/<Button\b([^>]*?)\/>/gis);

      return Array.from(primeButtons)
        .filter((match) => /(?:^|\s):?icon=/.test(match[1]))
        .filter((match) => !/(?:^|\s):?label=/.test(match[1]))
        .filter((match) => !/(?:aria-label|aria-labelledby)=/.test(match[1]))
        .map(() => relative(process.cwd(), path));
    });

    expect(violations).toEqual([]);
  });
});
