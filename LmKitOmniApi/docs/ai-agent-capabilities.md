# Khả năng AI đang được hỗ trợ

Tài liệu này mô tả các đường chạy có thật trong `LmKitOmniApi`. Một chức năng chỉ được liệt kê là hỗ trợ khi có API/runtime tương ứng và được build trong product project.

Chức năng tắt mặc định được liệt kê riêng ở mục [Chức năng opt-in](#chức-năng-opt-in) — đừng đọc bảng bên dưới như thể mọi thứ đều bật sẵn.

## Agent runtime

| Khả năng | Hiện trạng | Ghi chú |
|---|---|---|
| Chat có lịch sử | Hỗ trợ | Session theo tenant/user, SSE, giới hạn history và cache. |
| ReAct planning | Hỗ trợ | LM-Kit native agent, tối đa 5 vòng lặp (`AgentOrchestrator.MaxReActIterations`), structured tool calls. |
| Multi-agent | Hỗ trợ có điều kiện | Supervisor cùng **Research, Analysis và Vision** specialists (`Infrastructure/AI/Agents/`); chưa có quality/load benchmark production. |
| Human approval | Hỗ trợ | Tool có rủi ro phải qua permission, sandbox và approval; quyền được kiểm tra lại khi thực thi. |
| Agent memory | Hỗ trợ | Memory theo tenant/user, semantic recall, retention worker và UI xem/xác nhận/xóa. Fact heuristic không vào prompt trước khi được xác nhận. |
| Graph memory | Không hỗ trợ | Không có graph runtime, cũng không còn entity graph nào trong `Domain/Entities`. |

Tool resilience được cô lập theo tenant/tool. Production dùng Redis Lua để đếm failure và cấp đúng một half-open probe giữa nhiều replica; circuit key được hash và side-effect không idempotent không bị retry.

## Công cụ agent

Các LM-Kit Default Tools được bật mặc định đều là read-only, deterministic và không truy cập filesystem/process/environment (`Infrastructure/AI/Tools/LmKitDefaultToolCatalog.cs`):

- arithmetic;
- current date/time;
- JSON query/validation;
- CSV inspection;
- XML inspection;
- descriptive statistics.

Các thao tác ứng dụng sau đi qua permission, sandbox, timeout, output cap, resilience và audit:

- tìm kiếm RAG theo owner;
- phân tích vision trên file thuộc quyền user;
- speech transcription trên file thuộc quyền user;
- text analysis;
- web search;
- multi-agent delegation;
- summarization;
- MCP Streamable HTTP tools được tenant admin cấu hình và gọi qua official MCP C# SDK.

Không có hàng trăm tool Office/PDF/Image tự sinh. Các source placeholder từng ném `NotImplementedException` đã bị xóa; muốn bổ sung tool mới phải có implementation, ownership policy và test riêng.

## Chức năng opt-in

Các mục dưới đây **đã có code và test, nhưng tắt mặc định** trong `appsettings.json`. Khi tắt, endpoint tương ứng trả `501 Not Implemented` (hoặc list rỗng) — không phải lỗi.

| Cấu hình | Mặc định | Cần thêm gì để bật |
|---|---|---|
| `CodeInterpreter:Python:Enabled` | `false` | Một container image Python (`CodeInterpreter:Python:Image`) + Docker socket. Sandbox JavaScript (Jint) thì **bật sẵn** (`JavaScriptEnabled: true`). |
| `BrowserTool:Enabled` | `false` | `BrowserTool:Image` + allowlist host. |
| `WebRead:Enabled` | `false` | Không cần gì thêm; wrap `WebReadTool` của LM-Kit sau guard chỉ-public-web. |
| `DocumentTools:Enabled` | `false` | Không cần gì thêm (PdfForm / PdfRedactor / document API của LM-Kit). |
| `DatabaseAgent:Enabled` | `false` | Connection do Admin tạo; `DatabaseAgent:AllowedHosts` phải liệt kê host được phép. |
| `ComputerUse:Enabled` | `false` | `ComputerUse:Image` (browser container). Giữ nguyên `RequireApprovalPerAction: true`. |
| `Lora:Enabled` | `false` | Không cần gì thêm — `Lora:AdapterStoragePath` rỗng sẽ tự dùng `<cwd>/App_Data/lora`. Adapter chỉ Admin upload được. |
| `GroundingEval:Enabled` | `false` | Không cần gì thêm — harness gọi model qua seam `IComputerUseModel`. |
| `GroundingTraining:Enabled` | `false` | `DatasetPath` + `AdapterOutputPath`; muốn hot-swap adapter kết quả thì cần `Lora:Enabled` nữa. |
| `ChatReasoning:Enabled` | `false` | Không cần gì thêm. |
| `Voice:TtsEnabled` | `false` | `Voice:PiperExecutablePath` + voice model. `POST /api/speech/synthesize` trả 501 khi tắt. |
| `Voice:LiveAgentEnabled` | `false` | LiveKit URL/key/secret **và** `Voice:AgentTenantId` + `Voice:AgentUserId` (xem [known-issues #8](known-issues.md)). |
| `AgentMemory:RecallUnconfirmed` | `false` | Không cần gì thêm — nhưng là quyết định **riêng tư**: bật lên nghĩa là fact suy luận bằng regex được recall mà không cần user xác nhận. Mặc định `false` giữ đúng vòng lặp qua con người (`GET /api/memory` → `POST /api/memory/{id}/confirm`). |

`ComputerUse` có một **bất biến ngân sách thời gian** được kiểm tra lúc chạy
(`ComputerUseOptions.AreTimeBudgetsConsistent`):

```text
SessionWallClockSeconds >= MaxSteps * (StepTimeoutSeconds + ApprovalTimeoutSeconds)
```

Với cấu hình đang ship (`15 * (30 + 90) = 1800`) thì đúng. Nếu hạ `SessionWallClockSeconds`
xuống dưới ngưỡng này, agent log warning một lần ở đầu run: mọi run cần người duyệt sẽ bị hủy ở
wall clock trước khi kịp hoàn tất.

Wire contract của computer-use: action `key` **bắt buộc có `ref`**
(`{"action":"key","ref":3,"keys":"Enter"}`); thiếu `ref` bị parser từ chối, tốn một step và một
lượt hỏi lại chứ không kết thúc session. Image browser tự viết phải focus đúng element theo
`ref` rồi mới gửi phím; image bỏ qua `ref` vẫn tương thích (gửi vào element đang focus).
`key` được kiểm tra credential y hệt `type`, nên không thể gõ từng ký tự bí mật vào ô
password/OTP. Safety marker viết tắt (`pin`, `otp`, `cvv`, `cvc`, `ssn`, `pwd`, `2fa`, `mfa`)
khớp theo biên chữ cái — "Pinterest"/"spinner" không còn kết thúc session, còn "PIN", "Mã PIN",
"CVV2", "pin_code" thì vẫn.

## RAG và tài liệu

| Khả năng | Hiện trạng |
|---|---|
| Upload và magic-byte validation | Hỗ trợ |
| Markdown conversion/OCR | Hỗ trợ theo model/config |
| Background vectorization | Atomic claim, lease, retry tối đa 3 lần, sau đó `Failed` |
| Dense + sparse + RRF + cross-encoder rerank | Hỗ trợ |
| Tenant/owner vector ACL | Bắt buộc |
| Citation | File và chunk locator |
| Delete | Xóa vector, file và DB record theo ownership |
| Shared-document ACL | Chưa hỗ trợ |

### Qdrant: payload index là điều kiện của sparse search

Sparse ("hybrid") retrieval lọc bằng `Match { Text = keyword }`, và Qdrant **từ chối** filter đó
nếu field chưa có full-text payload index. `EnsureCollectionExistsAsync` tạo sẵn, idempotent và
best-effort:

| Field | Loại index | Vì sao |
|---|---|---|
| `Keywords` | full text (word tokenizer, lowercase, 2..30) | bắt buộc cho `Match.Text` |
| `AccessScope` | keyword | filter private-scope |
| `TenantId` | keyword | filter tenant-scope |
| `DocumentId` | keyword | allowlist pinned-knowledge |

Index tạo lỗi thì log Warning chứ không chặn việc tạo collection. **Collection đã tồn tại** nhận
index ở lần `EnsureCollectionExistsAsync` kế tiếp (RAG init, `SchemaIndexingService`,
`AgentMemoryService`, `DocumentVectorizationWorker`) — không cần migration, nhưng lần ensure đầu
sau khi deploy sẽ tốn thêm CPU/RAM phía Qdrant. Collection rất lớn thì nên pre-create index
out-of-band trước khi deploy.

Lỗi sparse search **được ném ra ngoài** (trước đây bị nuốt trong `catch (Exception) { }`), đó là
điều làm cho `RagPipelineService.PerformKeywordSearchFallbackAsync` chạy được thay vì là dead
code. Caller mới của `SearchByPayloadFilterAsync` / `SearchByPayloadWithinDocumentsAsync` phải tự
xử lý exception.

`VectorStore:BaseUrl` đi qua `Infrastructure/VectorDb/QdrantClientFactory.cs`, nên scheme
`https://` được tôn trọng và `VectorStore:ApiKey` (tùy chọn) được gửi kèm dưới dạng header
`api-key`.

## Web search

Chuỗi provider theo thứ tự cố định (`ResilientWebSearchService.Rank`): **SearXNG → Brave →
Tavily → DuckDuckGo scraper**, bỏ qua provider chưa cấu hình. SearXNG chạy trong compose và
không cần API key, nên nó là mặc định thật sự; `WebSearch:Searx:BaseUrl` rỗng trong
`appsettings.json` và được compose set thành `http://searxng:8080`.

`IWebSearchService.SearchWebAsync` trả `WebSearchOutcome`, **không** phải `string`:

- `ResultsJson` **luôn** là JSON array hợp lệ (`"[]"` khi không có gì trả về, vì bất kỳ lý do gì);
- trạng thái nằm ở `Status` (`Success | InvalidQuery | NotConfigured | NoResults | Unavailable`) và `Message`;
- `ToToolOutput()` chỉ để render chuỗi cho model đọc — **không bao giờ parse nó**. Consumer mới phải parse `ResultsJson` và rẽ nhánh theo `Status`.

Cache key tách namespace (`web-search:composite:` và `web-search:duckduckgo:`) và **kết quả rỗng
không được cache**; trước đây hai lớp hash cùng một tuple `{query}:{count}` nên một lần scrape
hỏng ghi `"[]"` vào cache rồi composite đọc lại như kết quả rỗng thành công của chính nó, bỏ qua
mọi provider khác trong suốt 5 phút TTL.

## Vision, speech và text analysis

- Vision: analyze, classify, OCR và remove-background.
- Speech: transcription và language detection. TTS (Piper) tắt mặc định.
- Text: sentiment/NER/PII, classification, language, keywords và embeddings.
- Mọi endpoint AI có authentication, input cap, Redis-backed per-user rate limiting (local fallback) và per-model concurrency gate.
- Production có thể bật model warmup và buộc readiness phải có license/chat model đã load.

### Voice room

`GET /api/speech/token` trả `{ token, room }`. Room name luôn là `{tenant:N}-{user:N}-{label}`,
sinh bởi `VoiceRoomNaming` — nguồn duy nhất, dùng chung cho cả token endpoint lẫn hosted agent,
nên agent và caller ở cùng một room. Label do client truyền (`?room=`) chỉ là hậu tố và được
sanitize về `[A-Za-z0-9_-]`; nó không mở rộng được scope vì prefix tenant/user do server sinh.
Hệ quả: hai người trong cùng tenant xin cùng một label sẽ vào **hai room khác nhau**.

LiveKit credentials được `VoiceLiveKitCredentials.Resolve` phân giải theo từng field:
`Voice:LiveKitUrl` / `Voice:LiveKitApiKey` / `Voice:LiveKitApiSecret` trước, rồi mới đến block
dùng chung `LiveKit:Url` / `LiveKit:ApiKey` / `LiveKit:ApiSecret` (block này cũng cấu hình chính
container LiveKit). Đặt block nào cũng được.

Giới hạn: hosted agent chỉ phục vụ **một** user đã cấu hình — xem [known-issues #8](known-issues.md).

## MCP

Hệ thống dùng official MCP C# SDK và Streamable HTTP. Client ưu tiên protocol stateless `2026-07-28` (`server/discover`, `tools/list`, `tools/call`, `Mcp-Method`/`Mcp-Name`/`Mcp-Param-*`) và tự fallback sang initialize/session cho server cũ. CRUD cấu hình vẫn tenant-scoped; DNS/private-network/redirect bị chặn và header bí mật được mã hóa. Input schema được giữ nguyên, invocation gắn rõ server để không gọi nhầm khi trùng tên. Vì MCP annotations chỉ là hint không đáng tin mặc định, mọi tool cần HITL; `readOnlyHint=true` chỉ được tự chạy nếu Tenant Admin đã bật trust policy cho đúng server. Có OAuth 2.0 authorization-code flow theo từng user (`McpOAuthController`, `McpUserOAuthToken`). Trước production vẫn cần upstream conformance suite và E2E với MCP server đích thật.

## Web client

- Login, chat, document, user administration, memory, API key, widget admin, approvals và MCP settings có UI.
- Notification in-app: `ScheduledTaskWorker` ghi row `Notification` (loại `scheduled` / `scheduled_error`), client đọc qua `GET /api/notifications`.
- Model output được escape trước khi áp dụng formatting giới hạn.
- Web URL chỉ chấp nhận `http`/`https` trước khi tạo link.
- `/widget/chat` là **public embeddable widget**: `POST /api/widget/auth` là `[AllowAnonymous]`, đổi widget key (hash at rest, rotate được qua `WidgetAdminController`) lấy token ngắn hạn, có origin allowlist và quota phút/ngày (`WidgetQuotaService`, mặc định 60/phút và 10.000/ngày). Nginx phục vụ route này với `frame-ancestors` lấy per-tenant từ API qua `auth_request` (`LmKitOmniClient/nginx.conf`), nên **client container phải được rebuild/redeploy** thì widget mới nhúng được. Phần API đã đúng, nhưng **cấu hình nginx hiện đánh rơi `?key=`** nên mọi trang widget đều bị `frame-ancestors 'none'` — xem [known-issues #0](known-issues.md).

## Bảo mật vận hành — hai bất biến về thứ tự middleware

1. **`/metrics` được gác theo prefix, không phải exact path.**
   `UseOpenTelemetryPrometheusScrapingEndpoint()` đăng ký qua `Map("/metrics", …)`, mà `Map` khớp
   theo prefix (`StartsWithSegments`). Guard phải phủ đúng không gian đó —
   `app.UseMetricsEndpointGuard()` (`Infrastructure/Security/MetricsEndpointGuard.cs`) khớp
   `/metrics`, `/metrics/`, `/metrics/<bất kỳ>` và **không** khớp `/metricsdashboard`. Một guard
   so sánh `Path.Equals("/metrics")` từng để lộ toàn bộ Prometheus exposition cho request ẩn danh
   ở mọi path con.
2. **`UseRateLimiter()` chạy SAU `UseAuthorization()`.**
   Policy `widget-chat` partition theo claim `TenantId` và `Items["Widget.RequestOrigin"]`. Widget
   token là auth scheme **không mặc định** nên principal của nó do `AuthorizationMiddleware` tạo,
   không phải `UseAuthentication()`; còn origin thì do `WidgetOriginRequirement` ghi vào
   `HttpContext.Items`. Chạy limiter trước sẽ gộp mọi tenant và mọi origin vào đúng một partition
   `widget:unknown:unknown`. Đánh đổi đã chấp nhận: request bị từ chối sẽ được authorize trước khi
   bị throttle — `LoginPolicy`/`SharePolicy` gác endpoint `[AllowAnonymous]` và partition theo IP
   nên không đổi về bản chất.

`/health/ready` báo **Unhealthy** khi `AiModels:DefaultChat` trỏ vào một model đã đăng ký trong
`AiModels:Models` mà file weights không có trên đĩa; `LmModelManager` log Critical kèm đúng đường
dẫn lúc khởi động. `/health` và `/health/live` không đổi (`/health/live` lọc
`Predicate = _ => false`) — restart process không tạo ra file weights, nên thiếu model không được
phép giết container đang sống. Model id **không** nằm trong registry (catalog id kiểu `qwen3.5:2b`
hoặc URL `https://`) không bị coi là thiếu, vì chỉ có thể biết khi thử load.

`MongoDatabaseService` cache một `MongoClient` cho mỗi connection string (`ConcurrentDictionary`,
sống suốt process) — trước đây mỗi query tạo và vứt một client từ một **singleton**, tức là một
connection pool cộng một SDAM monitor cho mỗi lần gọi.

## Bằng chứng và release gates

Xem:

- [Known issues](known-issues.md)
- [Đánh giá chức năng (ảnh chụp 2026-08-20)](audits/2026-08-20-functional-assessment.md)
- [ADR AI runtime](adr/ADR-001-ai-runtime-upgrade.md)
- [Runbook triển khai](runbooks/deployment.md)

Trước production vẫn phải chạy model/license smoke trên artifact thật, AI golden evaluation, browser E2E có inference model thật, load test và backup/rollback drill. Browser contract E2E/axe và full-stack E2E qua Nginx/API/PostgreSQL/Redis/Qdrant đã chạy trong CI (`.github/workflows/ci.yml`); full-stack hiện chưa gọi model hoặc MCP server thật.
