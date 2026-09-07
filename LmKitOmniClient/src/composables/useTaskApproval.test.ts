import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  approvalContinuationPrompt,
  approvalFailureMessage,
  approvalIdFromMarkerText,
  fetchPendingApproval,
  findApprovalChatSession,
  isApprovalId,
  isSettledStatus,
  streamApprovedContinuation,
  submitApprovalDecision
} from './useTaskApproval';

// --- Test helpers -----------------------------------------------------------

const APPROVAL_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const SESSION_ID = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

const text = (body: string, status: number): Response =>
  new Response(body, { status, headers: { 'content-type': 'text/plain' } });

/** Builds an SSE body in the exact shape ChatController writes. */
const sse = (...values: string[]): Response =>
  new Response(values.map((value) => `data: ${JSON.stringify(value)}\n\n`).join(''), { status: 200 });

interface FetchCall {
  url: string;
  init?: RequestInit;
}

function installFetch(handler: (url: string, init?: RequestInit) => Response | Promise<Response>) {
  const calls: FetchCall[] = [];
  const fetchMock = vi.fn((url: string, init?: RequestInit) => {
    calls.push({ url, init });
    return Promise.resolve(handler(url, init));
  });
  vi.stubGlobal('fetch', fetchMock);
  return { calls, fetchMock };
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

// --- Failure wording --------------------------------------------------------

describe('approvalFailureMessage', () => {
  // The whole point of the map: four situations a user must be able to tell apart.
  const distinguished = [404, 409, 410, 500];

  it('gives 404 / 409 / 410 / 500 four different explanations for each decision', () => {
    for (const decision of ['approve', 'reject'] as const) {
      const messages = distinguished.map((status) => approvalFailureMessage(status, decision));
      expect(new Set(messages).size).toBe(distinguished.length);
      for (const message of messages) expect(message.trim().length).toBeGreaterThan(20);
    }
  });

  it('says an expired approval executed nothing', () => {
    expect(approvalFailureMessage(410, 'approve')).toContain('hết hạn');
    expect(approvalFailureMessage(410, 'approve')).toContain('KHÔNG được thực thi');
  });

  it('says a 409 means somebody else decided first', () => {
    expect(approvalFailureMessage(409, 'approve')).toContain('quyết định trước');
  });

  it('says a 500 approve executed but failed, not that approval was refused', () => {
    const message = approvalFailureMessage(500, 'approve');
    expect(message).toContain('phê duyệt');
    expect(message).toContain('thất bại');
  });

  it('never falls back to a generic "có lỗi xảy ra"', () => {
    for (const status of [0, 400, 401, 403, 404, 409, 410, 429, 500, 502, 503, 504, 418]) {
      for (const decision of ['approve', 'reject'] as const) {
        expect(approvalFailureMessage(status, decision)).not.toContain('có lỗi xảy ra');
      }
    }
  });

  it('includes the status code for an unknown status', () => {
    expect(approvalFailureMessage(418, 'approve')).toContain('418');
    expect(approvalFailureMessage(418, 'reject')).toContain('418');
  });
});

describe('isSettledStatus', () => {
  it('treats 404 / 409 / 410 as unanswerable for both decisions', () => {
    for (const status of [404, 409, 410]) {
      expect(isSettledStatus(status, 'approve')).toBe(true);
      expect(isSettledStatus(status, 'reject')).toBe(true);
    }
  });

  it('settles a failed approve but not a failed reject', () => {
    // The approve handler has already flipped the row to Failed; nothing on the
    // server is known to have changed after a failed reject.
    expect(isSettledStatus(500, 'approve')).toBe(true);
    expect(isSettledStatus(500, 'reject')).toBe(false);
  });

  it('leaves a transport failure retryable', () => {
    expect(isSettledStatus(0, 'approve')).toBe(false);
  });
});

describe('isApprovalId', () => {
  it('accepts a canonical GUID in either case', () => {
    expect(isApprovalId(APPROVAL_ID)).toBe(true);
    expect(isApprovalId(APPROVAL_ID.toUpperCase())).toBe(true);
  });

  it('rejects anything that is not one', () => {
    for (const value of ['', '   ', 'not-a-guid', `${APPROVAL_ID}x`, null, undefined]) {
      expect(isApprovalId(value as string)).toBe(false);
    }
  });
});

describe('approvalIdFromMarkerText', () => {
  it('recovers the id from a step observation the API persisted', () => {
    expect(approvalIdFromMarkerText(`[HITL_APPROVAL_REQUIRED:${APPROVAL_ID}]`)).toBe(APPROVAL_ID);
    expect(approvalIdFromMarkerText(`nội dung\n[HITL_APPROVAL_REQUIRED:${APPROVAL_ID}]\n`)).toBe(APPROVAL_ID);
  });

  it('returns empty for anything that is not a marker with a real id', () => {
    for (const value of [
      '',
      'Đã ghi 3 dòng.',
      '[HITL_APPROVAL_REQUIRED:]',
      '[HITL_APPROVAL_REQUIRED:not-a-guid]',
      null,
      undefined
    ]) {
      expect(approvalIdFromMarkerText(value as string)).toBe('');
    }
  });
});

// --- The decision call ------------------------------------------------------

describe('submitApprovalDecision', () => {
  it('returns the tool output of a successful approve', async () => {
    const { calls } = installFetch(() => json({ success: true, result: '42 hàng đã cập nhật' }));

    const outcome = await submitApprovalDecision(APPROVAL_ID, 'approve');

    expect(outcome).toMatchObject({ ok: true, status: 200, result: '42 hàng đã cập nhật', error: '' });
    expect(calls[0].url).toBe(`/api/taskapproval/${APPROVAL_ID}/approve`);
    expect(calls[0].init?.method).toBe('POST');
  });

  it('sends the capitalized Comment field on a reject', async () => {
    const { calls } = installFetch(() => json({ success: true, message: 'Task rejected.' }));

    const outcome = await submitApprovalDecision(APPROVAL_ID, 'reject');

    expect(outcome.ok).toBe(true);
    expect(calls[0].url).toBe(`/api/taskapproval/${APPROVAL_ID}/reject`);
    expect(JSON.parse(String(calls[0].init?.body))).toEqual({ Comment: 'User rejected' });
  });

  it.each([
    [404, 'Không tìm thấy'],
    [409, 'quyết định trước'],
    [410, 'hết hạn'],
  ])('translates %i instead of echoing the API English', async (status, fragment) => {
    installFetch(() => text('Task is no longer pending.', status));

    const outcome = await submitApprovalDecision(APPROVAL_ID, 'approve');

    expect(outcome.ok).toBe(false);
    expect(outcome.status).toBe(status);
    expect(outcome.settled).toBe(true);
    expect(outcome.error).toContain(fragment);
    expect(outcome.error).not.toContain('pending');
  });

  it('reports a 500 as "approved but execution failed"', async () => {
    installFetch(() => json({ Success: false, Error: 'Approved task execution failed.' }, 500));

    const outcome = await submitApprovalDecision(APPROVAL_ID, 'approve');

    expect(outcome).toMatchObject({ ok: false, status: 500, settled: true });
    expect(outcome.error).toContain('thực thi');
    expect(outcome.error).not.toContain('Approved task');
  });

  it('honours a success:false body on a 200', async () => {
    installFetch(() => json({ success: false, result: 'Công cụ trả về lỗi.' }));

    const outcome = await submitApprovalDecision(APPROVAL_ID, 'approve');

    expect(outcome.ok).toBe(false);
    expect(outcome.error).toBe('Công cụ trả về lỗi.');
  });

  it('maps a transport failure to status 0 rather than throwing', async () => {
    installFetch(() => Promise.reject(new Error('Failed to fetch')));

    const outcome = await submitApprovalDecision(APPROVAL_ID, 'approve');

    expect(outcome).toMatchObject({ ok: false, status: 0, settled: false });
    expect(outcome.error).toBeTruthy();
  });

  it('refuses a malformed id without touching the network', async () => {
    const { fetchMock } = installFetch(() => json({ success: true }));

    const outcome = await submitApprovalDecision('not-a-guid', 'approve');

    expect(outcome.ok).toBe(false);
    expect(outcome.status).toBe(400);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('falls back to the server body for an unrecognised status', async () => {
    installFetch(() => json({ message: 'Dịch vụ đang bảo trì.' }, 418));

    const outcome = await submitApprovalDecision(APPROVAL_ID, 'approve');

    expect(outcome.error).toBe('Dịch vụ đang bảo trì.');
  });
});

// --- Finding the conversation an approval came from -------------------------

describe('findApprovalChatSession', () => {
  it('searches session content for the approval id', async () => {
    const { calls } = installFetch(() => json([{ id: SESSION_ID, title: 'Báo cáo doanh thu' }]));

    const session = await findApprovalChatSession(APPROVAL_ID);

    expect(session).toEqual({ id: SESSION_ID, title: 'Báo cáo doanh thu' });
    expect(calls[0].url).toBe(`/api/chat/sessions/search?q=${APPROVAL_ID}`);
  });

  it('never searches with a blank term, which would return every session', async () => {
    // SECURITY: an empty q lists all sessions, and the caller posts an approval
    // result into whatever comes back first.
    const { fetchMock } = installFetch(() => json([{ id: SESSION_ID, title: 'Bất kỳ' }]));

    for (const value of ['', '   ', 'not-a-guid']) {
      expect(await findApprovalChatSession(value)).toBeNull();
    }
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('returns null when nothing matches (agent-run or temporary chat)', async () => {
    installFetch(() => json([]));
    expect(await findApprovalChatSession(APPROVAL_ID)).toBeNull();
  });

  it('returns null instead of throwing when the search fails', async () => {
    installFetch(() => Promise.reject(new Error('offline')));
    expect(await findApprovalChatSession(APPROVAL_ID)).toBeNull();
  });
});

describe('fetchPendingApproval', () => {
  it('picks the matching row out of the pending list', async () => {
    installFetch(() => json([
      { id: 'cccccccc-cccc-cccc-cccc-cccccccccccc', actionName: 'other', details: 'x', expiresAtUtc: '2026-01-01T00:00:00Z' },
      { id: APPROVAL_ID, actionName: 'db_write', details: 'DELETE FROM users', expiresAtUtc: '2026-02-02T00:00:00Z' }
    ]));

    expect(await fetchPendingApproval(APPROVAL_ID)).toEqual({
      id: APPROVAL_ID,
      actionName: 'db_write',
      details: 'DELETE FROM users',
      expiresAtUtc: '2026-02-02T00:00:00Z'
    });
  });

  it('returns null when the approval is no longer pending', async () => {
    installFetch(() => json([]));
    expect(await fetchPendingApproval(APPROVAL_ID)).toBeNull();
  });
});

// --- Writing the result back into the conversation --------------------------

describe('approvalContinuationPrompt', () => {
  it('carries the tool result and asks the agent to continue', () => {
    const prompt = approvalContinuationPrompt('42 hàng');
    expect(prompt).toContain('phê duyệt');
    expect(prompt).toContain('42 hàng');
    expect(prompt).toContain('tiếp tục');
  });
});

describe('streamApprovedContinuation', () => {
  it('posts the continuation as the next user turn and collects the answer', async () => {
    const { calls } = installFetch(() => sse('Đã ', 'xong.', '[DONE]'));

    const outcome = await streamApprovedContinuation(SESSION_ID, '42 hàng');

    expect(outcome).toMatchObject({ ok: true, answer: 'Đã xong.', error: '', pendingApprovalId: '' });
    expect(calls[0].url).toBe('/api/chat/stream');
    const body = JSON.parse(String(calls[0].init?.body));
    expect(body.SessionId).toBe(SESSION_ID);
    expect(body.Message).toBe(approvalContinuationPrompt('42 hàng'));
    expect(body.enableWebSearch).toBe(false);
  });

  it('ignores protocol markers that are not content', async () => {
    installFetch(() => sse('[THINKING]: đang đọc', '[WEB_SEARCH]:https://a|https://b', 'Kết quả.', '[DONE]'));

    const outcome = await streamApprovedContinuation(SESSION_ID, 'x');

    expect(outcome.ok).toBe(true);
    expect(outcome.answer).toBe('Kết quả.');
  });

  it('surfaces a second approval gate the continuation ran into', async () => {
    installFetch(() => sse('Tôi cần ', `[HITL_APPROVAL_REQUIRED:${APPROVAL_ID}]`, '[DONE]'));

    const outcome = await streamApprovedContinuation(SESSION_ID, 'x');

    expect(outcome.ok).toBe(true);
    expect(outcome.pendingApprovalId).toBe(APPROVAL_ID);
  });

  it('reports a mid-stream [ERROR] as a failure', async () => {
    installFetch(() => sse('một phần', '[ERROR]: Mô hình không phản hồi.'));

    const outcome = await streamApprovedContinuation(SESSION_ID, 'x');

    expect(outcome.ok).toBe(false);
    expect(outcome.error).toBe('Mô hình không phản hồi.');
  });

  it('reports a rejected POST without pretending anything was written', async () => {
    installFetch(() => json({ message: 'Bạn gửi quá nhanh.' }, 429));

    const outcome = await streamApprovedContinuation(SESSION_ID, 'x');

    expect(outcome.ok).toBe(false);
    expect(outcome.error).toBe('Bạn gửi quá nhanh.');
  });

  it('reports a transport failure instead of throwing', async () => {
    installFetch(() => Promise.reject(new Error('offline')));

    const outcome = await streamApprovedContinuation(SESSION_ID, 'x');

    expect(outcome.ok).toBe(false);
    expect(outcome.error).toBeTruthy();
  });
});
