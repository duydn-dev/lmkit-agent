import { describe, expect, it } from 'vitest';
import { VIDEO_LANGUAGES, filterVideoLanguages, isAllowedVideoSource } from './videoLanguages';

describe('videoLanguages catalogue', () => {
  it('co du 184 ISO 639-1 entries', () => {
    expect(VIDEO_LANGUAGES.length).toBe(184);
  });

  it('ma duy nhat, khong rong', () => {
    const codes = VIDEO_LANGUAGES.map((l) => l.code);
    expect(new Set(codes).size).toBe(184);
    for (const lang of VIDEO_LANGUAGES) {
      expect(lang.code).toMatch(/^[a-z]{2}$/);
      expect(lang.nativeName.trim().length).toBeGreaterThan(0);
      expect(lang.englishName.trim().length).toBeGreaterThan(0);
    }
  });

  it('tim duoc bang ma vi', () => {
    expect(filterVideoLanguages('vi').some((l) => l.code === 'vi')).toBe(true);
  });

  it('tim duoc bang ten tieng Anh (case-insensitive)', () => {
    expect(filterVideoLanguages('Vietnamese').some((l) => l.code === 'vi')).toBe(true);
    expect(filterVideoLanguages('vietnamese').some((l) => l.code === 'vi')).toBe(true);
  });

  it('tim duoc bang ten ban dia (khong dau van ra)', () => {
    expect(filterVideoLanguages('Tiếng Việt').some((l) => l.code === 'vi')).toBe(true);
    expect(filterVideoLanguages('tieng viet').some((l) => l.code === 'vi')).toBe(true);
  });

  it('query khong khop tra ve rong, query rong tra toan bo', () => {
    expect(filterVideoLanguages('zzz-khong-ton-tai').length).toBe(0);
    expect(filterVideoLanguages('').length).toBe(184);
  });
});

describe('isAllowedVideoSource', () => {
  it('chap nhan http/https tuyet doi', () => {
    expect(isAllowedVideoSource('https://example.com/video.mp4')).toBe(true);
    expect(isAllowedVideoSource('http://example.com/v.mp4')).toBe(true);
  });

  it('tu choi rong, relative, thieu scheme va scheme la', () => {
    expect(isAllowedVideoSource('')).toBe(false);
    expect(isAllowedVideoSource('/videos/a.mp4')).toBe(false);
    expect(isAllowedVideoSource('example.com/video.mp4')).toBe(false);
    expect(isAllowedVideoSource('javascript:alert(1)')).toBe(false);
    expect(isAllowedVideoSource('data:video/mp4;base64,AAA')).toBe(false);
    expect(isAllowedVideoSource('ftp://example.com/v.mp4')).toBe(false);
  });
});
