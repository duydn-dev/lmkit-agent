import { expect, test, type Page, type Route } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const sessionId = '11111111-2222-3333-4444-555555555555';
const user = {
  id: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
  tenantId: '99999999-8888-7777-6666-555555555555',
  email: 'admin@example.test',
  fullName: 'Admin Tester',
  role: 'Admin'
};

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function expectNoWcagViolations(page: Page) {
  // Vue route transitions may begin one frame after the target content mounts.
  await page.waitForTimeout(300);
  await page.evaluate(async () => {
    await Promise.all(document.getAnimations().map((animation) => animation.finished.catch(() => undefined)));
  });
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
    .analyze();
  expect(results.violations.map((violation) => ({
    id: violation.id,
    impact: violation.impact,
    nodes: violation.nodes.map((node) => ({
      target: node.target,
      html: node.html,
      summary: node.failureSummary
    }))
  }))).toEqual([]);
}

async function mockAuthenticatedApi(page: Page) {
  const browserErrors: string[] = [];
  page.on('pageerror', (error) => browserErrors.push(`pageerror: ${error.message}`));
  page.on('console', (message) => {
    if (message.type() === 'error') browserErrors.push(`console: ${message.text()}`);
  });
  await page.route(/^http:\/\/127\.0\.0\.1:4173\/api\//, async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const method = request.method();

    if (path === '/api/auth/me') return json(route, user);
    if (path === '/api/auth/logout') return json(route, { success: true });
    if (path === '/api/chat/sessions' && method === 'GET') return json(route, []);
    if (path === '/api/chat/sessions' && method === 'POST') {
      return json(route, { id: sessionId, title: 'Đoạn chat mới', createdAt: new Date().toISOString() });
    }
    if (path === '/api/chat/stream') {
      return route.fulfill({
        status: 200,
        headers: { 'content-type': 'text/event-stream; charset=utf-8' },
        body: 'data: "Xin chào từ browser E2E"\n\ndata: "[DONE]"\n\n'
      });
    }
    if (path === '/api/document') {
      return json(route, [{
        id: 'doc-1',
        fileName: 'quy-trinh.pdf',
        fileSize: 2048,
        uploadedAt: '2026-08-20T00:00:00Z',
        isVectorized: true,
        vectorizationStatus: 'Completed'
      }]);
    }
    if (path === '/api/memory') {
      return json(route, [{
        id: 'memory-1',
        memoryType: 'Preference',
        memoryKey: 'language',
        memoryValue: 'Trả lời bằng tiếng Việt',
        confidence: 0.97,
        isConfirmed: true,
        updatedAtUtc: '2026-08-20T00:00:00Z'
      }]);
    }
    if (path === '/api/users') {
      return json(route, [{ ...user, isActive: true }]);
    }
    // The settings dialog fetches connection suggestions when an admin opens it.
    if (path === '/api/mcp-servers/catalog') return json(route, []);
    if (path === '/api/mcp-servers') return json(route, []);
    // The notification bell polls this from the app shell on every view.
    if (path === '/api/notifications' && method === 'GET') return json(route, []);
    // ChatView refreshes the Canvas count badge whenever a session activates.
    if (path === '/api/canvas' && method === 'GET') return json(route, []);

    return json(route, { message: `E2E mock chưa khai báo ${method} ${path}` }, 501);
  });
  return browserErrors;
}

test('anonymous user is redirected and sees the API login error', async ({ page }) => {
  await page.route(/^http:\/\/127\.0\.0\.1:4173\/api\//, async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/auth/login') return json(route, { message: 'Thông tin đăng nhập không đúng.' }, 401);
    return json(route, { message: 'Unauthorized' }, 401);
  });

  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await expectNoWcagViolations(page);
  await page.getByLabel('Email / Tài khoản').fill('wrong@example.test');
  await page.getByLabel('Mật khẩu').fill('invalid-password');
  await page.getByRole('button', { name: 'Đăng Nhập' }).click();

  await expect(page.getByRole('alert')).toHaveText('Thông tin đăng nhập không đúng.');
});

test('authenticated user can create a chat and consume the SSE response', async ({ page }) => {
  const browserErrors = await mockAuthenticatedApi(page);
  await page.goto('/chat');

  await page.getByRole('textbox', { name: 'Tin nhắn', exact: true }).fill('Kiểm tra luồng chat');
  await page.getByRole('button', { name: 'Gửi tin nhắn' }).click();

  await expect(page.getByText('Kiểm tra luồng chat', { exact: true })).toBeVisible();
  await expect(page.getByText('Xin chào từ browser E2E', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Sao chép câu trả lời' })).toBeVisible();
  await expectNoWcagViolations(page);
  expect(browserErrors).toEqual([]);
});

test('admin can navigate documents, memory, users and MCP settings', async ({ page }) => {
  const browserErrors = await mockAuthenticatedApi(page);
  await page.goto('/documents');
  await expect(page.getByRole('heading', { name: 'Kho Tài Liệu' })).toBeVisible();
  await expect(page.getByText('quy-trinh.pdf')).toBeVisible();
  await expectNoWcagViolations(page);

  await page.getByRole('link', { name: 'Bộ nhớ trợ lý' }).click();
  await expect(page.getByRole('heading', { name: 'Bộ nhớ của trợ lý' })).toBeVisible();
  await expect(page.getByText('Trả lời bằng tiếng Việt')).toBeVisible();
  await expectNoWcagViolations(page);

  await page.getByRole('link', { name: 'Quản lý User' }).click();
  await expect(page.getByRole('heading', { name: 'Quản lý Người dùng' })).toBeVisible();
  await expect(page.getByRole('table').getByText('admin@example.test')).toBeVisible();
  await expectNoWcagViolations(page);

  await page.getByRole('button', { name: /Admin Tester/ }).click();
  await expect(page.getByRole('dialog', { name: 'Cấu hình hệ thống' })).toBeVisible();
  await expect(page.getByText('Chưa có máy chủ MCP nào được kết nối.')).toBeVisible();
  await expectNoWcagViolations(page);
  expect(browserErrors).toEqual([]);
});

test('mobile navigation exposes the primary routes and closes after navigation', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const browserErrors = await mockAuthenticatedApi(page);
  await page.goto('/chat');

  const menuButton = page.getByRole('button', { name: 'Mở menu điều hướng' });
  await menuButton.click();
  await expect(page.getByRole('navigation', { name: 'Điều hướng di động' })).toBeVisible();
  await page.getByRole('link', { name: 'Kho tài liệu' }).click();

  await expect(page).toHaveURL(/\/documents$/);
  await expect(page.getByRole('navigation', { name: 'Điều hướng di động' })).toBeHidden();
  await expect(page.getByRole('heading', { name: 'Kho Tài Liệu' })).toBeVisible();
  await expectNoWcagViolations(page);
  expect(browserErrors).toEqual([]);
});
