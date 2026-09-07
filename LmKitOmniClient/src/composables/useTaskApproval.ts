import { http } from '@/api/http';
import { ApiFactory } from '@/api/api.factory';
import { errorMessage, readApiError } from '@/api/errors';
import { ChatSseParser } from '@/utils/chatSse';

/**
 * The one place a human-in-the-loop task approval is resolved from. Every surface
 * that can approve or reject — the chat card (`useHitlActions`), the Approvals
 * page and the agent-run page — goes through {@link submitApprovalDecision} so the
 * network call, the outcome classification and the wording of every failure are
 * identical wherever the user happens to be standing.
 *
 * It also owns the two pieces the Approvals page needs to put an approved result
 * back into the conversation it came from ({@link findApprovalChatSession} and
 * {@link streamApprovedContinuation}); see the note on that pair below.
 */

export type ApprovalDecision = 'approve' | 'reject';

/**
 * Canonical GUID shape, as `System.Text.Json` serializes `TaskApproval.Id`.
 *
 * SECURITY / CORRECTNESS: {@link findApprovalChatSession} feeds the id straight into
 * the session content search, and that endpoint treats an EMPTY term as "list every
 * session". A blank or malformed id must therefore never reach it — it would return
 * an unrelated session that the caller would then post an approval result into.
 */
const APPROVAL_ID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function isApprovalId(value: string | null | undefined): boolean {
  return typeof value === 'string' && APPROVAL_ID_PATTERN.test(value.trim());
}

/**
 * Plain-Vietnamese explanation of a failed decision, chosen by HTTP status.
 *
 * The server's own body is deliberately NOT used for the statuses listed here: the
 * API answers with English sentences ("Task is no longer pending.") that would land
 * verbatim in a Vietnamese UI, and — more importantly — 404 / 409 / 410 / 500 mean
 * four genuinely different things to the person who just clicked the button:
 *
 *  - 404 nobody can find the task any more,
 *  - 409 somebody else decided first,
 *  - 410 the window closed with nobody deciding, so nothing ran,
 *  - 500 the decision was recorded but the action itself blew up.
 *
 * Collapsing those into one "có lỗi xảy ra" is what this function exists to prevent.
 */
export function approvalFailureMessage(status: number, decision: ApprovalDecision): string {
  const approving = decision === 'approve';

  switch (status) {
    case 0:
      return 'Không kết nối được tới máy chủ. Kiểm tra kết nối mạng rồi thử lại.';
    case 400:
      return approving ? 'Yêu cầu phê duyệt không hợp lệ.' : 'Yêu cầu từ chối không hợp lệ.';
    case 401:
      return 'Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại rồi thử lại.';
    case 403:
      return approving
        ? 'Bạn không có quyền phê duyệt tác vụ này.'
        : 'Bạn không có quyền từ chối tác vụ này.';
    case 404:
      return approving
        ? 'Không tìm thấy tác vụ này — có thể tác vụ đã được xử lý ở nơi khác hoặc đã bị xóa. Không có hành động nào được thực thi.'
        : 'Không còn tác vụ nào đang chờ để từ chối — tác vụ đã được phê duyệt, đã bị từ chối, hoặc đã hết hạn.';
    case 409:
      return approving
        ? 'Tác vụ không còn ở trạng thái chờ: người khác (hoặc một tab khác) đã quyết định trước bạn. Không có hành động nào được thực thi thêm.'
        : 'Tác vụ không còn ở trạng thái chờ: người khác đã quyết định trước bạn.';
    case 410:
      return approving
        ? 'Yêu cầu phê duyệt đã hết hạn trước khi có người trả lời, nên hành động KHÔNG được thực thi. Hãy yêu cầu agent thực hiện lại.'
        : 'Yêu cầu phê duyệt đã hết hạn nên không cần từ chối nữa. Hành động KHÔNG được thực thi.';
    case 429:
      return 'Bạn thao tác quá nhanh. Vui lòng chờ một chút rồi thử lại.';
    case 500:
      return approving
        ? 'Tác vụ đã được phê duyệt nhưng thực thi thất bại. Hành động không hoàn tất — vui lòng kiểm tra nhật ký máy chủ.'
        : 'Không ghi nhận được việc từ chối do lỗi máy chủ. Vui lòng thử lại.';
    case 502:
    case 503:
    case 504:
      return 'Máy chủ đang không phản hồi. Vui lòng thử lại sau ít phút.';
    default:
      return approving
        ? `Phê duyệt không thành công (mã lỗi ${status}).`
        : `Từ chối không thành công (mã lỗi ${status}).`;
  }
}

/**
 * True when the approval can never be answered again, so the surface should retire
 * its buttons instead of inviting a retry that is guaranteed to fail.
 *
 * 404/409/410 are terminal for both decisions. A failed approve (500) is terminal
 * too — the handler has already flipped the row to `Failed`, so a second attempt
 * only earns a 409. A failed reject is NOT terminal: nothing on the server is known
 * to have changed, so the user may legitimately try again.
 */
export function isSettledStatus(status: number, decision: ApprovalDecision): boolean {
  if (status === 404 || status === 409 || status === 410) return true;
  return decision === 'approve' && status === 500;
}

export interface ApprovalDecisionResult {
  /** The decision was recorded (and, for an approve, the tool ran). */
  ok: boolean;
  /** HTTP status, or 0 when the request never reached the server. */
  status: number;
  /** Tool output. Only ever populated by a successful approve. */
  result: string;
  /** Vietnamese explanation; empty when `ok`. */
  error: string;
  /** The approval is now unanswerable — stop offering the buttons. */
  settled: boolean;
}

/**
 * POSTs one approve/reject and classifies the answer. Never throws: a transport
 * failure comes back as `status: 0` with the same shape as an HTTP failure, so
 * callers have exactly one branch to write.
 */
export async function submitApprovalDecision(
  taskId: string,
  decision: ApprovalDecision
): Promise<ApprovalDecisionResult> {
  const failure = (status: number, error: string): ApprovalDecisionResult => ({
    ok: false,
    status,
    result: '',
    error,
    settled: isSettledStatus(status, decision)
  });

  if (!isApprovalId(taskId)) return failure(400, approvalFailureMessage(400, decision));

  try {
    const response = decision === 'approve'
      ? await http.post(ApiFactory.TASK_APPROVAL.APPROVE(taskId))
      // The backend expects the capitalized "Comment" field.
      : await http.post(ApiFactory.TASK_APPROVAL.REJECT(taskId), { Comment: 'User rejected' });

    if (!response.ok) {
      // An unrecognised status is the only case where the server may still know
      // something useful; for the four documented ones our own wording wins.
      const known = approvalFailureMessage(response.status, decision);
      const message = isKnownApprovalStatus(response.status)
        ? known
        : await readApiError(response, known);
      return failure(response.status, message);
    }

    const body = await response.json().catch(() => ({})) as { success?: boolean; result?: string };
    if (body.success === false) {
      const detail = typeof body.result === 'string' && body.result.trim() ? body.result.trim() : '';
      return failure(response.status, detail || approvalFailureMessage(500, decision));
    }

    return {
      ok: true,
      status: response.status,
      result: typeof body.result === 'string' ? body.result : '',
      error: '',
      settled: true
    };
  } catch (cause) {
    return failure(0, errorMessage(cause, approvalFailureMessage(0, decision)));
  }
}

function isKnownApprovalStatus(status: number): boolean {
  return [400, 401, 403, 404, 409, 410, 429, 500, 502, 503, 504].includes(status);
}

/**
 * Recovers the approval id from a `[HITL_APPROVAL_REQUIRED:{id}]` marker.
 *
 * The orchestrator returns that marker as the OBSERVATION of the gated tool call,
 * and the agent-run handler persists it as a real `AgentRunStep`. It is therefore
 * the only way a page that did not watch the original stream — after a reload, or
 * when opening a parked run from the history list — can learn which approval is
 * holding that run.
 */
export function approvalIdFromMarkerText(text: string | null | undefined): string {
  if (typeof text !== 'string') return '';
  const match = text.match(/\[HITL_APPROVAL_REQUIRED:([^\]\s]+)\]/i);
  const id = match?.[1]?.trim() ?? '';
  return isApprovalId(id) ? id : '';
}

export interface PendingApprovalDetail {
  id: string;
  /** Tool the agent wants to run. */
  actionName: string;
  /** Decrypted payload (the SQL, the MCP call, ...) so the human can judge it. */
  details: string;
  /** When the approval stops being answerable. */
  expiresAtUtc: string;
}

/**
 * Looks one pending approval up by id so a surface that only learned the id from
 * the `[HITL_APPROVAL_REQUIRED:{id}]` marker can still show WHAT is being approved.
 * The API has no by-id endpoint; the owner-scoped pending list is the only source.
 *
 * Returns null when the approval is not (or is no longer) pending — callers show
 * the buttons anyway, because the decision endpoints are the authority on that and
 * answer 404/409/410 with a precise explanation.
 */
export async function fetchPendingApproval(approvalId: string): Promise<PendingApprovalDetail | null> {
  if (!isApprovalId(approvalId)) return null;
  try {
    const response = await http.get(ApiFactory.TASK_APPROVAL.PENDING);
    if (!response.ok) return null;
    const data = await response.json().catch(() => []) as Array<Partial<PendingApprovalDetail>>;
    if (!Array.isArray(data)) return null;
    const match = data.find((item) => item?.id === approvalId);
    if (!match) return null;
    return {
      id: approvalId,
      actionName: typeof match.actionName === 'string' ? match.actionName : '',
      details: typeof match.details === 'string' ? match.details : '',
      expiresAtUtc: typeof match.expiresAtUtc === 'string' ? match.expiresAtUtc : ''
    };
  } catch {
    // The card still works (approve/reject) without the detail preview.
    return null;
  }
}

// ── Putting an approved result back into the conversation ────────────────────
//
// A chat approval's continuation is driven ENTIRELY by the client: the approve
// endpoint runs the tool and returns its output, but writes nothing to the chat
// session (`AgentRunApprovalReconciler` only resolves agent runs). The chat card
// closes that loop by sending the output back as the next user turn; the two
// helpers below let the Approvals page — which never knew the session — do the
// same, so approving from either surface leaves the same durable transcript.

/** The follow-up user turn that carries an approved tool result back to the model. */
export function approvalContinuationPrompt(result: string): string {
  return `Tôi đã phê duyệt hành động trên. Kết quả thực thi là: ${result}. Vui lòng tiếp tục.`;
}

export interface ApprovalChatSession {
  id: string;
  title: string;
}

/** All-zero GUID, which is how `System.Text.Json` serializes an unset `Guid`. */
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

/**
 * The three fields `GET /api/taskapproval/pending` carries about the conversation an
 * approval belongs to. All optional: a server that predates them simply omits them,
 * and {@link resolveApprovalChatSession} falls back to the content search.
 */
export interface ApprovalSessionFields {
  /** The approval's own id — the search term for the fallback path. */
  id: string;
  /** `TaskApproval.ChatSessionId`, straight off the row. */
  chatSessionId?: string | null;
  /**
   * True when {@link chatSessionId} names a session the user can actually open and
   * continue. False for the two substrates that carry a real ChatSessionId but are
   * excluded from every chat list and search: the hidden `IsAgentRun` session behind
   * an agent run or a computer-use gate, and a temporary (`IsEphemeral`) chat.
   */
  isChatSession?: boolean | null;
  /** Title of that session; only meaningful when {@link isChatSession}. */
  chatSessionTitle?: string | null;
}

/**
 * The conversation to write an approved result into, straight off the pending row.
 *
 * Returns null when the row states there is no such conversation — no request is
 * made in that case, which is the whole point: an agent-run or temporary-chat
 * approval is answered without a speculative search.
 *
 * Returns undefined when the row says nothing, i.e. the server has not (yet) grown
 * the fields. The caller then uses {@link findApprovalChatSession}.
 */
export function approvalChatSessionFromRow(
  row: ApprovalSessionFields
): ApprovalChatSession | null | undefined {
  if (row.isChatSession === false) return null;
  if (row.isChatSession !== true) return undefined;

  const id = typeof row.chatSessionId === 'string' ? row.chatSessionId.trim() : '';
  // A row that claims to be a chat session but carries no usable id contradicts
  // itself; treat it as "server said nothing" rather than trusting half of it.
  if (!isApprovalId(id) || id === EMPTY_GUID) return undefined;

  return { id, title: typeof row.chatSessionTitle === 'string' ? row.chatSessionTitle : '' };
}

/**
 * The one call a surface makes to learn where an approved result should go.
 *
 * Prefers the row's own `chatSessionId` — the id is already on `task_approvals` and
 * needs no guesswork. {@link findApprovalChatSession} is the fallback for a server
 * that does not yet project those fields, and can be deleted along with this branch
 * once every deployed API carries them.
 */
export async function resolveApprovalChatSession(
  row: ApprovalSessionFields
): Promise<ApprovalChatSession | null> {
  const fromRow = approvalChatSessionFromRow(row);
  if (fromRow !== undefined) return fromRow;
  return findApprovalChatSession(row.id);
}

/**
 * Finds the chat session an approval was raised in, by searching message content
 * for the `[HITL_APPROVAL_REQUIRED:{id}]` marker the orchestrator emitted and the
 * chat handler persisted with the assistant turn.
 *
 * TRANSITIONAL: only reached when the pending row omits the fields
 * {@link approvalChatSessionFromRow} reads. It is fragile in exactly the ways a
 * derived lookup is — it breaks if the marker text changes, if the message is edited
 * or trimmed, and it finds nothing for a session that persists no messages — which is
 * why the row's own `ChatSessionId` supersedes it.
 *
 * Returns null (not an error) for the two cases where there is legitimately nothing
 * to continue: the search endpoint excludes agent-run sessions — those resolve
 * themselves through the reconciler and are surfaced on the agent-run page — and
 * temporary chats, which persist no messages to match against. Those are the same two
 * cases `isChatSession: false` names directly.
 */
export async function findApprovalChatSession(approvalId: string): Promise<ApprovalChatSession | null> {
  if (!isApprovalId(approvalId)) return null;
  try {
    const response = await http.get(ApiFactory.CHAT.SEARCH_SESSIONS(approvalId.trim()));
    if (!response.ok) return null;
    const data = await response.json().catch(() => []) as Array<{ id?: unknown; title?: unknown }>;
    if (!Array.isArray(data)) return null;
    const match = data.find((item) => typeof item?.id === 'string' && item.id);
    if (!match) return null;
    return {
      id: match.id as string,
      title: typeof match.title === 'string' ? match.title : ''
    };
  } catch {
    // A failed lookup is reported by the caller as "chưa ghi được vào cuộc trò
    // chuyện", never as a failed approval — the tool has already run by now.
    return null;
  }
}

export interface ContinuationOutcome {
  /** The turn streamed to completion; the conversation now holds the result. */
  ok: boolean;
  /** The assistant's follow-up answer, markers stripped. */
  answer: string;
  /** Vietnamese explanation; empty when `ok`. */
  error: string;
  /** Set when the continuation itself paused on a NEW approval gate. */
  pendingApprovalId: string;
}

/**
 * Sends the approved result into `sessionId` as the next user turn and drains the
 * SSE response. Draining matters more than displaying: the chat handler persists
 * the user turn before streaming and the assistant turn when the stream ends, so
 * running this to completion is what makes the result durable in the conversation.
 *
 * Uses `ChatSseParser` (the shared protocol reader) rather than `consumeStream`:
 * there is no reactive bubble, no transcript to scroll, and no single-stream abort
 * controller to share with the chat surfaces — only three events matter here.
 */
export async function streamApprovedContinuation(
  sessionId: string,
  result: string
): Promise<ContinuationOutcome> {
  const outcome: ContinuationOutcome = { ok: false, answer: '', error: '', pendingApprovalId: '' };

  try {
    const response = await http.post(ApiFactory.CHAT.STREAM, {
      SessionId: sessionId,
      Message: approvalContinuationPrompt(result),
      ModelId: null,
      // The tool output is already in hand; the continuation only has to read it.
      enableWebSearch: false
    });

    if (!response.ok) {
      outcome.error = await readApiError(response, 'Không thể gửi kết quả vào cuộc trò chuyện');
      return outcome;
    }
    if (!response.body) {
      outcome.error = 'Trình duyệt không hỗ trợ streaming response.';
      return outcome;
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder('utf-8');
    const parser = new ChatSseParser();
    let finished = false;
    let streamError = '';

    while (!finished) {
      const { done, value } = await reader.read();
      const events = done
        ? [...parser.push(decoder.decode()), ...parser.finish()]
        : parser.push(decoder.decode(value, { stream: true }));
      for (const event of events) {
        if (event.type === 'done') {
          finished = true;
          break;
        }
        if (event.type === 'error') {
          streamError = event.value;
          finished = true;
          break;
        }
        if (event.type === 'approval') {
          // The continuation hit another gate. The turn is still persisted; the
          // caller surfaces the new approval instead of pretending it finished.
          outcome.pendingApprovalId = event.value;
          finished = true;
          break;
        }
        if (event.type === 'content') outcome.answer += event.value;
        // thinking / reasoning / file / step / web-search markers are noise here.
      }
      if (done) break;
    }
    if (finished) await reader.cancel().catch(() => {});

    if (streamError) {
      outcome.error = streamError;
      return outcome;
    }
    outcome.ok = true;
    return outcome;
  } catch (cause) {
    outcome.error = errorMessage(cause, 'Không thể gửi kết quả vào cuộc trò chuyện.');
    return outcome;
  }
}
