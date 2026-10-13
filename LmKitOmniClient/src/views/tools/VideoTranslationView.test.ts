import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { filterVideoLanguages, isAllowedVideoSource } from './videoLanguages';

// Không có @vue/test-utils trong repo nên test view ở mức hợp đồng:
// helper dùng bởi view + sự hiện diện của route/nav/source (đọc text, không import side-effect).

const repoRoot = fileURLToPath(new URL('../../..', import.meta.url));
function read(rel: string): string {
  return readFileSync(`${repoRoot}/${rel}`, 'utf8');
}

describe('VideoTranslationView contract', () => {
  it('route /tools/video-translation lazy-load view, bảo vệ auth', () => {
    const router = read('src/router/index.ts');
    expect(router).toContain("path: '/tools/video-translation'");
    expect(router).toContain('VideoTranslationView.vue');
    expect(router).toContain('requiresAuth');
  });

  it('sidebar AI Studio có mục Dịch video', () => {
    const layout = read('src/layout/AppLayout.vue');
    expect(layout).toContain('/tools/video-translation');
    expect(layout).toContain('Dịch video');
  });

  it('view chỉ gắn player sau Preview: không có gán src lúc nhập', () => {
    const view = read('src/views/tools/VideoTranslationView.vue');
    // previewSource chỉ được set trong previewFromUrl/onFileChange, không watch v-model trực tiếp.
    expect(view).toContain('previewFromUrl');
    expect(view).toContain('URL.createObjectURL');
    expect(view).toContain('revokeObjectURL');
    expect(view).not.toContain('innerHTML');
  });

  it('view có caption demo + export disabled, không có download giả', () => {
    const view = read('src/views/tools/VideoTranslationView.vue');
    expect(view).toContain('Phụ đề minh họa — AI chưa kết nối');
    expect(view).toContain('data-testid="export-btn"');
    expect(view).toContain('disabled');
    expect(view).not.toContain('download=');
  });

  it('locale picker dùng chung catalogue (vi/Vietnamese/Tiếng Việt)', () => {
    expect(filterVideoLanguages('tieng viet').some((l) => l.code === 'vi')).toBe(true);
    expect(isAllowedVideoSource('https://example.com/video.mp4')).toBe(true);
    expect(isAllowedVideoSource('javascript:alert(1)')).toBe(false);
  });
});
