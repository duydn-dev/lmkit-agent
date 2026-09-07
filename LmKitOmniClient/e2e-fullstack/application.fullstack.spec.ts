import { expect, test } from '@playwright/test';

const adminEmail = process.env.E2E_ADMIN_EMAIL ?? 'e2e-admin@example.test';
const adminPassword = process.env.E2E_ADMIN_PASSWORD ?? 'E2e-Admin-2026!';
test('real stack supports auth, sessions, documents, user admin and logout', async ({ page }) => {
  const runId = Date.now();
  const createdUserEmail = `browser-member-${runId}@example.test`;
  const documentName = `fullstack-${runId}.txt`;
  const browserErrors: string[] = [];
  page.on('pageerror', (error) => browserErrors.push(`pageerror: ${error.message}`));
  page.on('console', (message) => {
    if (message.type() === 'error') browserErrors.push(`console: ${message.text()}`);
  });

  const loginResponse = await page.goto('/login');
  expect(loginResponse?.status()).toBe(200);
  expect(loginResponse?.headers()['x-content-type-options']).toBe('nosniff');
  // App routes stay unframable...
  expect(loginResponse?.headers()['content-security-policy']).toContain("frame-ancestors 'self'");
  expect(loginResponse?.headers()['permissions-policy']).toContain('microphone=(self)');

  // ...but the embeddable widget document must NOT be forced to 'self' — that
  // blocked every customer embed. Its frame-ancestors comes from the API, per
  // widget key; an unknown key fails closed to 'none' (never 'self').
  const anonymousWidget = await page.request.get('/widget/chat?key=not-a-real-widget-key');
  expect(anonymousWidget.status()).toBe(200);
  expect(anonymousWidget.headers()['content-security-policy']).toContain("frame-ancestors 'none'");
  expect(anonymousWidget.headers()['content-security-policy']).not.toContain("frame-ancestors 'self'");
  expect(anonymousWidget.headers()['x-content-type-options']).toBe('nosniff');

  await page.getByLabel('Email / Tài khoản').fill(adminEmail);
  await page.getByLabel('Mật khẩu').fill(adminPassword);
  await page.getByRole('button', { name: 'Đăng Nhập' }).click();
  await expect(page).toHaveURL(/\/chat$/);
  await expect(page.getByText('Hôm nay tôi có thể giúp gì cho bạn?')).toBeVisible();
  // The login bootstrap probes /auth/me anonymously; its expected 401 is not an application error.
  browserErrors.length = 0;

  const api = page.context().request;
  const meResponse = await api.get('/api/auth/me');
  expect(meResponse.status()).toBe(200);
  const currentUser = await meResponse.json();
  expect(currentUser.email).toBe(adminEmail);
  expect(currentUser.role).toBe('Admin');

  const existingSessions = await (await api.get('/api/chat/sessions')).json();
  for (const existingSession of existingSessions)
    await api.delete(`/api/chat/sessions/${existingSession.id}`);
  const createSessionResponse = await api.post('/api/chat/sessions');
  expect(createSessionResponse.status()).toBe(200);
  const session = await createSessionResponse.json();
  await page.reload();
  await expect(page.getByRole('button', { name: 'Đoạn chat mới', exact: true })).toBeVisible();
  page.once('dialog', (dialog) => dialog.accept());
  await page.getByRole('button', { name: 'Xóa đoạn chat Đoạn chat mới', exact: true }).click();
  await expect.poll(async () => (await api.get(`/api/chat/sessions/${session.id}/messages`)).status()).toBe(404);

  await page.getByRole('link', { name: 'Kho tài liệu (RAG)' }).click();
  await page.getByRole('button', { name: 'Tải tài liệu lên' }).click();
  await page.getByLabel('Chọn tài liệu để tải lên').setInputFiles({
    name: documentName,
    mimeType: 'text/plain',
    buffer: Buffer.from('Tài liệu kiểm tra full-stack E2E.')
  });
  await page.getByRole('dialog').getByRole('button', { name: 'Tải lên', exact: true }).click();
  await expect(page.getByText(documentName)).toBeVisible();
  await page.getByRole('button', { name: `Xóa tài liệu ${documentName}` }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Xóa', exact: true }).click();
  await expect(page.getByRole('heading', { name: documentName, exact: true })).toBeHidden();

  await page.getByRole('link', { name: 'Quản lý User' }).click();
  await page.getByRole('button', { name: 'Thêm người dùng' }).click();
  const userDialog = page.getByRole('dialog', { name: 'Tạo Tài khoản mới' });
  await userDialog.getByLabel('Email').fill(createdUserEmail);
  await userDialog.getByLabel('Mật khẩu').fill('Browser-Member-2026!');
  await userDialog.getByLabel('Họ và Tên').fill('Browser Member');
  await userDialog.getByRole('button', { name: 'Lưu lại' }).click();
  await expect(page.getByRole('table').getByText(createdUserEmail)).toBeVisible();
  page.once('dialog', (dialog) => dialog.accept());
  await page.getByRole('button', { name: `Khóa tài khoản ${createdUserEmail}` }).click();
  const createdUserRow = page.getByRole('row').filter({ hasText: createdUserEmail });
  await expect(createdUserRow.getByText('Đã khóa', { exact: true })).toBeVisible();

  // The settings modal was retired; MCP management now has its own admin page.
  // The user-card gear navigates admins to the hub; the sidebar link opens MCP.
  await page.getByRole('button', { name: /Admin User/ }).click();
  await expect(page).toHaveURL(/\/admin$/);
  await page.getByRole('complementary', { name: 'Thanh bên ứng dụng' })
    .getByRole('link', { name: 'Máy chủ MCP' }).click();
  await expect(page).toHaveURL(/\/admin\/mcp-servers$/);
  await expect(page.getByText('Kết nối MCP Streamable HTTP theo tenant.')).toBeVisible();
  await expect(page.getByRole('checkbox', { name: /Tin cậy khai báo/ })).not.toBeChecked();
  expect((await api.get('/api/mcp-servers')).status()).toBe(200);

  // ── Public widget embed contract ──
  // Allowlist an origin, mint a key, and check that the widget document is framable
  // by exactly that origin — the per-tenant frame-ancestors the API derives from the
  // allowlist, not the app-wide 'self' nginx used to force on every route.
  const embedOrigin = 'https://widget-e2e.example.com';
  const widgetSettings = await api.put('/api/admin/widget/settings', {
    data: {
      isActive: true,
      allowedOrigins: [embedOrigin],
      requestsPerMinute: 30,
      requestsPerDay: 500,
      widgetTitle: 'E2E widget'
    }
  });
  expect(widgetSettings.status()).toBe(204);
  const rotated = await api.post('/api/admin/widget/credentials:rotate');
  expect(rotated.status()).toBe(200);
  const widgetKey: string = (await rotated.json()).rawKey;

  const embeddedWidget = await page.request.get(`/widget/chat?key=${encodeURIComponent(widgetKey)}`);
  expect(embeddedWidget.status()).toBe(200);
  const embeddedCsp = embeddedWidget.headers()['content-security-policy'] ?? '';
  expect(embeddedCsp).toContain(`frame-ancestors ${embedOrigin}`);
  // The allowlisted origin must REPLACE the fail-closed default, not sit beside it.
  expect(embeddedCsp).not.toContain("frame-ancestors 'none'");
  expect(embeddedCsp).not.toContain("frame-ancestors 'self'");
  // The API->nginx handoff header is internal plumbing; it must not reach the browser.
  expect(embeddedWidget.headers()['x-widget-frame-ancestors']).toBeUndefined();

  // Fail-closed, re-checked now that a tenant IS configured with an allowlist. The
  // anonymous check at the top of this test ran before any widget existed, so it
  // could pass for the wrong reason; these prove the KEY is what opens the frame.
  // frame-ancestors is a clickjacking control, so every input gets its own case —
  // including hostile ?key= values, which nginx splices into the internal
  // auth_request's query string and which must never widen the directive.
  const failClosed: Array<[string, string]> = [
    ['no key at all', ''],
    ['empty key', '?key='],
    ['bogus key', '?key=not-a-real-widget-key'],
    ['truncated real key', `?key=${encodeURIComponent(widgetKey.slice(0, -1))}`],
    ['origin appended to a real key', `?key=${encodeURIComponent(`${widgetKey} https://evil.example.com`)}`],
    ['CSP directive smuggling', `?key=${encodeURIComponent('x; frame-ancestors https://evil.example.com')}`],
    ['CRLF header injection', `?key=${encodeURIComponent('x\r\nX-Widget-Frame-Ancestors: https://evil.example.com')}`],
    ['encoded & smuggling a 2nd key', `?key=${encodeURIComponent(widgetKey)}%26key=x`],
    ['over-long key', `?key=${'A'.repeat(300)}`]
  ];
  for (const [label, query] of failClosed) {
    const denied = await page.request.get(`/widget/chat${query}`);
    expect(denied.status(), label).toBe(200);
    const deniedCsp = denied.headers()['content-security-policy'] ?? '';
    expect(deniedCsp, label).toContain("frame-ancestors 'none'");
    expect(deniedCsp, label).not.toContain('evil.example.com');
    expect(deniedCsp, label).not.toContain(embedOrigin);
  }

  // The shipped loader must forward that key into the iframe URL; without it the
  // frame boots the authenticated path, 401s, and redirects the customer's visitors
  // to our login screen.
  const loader = await page.request.get('/widget.js');
  expect(loader.status()).toBe(200);
  const loaderSource = await loader.text();
  expect(loaderSource).toContain("'/widget/chat?key='");
  expect(loaderSource).not.toContain('embed=true');

  expect(browserErrors).toEqual([]);
  await page.getByRole('button', { name: 'Đăng xuất', exact: true }).click();
  await expect(page).toHaveURL(/\/login$/);
  expect((await api.get('/api/auth/me')).status()).toBe(401);
});
