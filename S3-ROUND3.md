# S3 — round 3: approval UX (known-issues #5 and #6)

Branch `round3/s3-approval-ux`, based on master `140239c`.

Files changed:

| file | what |
| --- | --- |
| `LmKitOmniClient/src/composables/useTaskApproval.ts` | **new** — the one place an approval is resolved from: the POST, the outcome classification, every Vietnamese failure string, plus the two helpers that carry an approved result back into its conversation |
| `LmKitOmniClient/src/composables/useChatStream.ts` | `useHitlActions` now routes through that composable; the pending-detail lookup was de-duplicated into it |
| `LmKitOmniClient/src/views/approvals/ApprovalsView.vue` | defect 1 |
| `LmKitOmniClient/src/views/agents/AgentRunsView.vue` | defect 2 |
| `LmKitOmniClient/src/views/agents/agentRunStatus.ts` | **new** — Vietnamese label / colour / description for all seven `AgentRunStatuses` values |
| `LmKitOmniClient/src/composables/useTaskApproval.test.ts`, `src/views/agents/agentRunStatus.test.ts` | **new** — 42 tests |

---

## (a) What the user sees, end to end

### Chat card (`ChatView` / `ChatWidgetView`) — unchanged flow, better failures

Approve → the tool runs → a system line lands in the transcript → the composer is
seeded with the continuation prompt → the turn is sent, and the answer streams into
the conversation. Exactly as before; the only difference is that a failure now says
which of the four things happened, in Vietnamese, instead of echoing the API's
English `"Task is no longer pending."`.

A failed decision deliberately leaves the card's buttons in place. `hitlResolved`
only renders as "Đã Phê duyệt" or "Đã Từ chối", and an approval that expired or was
answered in another tab is neither — claiming a decision the user did not make would
be worse than a button that repeats the explanation. Giving the chat card an honest
"closed" state needs a change in `ChatView.vue` / `ChatWidgetView.vue`, which I do
not own this round.

### Approvals page — the result now reaches the conversation

1. Each pending card shows the action, the decrypted payload, and **how long the
   approval can still be answered** (amber under an hour). The payload preview is
   new in practice: `toRow()` never copied `details`, so the `<pre>` block in the
   template had been dead since it was written.
2. Approve → success badge + the tool result (expandable, with a copy button).
3. The page then **continues the conversation itself**: it finds the chat session
   that raised the approval and posts the same continuation turn the chat card
   posts, draining the SSE response. The user sees
   *"Đã ghi kết quả vào cuộc trò chuyện "{tiêu đề}" và agent đã trả lời tiếp."*,
   the agent's follow-up answer, and a **Mở cuộc trò chuyện** link. Opening the
   session shows the same transcript approving from the chat card would have left.
4. If the continuation itself hits another approval gate, the card says so and the
   new task appears in the list above.
5. If no chat session owns the approval (an agent-run approval, or a temporary chat
   that persists nothing), the card says plainly: *"Hành động đã chạy xong, nhưng
   kết quả **chưa được ghi vào cuộc trò chuyện nào**"*, points at the agent-run page,
   and tells the user to copy the result. **The user is never left believing the
   result was delivered.**
6. If the continuation breaks mid-way, the card says the result may not have been
   written in full and links to the session so the user can check.
7. A decision that fails terminally (404/409/410, or a failed execution) retires the
   buttons behind a **"Không còn xử lý được"** badge with the specific explanation,
   instead of inviting a retry that cannot work.

### Agent-run page — the gate is answered where it appears

1. The run parks at the gate and a **"Cần bạn phê duyệt"** card appears with the
   action name, the payload, the remaining time, **Phê duyệt** / **Từ chối**, and
   the existing "Mở trang phê duyệt" link (kept as a fallback).
2. Deciding re-reads `GET /api/agent-runs/{id}`: the status pill flips to
   *Hoàn tất sau phê duyệt* / *Đã từ chối*, the tool call the run was waiting on
   appears as a real step with its output, and the run result picks up the appended
   summary. The history list refreshes with it.
3. **This also works after a reload.** The gate card is no longer tied to the stream
   that produced it: opening a parked run from the history list recovers the approval
   id from the run's own steps (the orchestrator persists the gated call with
   `[HITL_APPROVAL_REQUIRED:{id}]` as its observation). Without that, only the tab
   that watched the original stream could ever act — a refresh put the user right
   back to hunting on another page.
4. If the approval was already answered elsewhere or swept as expired, the card
   retires its buttons, says so, and shows the run's refreshed terminal state.
5. All seven statuses now render in Vietnamese with their own pill colour and a
   `title` explaining them. An unknown status still degrades to its own identifier
   in a neutral pill, so a status added server-side can never blank the UI.

---

## (b) Defect 1: solved client-side

**Solved, not mitigated** — for chat approvals, which is what the defect is about.

`POST /api/taskapproval/{id}/approve` runs the tool and hands the output to whoever
called it; `AgentRunApprovalReconciler` only writes back for **agent runs** (it
returns early when no parked run matches, which is every chat approval). So the
Approvals page has to do what the chat card does, and it needs two things the
approve response does not carry: the session id, and a continuation turn.

- **Session id.** `PendingApprovalDto` projects `ActionName` / `Details` /
  `CreatedAtUtc` / `ExpiresAtUtc` and deliberately not `ChatSessionId`, and there is
  no by-id approval endpoint. But `StreamChatCommandHandler` persists the assistant
  turn verbatim, markers included, so `[HITL_APPROVAL_REQUIRED:{id}]` is in the
  message body — and `GET /api/chat/sessions/search?q=` matches message content.
  Searching for the GUID finds exactly the session that raised the approval.
  The search excludes `IsAgentRun` and `IsEphemeral` sessions, which is the correct
  behaviour here rather than a limitation: those are the two cases where there is
  genuinely nothing to continue, and the page says so.
- **Continuation.** `POST /api/chat/stream` with the same prompt the chat card uses
  (`approvalContinuationPrompt`, now shared by both). The handler persists the user
  turn before streaming and the assistant turn when the stream ends, so draining the
  response is what makes the result durable — including if the user navigates away
  mid-stream, which persists the partial answer.

**A backend change would still be better**, and here is the exact one: give
`PendingApprovalDto` a `ChatSessionId` (it is already on the `TaskApproval` row, and
`GetPendingApprovalsQueryHandler` already selects from it). That removes the content
search — one hop instead of two, works for approvals whose owning message was
trimmed, and works for temporary chats. It does not change the continuation half.

The stronger backend fix — persisting an assistant turn for chat approvals inside
`ApproveTaskCommandHandler`, the way the reconciler already does for agent runs —
would make the client continuation optional rather than load-bearing, but it would
record the raw tool output rather than an answer, so the client turn would probably
still be wanted.

**Known limitation:** the continuation costs a model turn the user did not explicitly
ask for. That is exactly what approving from the chat card already does, so the two
surfaces now behave identically; it is stated on the card while it runs.

---

## (c) Every status handled, and its Vietnamese message

From `approvalFailureMessage(status, decision)` in `useTaskApproval.ts`. Used
identically by the chat card, the Approvals page and the agent-run page. For every
status listed here the server's own body is **ignored** — the API answers in English
(`"Task is no longer pending."`, `"Task approval has expired and was not executed."`)
and those would otherwise land verbatim in a Vietnamese UI.

| status | approve | reject |
| --- | --- | --- |
| 0 (no response) | Không kết nối được tới máy chủ. Kiểm tra kết nối mạng rồi thử lại. | (same) |
| 400 | Yêu cầu phê duyệt không hợp lệ. | Yêu cầu từ chối không hợp lệ. |
| 401 | Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại rồi thử lại. | (same) |
| 403 | Bạn không có quyền phê duyệt tác vụ này. | Bạn không có quyền từ chối tác vụ này. |
| **404** | Không tìm thấy tác vụ này — có thể tác vụ đã được xử lý ở nơi khác hoặc đã bị xóa. Không có hành động nào được thực thi. | Không còn tác vụ nào đang chờ để từ chối — tác vụ đã được phê duyệt, đã bị từ chối, hoặc đã hết hạn. |
| **409** | Tác vụ không còn ở trạng thái chờ: người khác (hoặc một tab khác) đã quyết định trước bạn. Không có hành động nào được thực thi thêm. | Tác vụ không còn ở trạng thái chờ: người khác đã quyết định trước bạn. |
| **410** | Yêu cầu phê duyệt đã hết hạn trước khi có người trả lời, nên hành động KHÔNG được thực thi. Hãy yêu cầu agent thực hiện lại. | Yêu cầu phê duyệt đã hết hạn nên không cần từ chối nữa. Hành động KHÔNG được thực thi. |
| 429 | Bạn thao tác quá nhanh. Vui lòng chờ một chút rồi thử lại. | (same) |
| **500** | Tác vụ đã được phê duyệt nhưng thực thi thất bại. Hành động không hoàn tất — vui lòng kiểm tra nhật ký máy chủ. | Không ghi nhận được việc từ chối do lỗi máy chủ. Vui lòng thử lại. |
| 502 / 503 / 504 | Máy chủ đang không phản hồi. Vui lòng thử lại sau ít phút. | (same) |
| anything else | `Phê duyệt không thành công (mã lỗi {status}).` — and the server's body is preferred when it has one | `Từ chối không thành công (mã lỗi {status}).` |

A 200 body with `success: false` is also treated as a failure, using the body's own
`result` text when present.

**Which of those retire the buttons** (`isSettledStatus`): 404, 409, 410 for both
decisions, plus 500 on an approve — the handler has already flipped the row to
`Failed`, so a retry only earns a 409. A failed *reject* stays retryable: nothing on
the server is known to have changed. A transport failure (0) stays retryable.

Continuation failures are reported separately, never as approval failures — the tool
has already run by then.

### Agent-run statuses (`agentRunStatus.ts`)

| status | label | pill |
| --- | --- | --- |
| Running | Đang chạy | amber |
| Completed | Hoàn tất | emerald |
| AwaitingApproval | Chờ phê duyệt | sky |
| CompletedAfterApproval | Hoàn tất sau phê duyệt | teal |
| Rejected | Đã từ chối | rose |
| Expired | Hết hạn phê duyệt | slate |
| Failed | Thất bại | red |
| *(unknown)* | the identifier itself, else "Không rõ" | neutral grey |

`Expired` and `Rejected` both end a run without executing anything, so their
descriptions keep "nobody looked" and "a human said no" apart — that distinction is
the reason `AgentRunStatuses` has both, and a test pins it.

---

## (d) `known-issues.md` entries resolved (I did not edit that file)

- **#5** — "Approving from the Approvals page loses the result for chat sessions". Fixed.
- **#6** — "The agent-run page shows the approval id but cannot act on it". Fixed, including
  the reload case the entry does not mention.

#7 ("a run that ends at an approval gate does not resume the ReAct loop") is **not**
resolved and should stay: it is by design, and the UI now names that outcome
explicitly ("agent không tiếp tục suy luận thêm" on the `CompletedAfterApproval` pill).

---

## (e) Verified by running vs. by reading

**By running** (in `LmKitOmniClient/`):

- `npm run lint` → **0 errors, 5 warnings** — the same five as on master.
- `npm run build` (`vue-tsc -b` + vite) → succeeds.
- `npm run test:unit` → **81 passed** (master baseline 39; 42 added).
- **The UI was actually rendered and clicked.** I built `dist/` and served it from a
  throwaway Node server in the scratchpad (`mockserver.mjs`, outside the repo) that
  stubs the handful of endpoints these pages call, then drove it in a real browser.
  Verified by looking at it:
  - Approvals page: pending list with payload preview and expiry; approve →
    result + *"Đã ghi kết quả vào cuộc trò chuyện …"* + streamed follow-up + link;
    a 410 → "Không còn xử lý được" with the expiry explanation and no buttons;
    an approval with no owning session → the amber "chưa được ghi vào cuộc trò
    chuyện nào" warning.
  - Agent-run page: run to the gate → inline gate card → approve → pill flips to
    *Hoàn tất sau phê duyệt*, the `db_write` step and its output appear, the result
    picks up the appended summary, the history list updates; reject → *Đã từ chối*.
  - Reload path: fresh page load, open the parked run from the history list → the
    gate card appears (id recovered from the run's steps) → approve → the open
    history card refreshes to *Hoàn tất sau phê duyệt · 3 bước*.
  - All seven status pills, plus an unknown status falling back to grey.

**By reading only** (no live API was available):

- That `GET /api/chat/sessions/search?q={guid}` matches the persisted
  `[HITL_APPROVAL_REQUIRED:{id}]` marker against a *real* database. Read from
  `GetChatSessionsSearchQueryHandler` (`s.Messages.Any(m => m.Content.ToLower().Contains(term))`)
  and `StreamChatCommandHandler` (`fullResponseBuilder` is persisted verbatim). The
  mock returned a session for the search; it did not prove the marker is in the row.
- The real 404/409/410/500 wire behaviour of `TaskApprovalController` — read from the
  controller and `ApproveTaskCommandHandler`; the mock reproduced the shapes.
- That `AgentRunApprovalReconciler` writes the step, appends to `Result` and sets the
  terminal status inside the approve/reject request, which is why one re-poll is enough.
- Rate limiting (`ai-agent`) on `/api/chat/stream`, which the continuation now hits
  from a second surface.

---

## (f) Found, not fixed

1. **The chat card has no honest "closed" state.** `ChatView.vue:193` and
   `ChatWidgetView.vue` render `hitlResolved` as only "Đã Phê duyệt" / "Đã Từ chối",
   so a 409/410 leaves the buttons up (with a correct explanation) rather than
   retiring them. `submitApprovalDecision` already returns `settled`; wiring it needs
   a third branch in those two templates, which are not mine this round.
2. **The Approvals page cannot continue an approval raised in a temporary chat.**
   Nothing is persisted for those, so there is no session to find and nothing the
   client could write to. The page states it. Fixing it properly needs the backend.
3. **`ApprovalsView` used to drop `details`.** `toRow()` never copied the field, so
   the payload preview never rendered — the user was approving a bare action name.
   Fixed here because it is in a file I own, but it predates this round.
4. **Concurrent approvals are still allowed.** Each card disables its own buttons
   while its request is in flight (unchanged), but two cards can be approved at once,
   which runs two tools concurrently. "Làm mới" is now disabled while anything is in
   flight, because a full reload would discard a card's result and its continuation.
5. **The expiry countdown does not tick.** It is computed at render from
   `ExpiresAtUtc` and refreshes on load / "Làm mới". A live countdown would need a
   timer; it did not seem worth one.
6. **The `ExecuteDirectActionAsync` tool scope hole (known-issues #2)** is still open
   and is now reachable from one more button. Nothing I can do client-side; noting it
   because this round makes approving easier from two more places.
