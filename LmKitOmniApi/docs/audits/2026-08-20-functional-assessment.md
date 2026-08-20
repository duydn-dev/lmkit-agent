# Đánh giá chức năng toàn hệ thống LMKit Agent

**Ngày đánh giá và remediation:** 2026-08-20

**Phạm vi:** API, AI runtime, dữ liệu, bảo mật, web client, kiểm thử và Docker deployment

**Điểm trưởng thành sau remediation:** **7,3/10** (trung bình không trọng số của 23 nhóm, làm tròn một chữ số)

## Cách chấm điểm

- **0-2:** placeholder hoặc không có đường dùng end-to-end.
- **3-4:** có code nền nhưng thiếu luồng hoàn chỉnh hoặc có lỗi nghiêm trọng.
- **5-6:** chức năng chính chạy được nhưng chưa đủ ổn định cho production.
- **7-8:** tương đối hoàn chỉnh, có kiểm soát và bằng chứng test; còn thiếu hardening/eval/load test.
- **9-10:** production-grade, có integration/E2E/load/security tests, SLO và rollback đã chứng minh.

## Checklist chức năng sau remediation

| # | Chức năng | Điểm /10 | Điểm yếu còn lại cần khắc phục |
|---:|---|---:|---|
| 1 | Đăng nhập, JWT, refresh token, logout và session | **8,2** | Đã có refresh rotation theo thiết bị, hash token, access-token blacklist, kiểm tra session/user active ở mỗi request, cookie dev/prod rõ ràng và browser E2E qua API/database thật. Còn thiếu MFA, recovery flow và kiểm thử nhiều thiết bị/trình duyệt. |
| 2 | Quản trị user, role và tenant | **8,0** | CRUD đã scope tenant, role allowlist, email unique, chặn tự disable/demote và bỏ quyền chọn tenant từ request. Chưa có tenant-management UI và audit viewer. |
| 3 | Chat session, lịch sử và SSE | **7,7** | Ownership được kiểm tra trước khi load model; missing/cross-tenant session trả 404; history/input có cap; attachment giới hạn 8 file/50 MB và file scratch được xóa bằng `finally`. Vòng đời session đã có browser E2E qua API/database thật. Output vẫn buffer để chạy guardrail trước khi phát; SSE chưa có event schema/version chuẩn và chat model thật chưa nằm trong full-stack E2E. |
| 4 | ReAct agent runtime | **7,5** | LM-Kit native ReAct, structured tools, filter, timeout, permission và approval hoạt động. Vẫn thường có lượt ReAct rồi synthesis nên latency cao; chưa có golden eval để chứng minh chất lượng/chi phí. |
| 5 | LM-Kit Default Tools | **8,0** | Sáu tool read-only phù hợp đã bật: arithmetic, time, JSON, CSV, XML và statistics. Chưa có eval chọn tool/false-call; file/PDF/image tools chưa được bật vì cần policy và ownership riêng. |
| 6 | Multi-agent supervisor và specialist | **7,0** | Role thật được truyền xuyên supervisor/specialist, lỗi path có khoảng trắng đã sửa. Chưa có parallel/load/eval; swarm interfaces cũ vẫn chỉ là scaffolding ngoài đường chạy chính. |
| 7 | Agent memory | **8,0** | Có tenant/user scope, retention worker, semantic recall, overwrite contradiction và UI xem/xác nhận/xóa. Fact heuristic là unconfirmed và không được đưa vào prompt trước khi user xác nhận; còn thiếu precision/recall eval và chỉnh sửa trực tiếp. |
| 8 | RAG và knowledge base | **8,0** | Vector filter theo tenant+owner, reindex migration, deterministic chunk IDs, citation chunk, Admin-only ingest và delete end-to-end. Chưa có shared-document ACL, page-level citation, quality eval và DB/vector outbox cho distributed atomicity. |
| 9 | MCP integration | **6,5** | Có tenant-scoped CRUD/UI, SSRF guard, encrypted headers, cache dùng chung và không trả secret. Đây vẫn là REST MCP adapter (`/mcp/tools`, `/mcp/invoke`), chưa phải MCP transport/capability negotiation chuẩn. |
| 10 | Tool RBAC, sandbox, resilience, HITL và audit | **8,0** | Approved action re-check quyền hiện tại; write/unknown action không retry; regression test chứng minh chạy một lần. AI HTTP quota dùng Redis atomic fixed-window với local fallback; circuit-breaker state vẫn chưa atomic tuyệt đối và policy role còn hai mức đơn giản. |
| 11 | Quản lý tài liệu | **8,0** | Magic-byte validation, lifecycle Pending/Processing/Completed/Failed, atomic lease/retry, delete vector/file/DB và UI trạng thái thật. Còn thiếu virus scan, object storage, dead-letter UI và multi-replica integration test. |
| 12 | Vision, OCR, classification, remove background | **7,5** | Ownership/signature/size checks, cleanup temp, response cap và per-model concurrency gate đã có. Chưa có image golden test và native workload vẫn chạy trong request. |
| 13 | Speech và LiveKit voice | **5,8** | Token đã tenant-scoped, speech endpoint có limit/rate limit; client dùng refresh-aware HTTP, hiện lỗi và cleanup audio track. LiveKit vẫn là development profile, room UI còn tĩnh và chưa có backend voice-agent/media pipeline production. |
| 14 | Text analysis và embeddings | **7,5** | Endpoint có auth, per-user rate limit, input cap và per-model concurrency gate; dùng LM-Kit thật. Còn thiếu batch API và quality set tiếng Việt. |
| 15 | Content creation pipeline | **6,5** | Multi-stage pipeline và fact-check gateway có thật, input bị giới hạn/rate limit. Chưa có UI, citation schema và factuality/golden evaluation. |
| 16 | Web search | **6,5** | Typed client, timeout, cancellation, response cap, cache và redirect normalization đã bổ sung. DuckDuckGo HTML scraping vẫn không ổn định như API chính thức và chưa fetch/verify nội dung nguồn. |
| 17 | Web client chính | **8,8** | XSS đã chặn; Copy/HITL hoạt động thật; API ProblemDetails/text dùng error contract chung và hiển thị alert. Playwright chạy production artifact; suite full-stack thật bao phủ auth, session, document và user admin; axe WCAG scan sạch trên fixtures và theme contrast đã sửa. Còn thiếu browser E2E với model/MCP thật, screen-reader và reflow exploratory. |
| 18 | Embeddable chat widget | **4,5** | Đã chặn route bằng auth và giới hạn `postMessage` theo referrer origin, nên không còn giả vờ public/insecure. Chưa phải widget nhúng công khai; cần scoped widget credential, origin allowlist và quota trước khi mở. |
| 19 | Notification, graph, API key và automation | **4,5** | Fake SignalR echo, Hangfire, Telegram, proactive job và graph runtime đã gỡ khỏi active code. Graph/API-key/notification entities còn là schema kế thừa, không phải chức năng triển khai và không được quảng cáo trong UI. |
| 20 | Observability, audit và health | **8,0** | Có readiness/liveness, PostgreSQL/Redis/Qdrant/model-license checks, warmup tùy chọn, Prometheus/OTLP, audit và hash tenant trace. Production phải bật ba model gate trong env; chưa có dashboard/alert/SLO và audit query UI. |
| 21 | Docker/deployment/config | **8,0** | Non-root API, init volume ownership, persisted model/upload/key volumes, dependency health, migration-before-traffic và security headers đã có. Production vẫn cần TLS ingress, secret manager, certificate rotation, backup/restore drill và immutable image registry. |
| 22 | Test và CI/CD | **8,8** | 95 backend, 16 frontend unit/source, 4 browser contract/axe và 1 browser full-stack test pass. CI dựng stack cô lập Nginx/API/PostgreSQL/Redis/Qdrant và kiểm tra auth/session/document/user thật. Release/Docker build, npm/NuGet vulnerability audit và migration drift sạch. Chưa có model-backed E2E, coverage gate, AI golden set, load/chaos/DAST. |
| 23 | Tài liệu và vận hành | **8,5** | Root README, capability matrix, architecture boundaries, deployment/rollback runbook, ADR và checklist này đã có. Source placeholder với hơn 400 `NotImplementedException` và roadmap quảng cáo sai đã được loại khỏi product tree. |

## Hạng mục đã đóng trong đợt remediation

- Khóa các lỗi P0: XSS output, SignalR broadcast, model URL từ request, session revocation, tenant isolation và RAG owner ACL.
- Hoàn thiện document lifecycle: signature validation, claim lease, retry/failure state, reindex và delete end-to-end.
- Hardening MCP adapter: CRUD admin, SSRF validation, encrypted secret headers và cache đúng lifetime.
- Bổ sung rate limit cho endpoint AI, input/output cap, web-search timeout/cache và non-idempotent retry policy.
- Sửa auth state/frontend logout, thêm memory/MCP UI, bỏ các tab chức năng giả chưa có backend.
- Bổ sung health checks, Data Protection persistence/certificate option, non-root container, model volume, CI và runbook.
- Bổ sung per-model inference gate/cancellation, Redis atomic AI quota, model warmup/readiness gate và memory confirmation consent.

## Giới hạn chưa được phép gọi là “production-ready”

1. Chưa chạy inference E2E với model thật và license LM-Kit trong CI.
2. Chưa có AI golden evaluation, browser full-stack E2E có inference model thật, load/chaos/DAST và SLO production.
3. MCP hiện là REST adapter, không phải MCP standard transport.
4. Widget chỉ hoạt động trong phiên đã đăng nhập; public embedding cần credential/origin contract riêng.
5. LiveKit profile hiện là development media transport, chưa phải voice-agent production.

## Bằng chứng kiểm tra

- `dotnet test ... -c Release`: **95/95 pass**, gồm HTTP integration cho auth cookie, authorization, memory consent/tenant scope, approval isolation/idempotency, session missing/cross-tenant contract, local rate-limit contract, attachment batch quota và cleanup scratch file.
- `npm run test:unit`: **16/16 pass**, bao phủ XSS-safe formatting, SSE parsing, API error contract và 5 accessibility source guardrails.
- `npm run test:e2e`: **4/4 pass** trên production frontend artifact; bao phủ auth error, chat SSE, documents, memory, user admin, MCP dialog, mobile navigation và axe WCAG 2.1 A/AA fixtures.
- `npm run test:e2e:fullstack`: **1/1 pass** qua Nginx/API/PostgreSQL/Redis/Qdrant thật; bao phủ security headers, login/cookie, session create/delete, document upload/delete, user create/lock và logout/revocation.
- `npm audit --audit-level=high`: **0 vulnerability**.
- `dotnet list ... package --vulnerable --include-transitive`: **không có package dễ tổn thương** theo nguồn NuGet hiện tại.
- `dotnet ef migrations has-pending-model-changes`: **không có model drift**.
- `docker compose --env-file .env.example config --quiet`: hợp lệ.
- Docker artifact mới: API/client build sạch; PostgreSQL/Redis/API healthy; `/health/ready`, `/health/live` và frontend đều HTTP 200.
- Smoke vòng cuối: memory fixture `IsConfirmed=false` → confirm HTTP 204 → `true`; 10 request AI nhận validation 400 và request thứ 11 nhận 429 + `Retry-After: 60`; Redis counter bằng 11 và có TTL.
- Smoke vòng trước: login → me → refresh → logout; token/refresh cũ đều 401; cross-tenant user update 404; upload/list/delete document thành công; file giả PDF bị 400.

## Gate phát hành còn bắt buộc

- Chạy model/license readiness và một golden set tiếng Việt trên đúng artifact production.
- Mở rộng browser full-stack E2E hiện có sang chat inference, memory và MCP với model/license/server thật; kiểm tra lại CSP/security headers tại ingress production.
- Chạy load test theo cấu hình phần cứng đích, đặt ngưỡng p95/error rate và chứng minh rollback/backup restore.
- Nếu cần public widget hoặc MCP chuẩn, triển khai contract riêng rồi audit lại; không mở route hiện tại ra public bằng cách bỏ auth.
