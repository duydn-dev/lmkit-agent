import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * Ô icon cạnh tiêu đề trang (ô gradient 40×40 + glyph PrimeIcons) phải LUÔN vuông.
 *
 * Lý do tồn tại: mỗi view khai báo ô này bằng tay (`w-10 h-10 ...`). Khi thiếu
 * `flex-shrink-0`, flexbox co ô theo chiều ngang lúc cửa sổ hẹp — ở 768px ô chỉ còn
 * ~27×40, ở 420px còn ~20×40, nên nó trông méo/lệch hẳn so với tiêu đề.
 */

const user = {
  id: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
  tenantId: '99999999-8888-7777-6666-555555555555',
  email: 'admin@example.test',
  fullName: 'Admin Tester',
  role: 'Admin',
  tenant: { name: 'CILA Test', agentName: null, logoUrl: null }
};

const ROUTES = [
  '/tools/video-translation',
  '/tools/text',
  '/tools/vision',
  '/research',
  '/projects',
  '/approvals',
  '/agents',
  '/agents/content-creation',
  '/agent-mode',
  '/documents',
  '/schedules',
  '/api-keys',
  '/settings/custom-instructions',
  '/admin',
  '/admin/users',
  '/admin/tenants',
  '/admin/databases',
  '/admin/knowledge',
  '/admin/mcp-servers',
  '/admin/quota',
  '/admin/lora',
  '/admin/audit',
  '/admin/widget'
];

/** Bề rộng hẹp là nơi lỗi co ô từng xuất hiện (768px) và tệ nhất (420px). */
const WIDTHS = [1280, 768, 420];

async function mockApi(page: Page) {
  await page.route(
    (url) => url.pathname.startsWith('/api/'),
    (route: Route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(route.request().url().includes('/api/auth/me') ? user : [])
      })
  );
}

/** Đo ô icon đứng ngay cạnh <h1> của trang. */
function measureTitleTile() {
  const h1 = document.querySelector('h1');
  if (!h1) return null;
  const box = h1.parentElement;
  const row = box?.parentElement;
  if (!box || !row) return null;
  const tile = [...row.children].find(
    (el): el is HTMLElement =>
      el instanceof HTMLElement && el.tagName === 'DIV' && !!el.querySelector('i') && !el.querySelector('h1,h2,h3')
  );
  if (!tile) return null;
  const icon = tile.querySelector('i');
  if (!icon) return null;
  const tr = tile.getBoundingClientRect();
  const ir = icon.getBoundingClientRect();
  return {
    width: tr.width,
    height: tr.height,
    radius: getComputedStyle(tile).borderRadius,
    shrink: getComputedStyle(tile).flexShrink,
    iconOffsetX: ir.left + ir.width / 2 - (tr.left + tr.width / 2),
    iconOffsetY: ir.top + ir.height / 2 - (tr.top + tr.height / 2)
  };
}

for (const width of WIDTHS) {
  test(`page-title icon tiles stay square and centred at ${width}px`, async ({ page }) => {
    test.setTimeout(120_000);
    await mockApi(page);
    await page.setViewportSize({ width, height: 900 });

    const problems: string[] = [];
    let measured = 0;
    for (const path of ROUTES) {
      await page.goto(path, { waitUntil: 'domcontentloaded' });
      for (let attempt = 0; attempt < 10 && (await page.locator('h1').count()) === 0; attempt++) {
        await page.waitForTimeout(200);
      }
      if ((await page.locator('h1').count()) === 0) {
        problems.push(`${path}: không render được <h1>`);
        continue;
      }

      const tile = await page.evaluate(measureTitleTile);
      if (!tile) continue; // trang không có ô icon cạnh h1
      measured++;

      const tag = `${path} (${tile.width}x${tile.height})`;
      if (Math.abs(tile.width - tile.height) > 0.5) problems.push(`${tag}: ô bị bóp méo, không vuông`);
      if (tile.shrink !== '0') problems.push(`${tag}: thiếu flex-shrink-0`);
      if (tile.radius !== '12px') problems.push(`${tag}: bo góc ${tile.radius}, không thống nhất 12px`);
      if (Math.abs(tile.iconOffsetX) > 1) problems.push(`${tag}: glyph lệch ngang ${tile.iconOffsetX.toFixed(2)}px`);
      if (Math.abs(tile.iconOffsetY) > 1) problems.push(`${tag}: glyph lệch dọc ${tile.iconOffsetY.toFixed(2)}px`);
    }

    expect(problems).toEqual([]);
    // Chống test "rỗng": nếu bộ đo hỏng và luôn trả null thì mọi assert ở trên vô nghĩa.
    expect(measured, 'số ô icon đo được quá ít — bộ đo có thể đã hỏng').toBeGreaterThanOrEqual(20);
  });
}
