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

/** Bọc mảng items thành shape PagedResult chuẩn của các getlist quản lý. */
function paged<T>(items: T[]) {
  return { items, page: 1, pageSize: 20, totalCount: items.length, totalPages: 1 };
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
      // Getlist chuẩn hóa: PagedResult { items, page, pageSize, totalCount, totalPages }.
      return json(route, paged([{ ...user, isActive: true, createdAt: '2026-08-20T00:00:00Z', failedLoginAttempts: 0, lockoutEnd: null }]));
    }
    // The settings dialog fetches connection suggestions when an admin opens it.
    if (path === '/api/mcp-servers/catalog') return json(route, []);
    if (path === '/api/mcp-servers') return json(route, paged([]));
    // The notification bell polls this from the app shell on every view.
    if (path === '/api/notifications' && method === 'GET') return json(route, []);
    // ChatView refreshes the Canvas count badge whenever a session activates.
    if (path === '/api/canvas' && method === 'GET') return json(route, []);

    // Interpreter-produced files are served here (owner-scoped, cookie-authed). A
    // 1x1 PNG is enough to prove <img src="/api/files/{id}"> renders in chat.
    if (path.startsWith('/api/files/')) {
      return route.fulfill({
        status: 200,
        contentType: 'image/png',
        body: Buffer.from(
          'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M8AAAMBAQDJ/pLvAAAAAElFTkSuQmCC',
          'base64'
        )
      });
    }

    // --- Admin/management + AI-tools screens ---
    // Admin Hub stat card + Approvals inbox both read pending approvals.
    if (path === '/api/taskapproval/pending' && method === 'GET') return json(route, []);
    // Admin database-connections list (empty is enough to render the screen).
    if (path === '/api/database-connections' && method === 'GET') return json(route, paged([]));
    // Dropdown gán tenant trên màn kết nối CSDL.
    if (path === '/api/tenants/options') return json(route, []);
    // Agent mode: past-runs list + a streamed run (run id, thinking, one step, result).
    if (path === '/api/agent-runs' && method === 'GET') return json(route, paged([]));
    // Persona pickers (Automation Agent + Task Scheduler) — danh sách trống là đủ.
    if (path === '/api/agents/custom' && method === 'GET') return json(route, paged([]));
    if (path === '/api/agent-runs' && method === 'POST') {
      return route.fulfill({
        status: 200,
        headers: { 'content-type': 'text/event-stream; charset=utf-8' },
        body:
          'data: ' + JSON.stringify('[AGENT_RUN:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee]') + '\n\n' +
          'data: ' + JSON.stringify('[THINKING]: đang lập kế hoạch') + '\n\n' +
          'data: ' + JSON.stringify('[STEP:{"ordinal":1,"action":"run_python","input":"print(2+2)","observation":"4"}]') + '\n\n' +
          'data: ' + JSON.stringify('Kết quả: 4') + '\n\n' +
          'data: ' + JSON.stringify('[DONE]') + '\n\n'
      });
    }
    // Audit activity log: filter facets + one sample row so the table renders.
    if (path === '/api/audit/facets') {
      return json(route, { actorTypes: ['agent'], actions: ['AI.Tool.Invoke'], entityTypes: ['run_python'] });
    }
    if (path === '/api/audit' && method === 'GET') {
      return json(route, {
        items: [{
          id: 'audit-1',
          actorUserId: user.id,
          actorType: 'agent',
          action: 'AI.Tool.Invoke',
          entityType: 'run_python',
          entityId: null,
          correlationId: null,
          detailsJson: '{"Status":"Success","DurationMs":12.3}',
          createdAtUtc: '2026-08-20T00:00:00Z'
        }],
        total: 1,
        page: 1,
        pageSize: 25
      });
    }

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
  // The login field is labelled "Tên tài khoản" (bootstrap users log in by username).
  await page.getByLabel('Tên tài khoản').fill('wrong@example.test');
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

test('admin can navigate documents, memory, users and the admin hub', async ({ page }) => {
  const browserErrors = await mockAuthenticatedApi(page);
  await page.goto('/documents');
  await expect(page.getByRole('heading', { name: 'Kho Tài Liệu' })).toBeVisible();
  await expect(page.getByText('quy-trinh.pdf')).toBeVisible();
  await expectNoWcagViolations(page);

  await page.getByRole('link', { name: 'Agent Memory' }).click();
  await expect(page.getByRole('heading', { name: 'Bộ nhớ của trợ lý' })).toBeVisible();
  await expect(page.getByText('Trả lời bằng tiếng Việt')).toBeVisible();
  await expectNoWcagViolations(page);

  await page.getByRole('link', { name: 'User Management' }).click();
  await expect(page.getByRole('heading', { name: 'User Management' })).toBeVisible();
  await expect(page.getByRole('table').getByText('admin@example.test')).toBeVisible();
  await expectNoWcagViolations(page);

  // The user card's gear is now an admin shortcut to the management hub
  // (the old MCP settings modal was retired in favour of /admin/mcp-servers).
  await page.getByRole('button', { name: /Admin Tester/ }).click();
  await expect(page).toHaveURL(/\/admin$/);
  await expect(page.getByRole('heading', { name: 'Bảng điều khiển quản trị' })).toBeVisible();
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
  await page.getByRole('link', { name: 'RAG Documents' }).click();

  await expect(page).toHaveURL(/\/documents$/);
  await expect(page.getByRole('navigation', { name: 'Điều hướng di động' })).toBeHidden();
  await expect(page.getByRole('heading', { name: 'Kho Tài Liệu' })).toBeVisible();
  await expectNoWcagViolations(page);
  expect(browserErrors).toEqual([]);
});

test('files a tool produced render inline in the assistant reply', async ({ page }) => {
  const browserErrors = await mockAuthenticatedApi(page);
  // Override the chat stream to emit a [FILE:] marker (as run_python would after
  // saving a chart). A later, more-specific route takes precedence over the
  // catch-all registered by mockAuthenticatedApi.
  await page.route('**/api/chat/stream', (route) =>
    route.fulfill({
      status: 200,
      headers: { 'content-type': 'text/event-stream; charset=utf-8' },
      body:
        'data: ' + JSON.stringify('Đây là biểu đồ bạn yêu cầu:') + '\n\n' +
        'data: ' + JSON.stringify('[FILE:{"id":"chart.png","name":"chart.png","contentType":"image/png","size":123}]') + '\n\n' +
        'data: ' + JSON.stringify('[DONE]') + '\n\n'
    })
  );

  await page.goto('/chat');
  await page.getByRole('textbox', { name: 'Tin nhắn', exact: true }).fill('Vẽ cho tôi một biểu đồ');
  await page.getByRole('button', { name: 'Gửi tin nhắn' }).click();

  const chart = page.getByRole('img', { name: 'chart.png' });
  await expect(chart).toBeVisible();
  await expect(chart).toHaveAttribute('src', /\/api\/files\/chart\.png$/);
  await expectNoWcagViolations(page);
  expect(browserErrors).toEqual([]);
});

test('agent mode streams a run and renders the step timeline and result', async ({ page }) => {
  const browserErrors = await mockAuthenticatedApi(page);
  await page.goto('/agent-mode');
  await expect(page.getByRole('heading', { name: 'Automation Agent' })).toBeVisible();

  await page.getByLabel('Mục tiêu', { exact: true }).fill('Tính 2+2');
  await page.getByRole('button', { name: 'Chạy', exact: true }).click();

  // The streamed [STEP:] marker becomes a timeline card (action chip), and the
  // streamed content becomes the result.
  await expect(page.getByText('run_python').first()).toBeVisible();
  await expect(page.getByText('Kết quả: 4')).toBeVisible();
  await expectNoWcagViolations(page);
  expect(browserErrors).toEqual([]);
});

test('admin sidebar exposes grouped management navigation and opens the hub', async ({ page }) => {
  const browserErrors = await mockAuthenticatedApi(page);
  await page.goto('/chat');

  const sidebar = page.getByRole('complementary', { name: 'Thanh bên ứng dụng' });
  await expect(sidebar.getByText('AI Studio')).toBeVisible();
  await expect(sidebar.getByText('Quản trị')).toBeVisible();
  await expect(sidebar.getByRole('link', { name: 'Audit Log' })).toBeVisible();
  await expect(sidebar.getByRole('link', { name: 'MCP Servers' })).toBeVisible();
  await expect(sidebar.getByRole('link', { name: 'Knowledge Base' })).toBeVisible();

  await sidebar.getByRole('link', { name: 'Dashboard', exact: true }).click();
  await expect(page).toHaveURL(/\/admin$/);
  await expect(page.getByRole('heading', { name: 'Bảng điều khiển quản trị' })).toBeVisible();
  await expectNoWcagViolations(page);
  expect(browserErrors).toEqual([]);
});

test('admin can open the database-connections management screen', async ({ page }) => {
  const browserErrors = await mockAuthenticatedApi(page);
  await page.goto('/admin/databases');
  await expect(page.getByRole('heading', { name: 'Database Connections' })).toBeVisible();
  await expect(page.getByText('Chưa có kết nối cơ sở dữ liệu nào.')).toBeVisible();
  await expectNoWcagViolations(page);
  expect(browserErrors).toEqual([]);
});

// One connection WITH a schema — the shared mock returns an empty list, so this test
// overrides just the two routes it needs (Playwright runs the last matching handler first).
const diagramConnection = {
  id: 'c0ffee00-1111-2222-3333-444444444444',
  tenantId: null,
  tenantName: null,
  name: 'kho-bao-cao',
  provider: 'Sqlite',
  isActive: true,
  allowWrites: false,
  isIndexed: true,
  indexStatus: 'Completed',
  lastIndexError: null,
  lastIndexedAtUtc: '2026-09-01T02:00:00Z',
  createdAtUtc: '2026-09-01T00:00:00Z',
  updatedAtUtc: '2026-09-01T02:00:00Z'
};

const diagramSchema = {
  connectionId: diagramConnection.id,
  name: diagramConnection.name,
  provider: 'Sqlite',
  isActive: true,
  isIndexed: true,
  indexStatus: 'Completed',
  lastIndexedAtUtc: '2026-09-01T02:00:00Z',
  tableCount: 2,
  totalTableCount: 2,
  truncated: false,
  tables: [
    {
      schema: 'main',
      name: 'customers',
      qualifiedName: 'customers',
      columns: [
        { name: 'id', dataType: 'INTEGER', isNullable: false, isPrimaryKey: true, isForeignKey: false },
        { name: 'name', dataType: 'TEXT', isNullable: false, isPrimaryKey: false, isForeignKey: false }
      ],
      foreignKeys: []
    },
    {
      schema: 'main',
      name: 'orders',
      qualifiedName: 'orders',
      columns: [
        { name: 'id', dataType: 'INTEGER', isNullable: false, isPrimaryKey: true, isForeignKey: false },
        { name: 'customer_id', dataType: 'INTEGER', isNullable: false, isPrimaryKey: false, isForeignKey: true }
      ],
      foreignKeys: [
        {
          column: 'customer_id',
          referencedTable: 'customers',
          referencedColumn: 'id',
          raw: 'customer_id → customers.id',
          isResolved: true
        }
      ]
    }
  ],
  relations: [
    {
      fromTable: 'orders',
      fromColumn: 'customer_id',
      toTable: 'customers',
      toColumn: 'id',
      targetIncluded: true
    }
  ]
};

test('admin can open the schema diagram of an indexed database connection', async ({ page }) => {
  const browserErrors = await mockAuthenticatedApi(page);
  await page.route(/\/api\/database-connections(\?.*)?$/, (route) =>
    route.request().method() === 'GET' ? json(route, paged([diagramConnection])) : route.fallback()
  );
  await page.route(/\/api\/database-connections\/[^/]+\/schema$/, (route) => json(route, diagramSchema));

  await page.goto('/admin/databases');
  await page.getByRole('button', { name: 'Xem sơ đồ schema' }).click();

  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('Sơ đồ CSDL — kho-bao-cao')).toBeVisible();
  await expect(dialog.getByText('2 bảng')).toBeVisible();
  await expect(dialog.getByText('1 quan hệ khoá ngoại')).toBeVisible();

  // The point of the feature: Mermaid really drew the entities and the FK edge in the browser.
  const diagram = dialog.locator('[role="img"] svg');
  await expect(diagram).toBeVisible();
  await expect(diagram).toContainText('orders');
  await expect(diagram).toContainText('customers');
  await expect(diagram).toContainText('customer_id → id');
  // The Mermaid source is available for review/export.
  await dialog.getByRole('button', { name: 'Xem mã Mermaid' }).click();
  await expect(dialog.locator('pre')).toContainText('erDiagram');

  await expectNoWcagViolations(page);
  expect(browserErrors).toEqual([]);
});

test('admin can open the audit, MCP and knowledge-base management screens', async ({ page }) => {
  const browserErrors = await mockAuthenticatedApi(page);

  await page.goto('/admin/audit');
  await expect(page.getByRole('heading', { name: 'Nhật ký hoạt động' })).toBeVisible();
  await expectNoWcagViolations(page);

  await page.goto('/admin/mcp-servers');
  await expect(page.getByRole('heading', { name: 'Máy chủ MCP' })).toBeVisible();
  await expect(page.getByText('Chưa có máy chủ MCP nào được kết nối.')).toBeVisible();
  await expectNoWcagViolations(page);

  // Selecting the OAuth client-credentials mode reveals its (accessible) fields.
  await page.getByLabel('Phương thức xác thực máy chủ MCP').click();
  await page.getByRole('option', { name: 'OAuth 2.0 Client Credentials' }).click();
  await expect(page.getByLabel('Client ID')).toBeVisible();
  await expect(page.getByLabel('Token URL')).toBeVisible();
  await expectNoWcagViolations(page);

  await page.goto('/admin/knowledge');
  await expect(page.getByRole('heading', { name: 'Cơ sở tri thức' })).toBeVisible();
  await expectNoWcagViolations(page);

  expect(browserErrors).toEqual([]);
});

test('AI-tools and approvals screens render for an authenticated user', async ({ page }) => {
  const browserErrors = await mockAuthenticatedApi(page);

  await page.goto('/tools/text');
  await expect(page.getByRole('heading', { name: 'Phân tích văn bản' })).toBeVisible();
  await expectNoWcagViolations(page);

  await page.goto('/tools/vision');
  await expect(page.getByRole('heading', { name: 'Thị giác ảnh' })).toBeVisible();
  await expectNoWcagViolations(page);

  await page.goto('/approvals');
  await expect(page.getByRole('heading', { name: 'Phê duyệt tác vụ' })).toBeVisible();
  await expectNoWcagViolations(page);

  expect(browserErrors).toEqual([]);
});
