# LM-Kit Omni Agent

Nền tảng AI agent **local-first, multi-tenant** xây bằng ASP.NET Core + Vue, chạy inference hoàn toàn trên máy bạn qua **LM-Kit.NET** (không gọi API AI cloud nào).

```text
Vue/Nginx ──cookie JWT──> ASP.NET Core API
                              │
            ┌─────────────────┼───────────────────┐
            │                 │                   │
       PostgreSQL          Qdrant              Redis
    users/chat/audit    RAG + memory      cache/revocation
                              │
                        LM-Kit.NET models
                 ReAct / supervisor / vision / speech
```

| Thành phần | Công nghệ |
|---|---|
| Backend API | ASP.NET Core (.NET 10), MediatR/CQRS, EF Core, nullable enabled |
| Frontend | Vue 3 + PrimeVue 4 + Tailwind 4 + Pinia + Vite |
| Dữ liệu | PostgreSQL 18 (users/chat/audit), Qdrant (RAG/memory vectors), Redis (cache/revocation/rate-limit) |
| AI runtime | LM-Kit.NET 2026.9 — inference local. Backend native: CUDA 13 trên Windows; CUDA 13 + CUDA 12 khi publish `linux-x64`/`linux-arm64`; CPU ở mọi nơi |

## Chức năng chính

Mọi thứ trong mục này **bật sẵn** trừ khi ghi rõ. Các chức năng tắt mặc định nằm ở [mục riêng bên dưới](#chức-năng-tắt-mặc-định).

**Chat & Agents**
- Chat streaming (SSE) với lịch sử, chia sẻ link, quản lý phiên
- ReAct agent với tool calling (tối đa 5 vòng lặp); multi-agent với supervisor điều phối ba specialist: research, analysis, vision
- Human-in-the-loop: các hành động nhạy cảm cần duyệt trước khi chạy
- Agent memory theo user — fact do agent tự suy luận phải được user xác nhận thì mới được recall
- Content creation pipeline (dàn ý → viết → biên tập), có UI

**Tri thức & tài liệu**
- RAG hybrid search trên Qdrant: query expansion → dense + sparse → RRF → **cross-encoder rerank** (`bge-m3`), trả kết quả kèm citation nguồn
- Upload & xử lý document theo vòng đời, có hàng đợi vector hóa: claim atomic, retry tối đa 3 lần, sau đó chuyển trạng thái `Failed`
- Vision/OCR (phân tích ảnh, trích text) và speech-to-text

**Tools & tích hợp**
- MCP server integration chuẩn 2026-07-28 (SDK chính thức, secrets mã hóa, SSRF/DNS guard, OAuth per-user)
- Tool sandbox JavaScript (Jint) — bật sẵn
- Web search: **SearXNG self-hosted** (mặc định, không cần API key) → Brave → Tavily (đều tùy chọn, cần API key) → fallback DuckDuckGo scraping

**Widget & API**
- Widget chat nhúng công khai: widget key theo tenant (rotate, hash at rest), origin allowlist, token ngắn hạn, quota theo phút/ngày. **Hiện chưa nhúng được thật**: nginx đánh rơi `?key=` ở subrequest lấy frame-policy nên mọi trang widget đều nhận `frame-ancestors 'none'` — xem [known-issues #0](LmKitOmniApi/docs/known-issues.md)
- API key cho tích hợp machine-to-machine (header `X-Api-Key`, lưu dưới dạng hash SHA-256)
- Notification in-app (chuông thông báo cho kết quả tác vụ định kỳ)

**Bảo mật**
- Phân tenant xuyên suốt, JWT cookie auth, rate limit AI per-user (local + distributed Redis), output guardrail, DataProtection key ring mã hóa được
- Audit log lưu **hash SHA-256 của tool arguments** thay vì nội dung thô (không phải hash-chain chống sửa)

### Chức năng tắt mặc định

Đã có code và test, nhưng `Enabled: false` trong `appsettings.json` — endpoint trả `501` cho tới khi bật:

| Chức năng | Khóa cấu hình | Cần thêm |
|---|---|---|
| Python code interpreter (container riêng, giới hạn CPU/RAM/timeout) | `CodeInterpreter:Python:Enabled` | `CodeInterpreter:Python:Image` |
| Browser fetch tool | `BrowserTool:Enabled` | `BrowserTool:Image` |
| Computer-use (điều khiển máy tính qua screenshot + grounding) | `ComputerUse:Enabled` | `ComputerUse:Image` |
| Database agent (SQL Server, PostgreSQL, MySQL, Oracle, SQLite, MongoDB — read-only) | `DatabaseAgent:Enabled` | `DatabaseAgent:AllowedHosts` |
| Web read (fetch & đọc URL, có guard SSRF) | `WebRead:Enabled` | — |
| Document tools (PDF form/redaction) | `DocumentTools:Enabled` | — |
| LoRA adapter hot-swap | `Lora:Enabled` | — |
| Grounding eval / fine-tuning cho computer-use | `GroundingEval:Enabled`, `GroundingTraining:Enabled` | — |
| Chat reasoning | `ChatReasoning:Enabled` | — |
| Text-to-speech (Piper) | `Voice:TtsEnabled` | `Voice:PiperExecutablePath` + voice model |
| Voice room agent qua LiveKit | `Voice:LiveAgentEnabled` | LiveKit URL/key/secret **và** `Voice:AgentTenantId` + `Voice:AgentUserId` |

Chi tiết từng mục: [ai-agent-capabilities.md](LmKitOmniApi/docs/ai-agent-capabilities.md#chức-năng-opt-in).

## Chạy trên máy local

### Yêu cầu

- **Docker Desktop** (phân bổ tối thiểu 12 GB RAM cho Docker)
- Không cần cài .NET/Node trên máy — mọi thứ chạy trong container

### File compose

Repo có **đúng một** file compose cho production: **`docker-compose.prod.yml`**. Nó phục vụ hai trong ba chế độ:

| Chế độ | Lệnh | Dùng khi |
|---|---|---|
| 1. Hạ tầng cho dev | `docker compose --env-file .env up -d` (`docker-compose.yml`) | Chạy API/FE trực tiếp trên host để debug; compose chỉ dựng Postgres/Qdrant/Redis/SearXNG |
| 2. Full stack production | `docker compose -f docker-compose.prod.yml --env-file .env up -d --wait` | Chạy thật: Nginx + API + hạ tầng |
| 3. Full-stack e2e | `./scripts/e2e-fullstack.sh up` | CI và chạy thử cục bộ — **vẫn là `docker-compose.prod.yml`**, chỉ khác ở env var (đổi host port, bật bootstrap admin, không xin GPU) và `-p lmkit-fullstack-e2e` |

`-f docker-compose.prod.yml` ở chế độ 2 **không phải tùy chọn**: thiếu nó, Docker đọc `docker-compose.yml` — file không có `api` lẫn `client`, nên không có gì phục vụ cả.

Chế độ 3 **không** có file compose riêng, và cũng sẽ không có: mọi khác biệt đi qua env var trên đúng file prod đó. Header của `docker-compose.yml` mô tả cả ba chế độ.

### Bước 1 — Tạo file `.env`

```powershell
Copy-Item .env.example .env
```

Sửa các giá trị bắt buộc trong `.env`:

| Biến | Bắt buộc | Ghi chú |
|---|---|---|
| `POSTGRES_PASSWORD` | ✅ | Mật khẩu PostgreSQL |
| `REDIS_PASSWORD` | ✅ | Không chứa dấu phẩy |
| `JWT_SECRET_KEY` | ✅ | Tối thiểu 32 ký tự |
| `BOOTSTRAP_ADMIN_ENABLED=true` + `BOOTSTRAP_ADMIN_EMAIL` + `BOOTSTRAP_ADMIN_PASSWORD` | Lần đầu | Tạo tài khoản admin lúc khởi động (không có admin mặc định). Đăng nhập xong nên xóa 3 dòng này và restart |
| `AI_DEFAULT_CHAT` / `AI_DEFAULT_VISION` / `AI_DEFAULT_EMBEDDING` / `AI_DEFAULT_RERANKER` | Không | Mặc định đã trỏ hết vào model local: `bonsai`, `glm-ocr`, `bge-m3` (dùng chung cho cả embedding lẫn reranker) |

### Bước 2 — Build image (lần đầu)

File compose **pull image có sẵn** từ Docker Hub. Nếu image chưa được publish, build local trước:

```powershell
# Windows:
scripts\build-push.bat --no-push

# Linux/macOS/WSL:
./scripts/build-push.sh --no-push
```

Target mặc định là `final-slim`: `ubuntu:22.04` + ASP.NET 10 runtime + thư viện CUDA runtime (`cudart` + `cublas`, cả thế hệ 12 và 13) mà backend LM-Kit `dlopen` — không phải cả toolkit. Driver (`libcuda.so.1`) do nvidia-container-toolkit của host inject. Nếu image đã có trên Docker Hub (của bạn hoặc team) thì bỏ qua bước này.

### Bước 3 — Đặt model vào `AIModels`

**Repo không kèm file model** (`AIModels/` bị gitignore) và không có script download. Nếu chưa có model, `/health/ready` sẽ báo `Unhealthy` và tin nhắn chat đầu tiên sẽ lỗi — xem [Model AI local](#model-ai-local-thư-mục-aimodels).

### Bước 4 — Chạy stack

```powershell
docker compose -f docker-compose.prod.yml --env-file .env up -d --wait
```

Service `api` xin GPU qua `API_GPU_COUNT` (bỏ trống hoặc `all` = mọi GPU). **Host không có GPU/nvidia-container-toolkit phải đặt `API_GPU_COUNT=0`**, nếu không container không start được. Khi container đã chạy, LM-Kit tự chọn backend theo thứ tự **CUDA 13 → CUDA 12 → CPU** (Vulkan không dùng được trong container vì image không có `libvulkan`).

Stack gồm: `client` (Nginx + Vue) → `api` → PostgreSQL + Qdrant + Redis + SearXNG. EF migration tự chạy lúc API start.

### Bước 5 — Mở và kiểm tra

1. Mở **http://localhost** và đăng nhập bằng tài khoản bootstrap đã tạo ở Bước 1.
2. Kiểm tra nhanh:

```powershell
docker compose -f docker-compose.prod.yml ps                 # mọi service healthy
curl http://localhost:5032/health/live                       # 200 — process sống
curl http://localhost:5032/health/ready                      # 503 khi thiếu file chat model
docker compose -f docker-compose.prod.yml logs -f api        # xem model load + backend LM-Kit
```

`/health` và `/health/ready` trả **503 trên một checkout sạch** vì chưa có file chat model — đó là hành vi đúng, không phải lỗi. Vì vậy healthcheck của container dùng `/health/live`.

3. Sau khi chạy thử xong: **xóa 3 dòng `BOOTSTRAP_ADMIN_*`** trong `.env` rồi `docker compose -f docker-compose.prod.yml --env-file .env up -d --wait`.

> Compose chạy HTTP thuần cho local nên `AUTH_COOKIE_SECURE=false`. Deploy ra Internet phải có TLS và bật `AUTH_COOKIE_SECURE=true`.

### Dừng / xóa dữ liệu

```powershell
docker compose -f docker-compose.prod.yml down          # dừng, giữ dữ liệu
docker compose -f docker-compose.prod.yml down -v       # dừng + xóa volume (mất DB, Qdrant, cache model)
```

## Model AI local (thư mục `AIModels`)

Model local được đọc từ `/app/AIModels` trong container, bind mount từ host qua `AI_MODELS_HOST_DIR` (mặc định `./LmKitOmniApi/AIModels`):

1. Copy file model (`.gguf`/`.lmk`) vào thư mục `AIModels`:
   ```bash
   scp -r models/ user@server:/opt/lmkit/LmKitOmniApi/AIModels/
   ```
2. Tham chiếu theo 1 trong 2 cách:
   - Đăng ký entry trong `AiModels:Models` (appsettings.json) với `"Path"` tính tương đối từ `/app/AIModels`, rồi đặt `AiModels__DefaultChat=<key>` — cách mặc định đang dùng (`bonsai`, `glm-ocr`, `bge-m3`).
   - Hoặc trỏ thẳng đường dẫn trong container: `AI_DEFAULT_CHAT=/app/AIModels/<file>.gguf`.
3. Restart API để áp dụng cho mọi slot: `docker compose -f docker-compose.prod.yml --env-file .env up -d api`.

Model tải từ xa (HTTP(S)/catalog id) lưu vào volume `models` (`/app/Models`); đặt `AI_MODELS_CACHE_HOST_DIR=<thư-mục>` để bind mount ra host nếu muốn giữ qua các lần deploy. Download chỉ qua HTTPS, kiểm tra host tin cậy, giới hạn kích thước và thời gian.

Muốn warm-up chat model lúc khởi động (tránh chờ load ở chat đầu tiên): `AI_WARMUP_CHAT_MODEL=true`.

## Triển khai Ubuntu server

Image API build cho `linux-x64`, kèm native backend **CUDA 13 và CUDA 12** — tự chọn theo thứ tự **CUDA 13 → CUDA 12 → CPU**, một image chạy được trên mọi host.

```bash
docker compose -f docker-compose.prod.yml --env-file .env up -d --wait

# Xác nhận backend đang dùng:
docker compose -f docker-compose.prod.yml logs api | grep -i backend
```

- Linux backend packages **không chứa CUDA runtime** (`libcudart`/`libcublas`) — image `api` (target `final-slim`) tự cài đúng 2 thư viện đó cho cả CUDA 12 và 13 từ apt repo NVIDIA. Bản dùng base `nvidia/cuda` đầy đủ vẫn build được bằng `API_TARGET=final`.
- ARM64 (Jetson/Grace): publish với `-p:RuntimeIdentifier=linux-arm64`, các package `*.linux-arm64` tự kích hoạt.

## Build & push images (Docker Hub)

```powershell
# Windows:
scripts\build-push.bat                       # build + push 2 image, tag latest + git sha
scripts\build-push.bat --tag v1.2.0          # tag tùy chỉnh
scripts\build-push.bat api                   # chỉ image API
scripts\build-push.bat --no-push             # build local, không push
```

```bash
# Linux/macOS/WSL:
./scripts/build-push.sh
./scripts/build-push.sh --tag v1.2.0 api
```

Cần `docker login` trước. Đổi namespace bằng `DOCKER_USER`. Sau khi push, máy deployment chỉ cần:

```bash
docker compose -f docker-compose.prod.yml --env-file .env pull && docker compose -f docker-compose.prod.yml --env-file .env up -d
```

## Chạy dev trực tiếp (hot-reload)

Khi cần sửa code FE/BE, thay vì chạy toàn bộ trong container:

```powershell
# 1. Chỉ dựng hạ tầng:
docker compose up -d

# 2. Backend (terminal 1) — .env KHÔNG được dotnet run tự đọc; set env trước:
$env:PostgreSql="Server=localhost;Port=5432;Database=LmKitAgent;Username=postgres;Password=<POSTGRES_PASSWORD>;"
# Hoặc dùng key chuẩn: $env:ConnectionStrings__PostgreSql=$env:PostgreSql
$env:ConnectionStrings__Redis="localhost:6379,password=<REDIS_PASSWORD>"
$env:VectorStore__BaseUrl="http://localhost:6334"
$env:JwtSettings__SecretKey="<JWT_SECRET_KEY>"
$env:Database__ApplyMigrations="true"
$env:WebSearch__Searx__BaseUrl="http://localhost:8888"   # tùy chọn: bật web search ở chế độ dev
dotnet run --project LmKitOmniApi        # API: http://localhost:5032

# 3. Frontend (terminal 2):
Set-Location LmKitOmniClient
npm ci
npm run dev                              # UI: http://localhost:5173 (proxy /api về 5032)
```

`http://localhost:5173` có sẵn trong CORS allowlist của `appsettings.json`. Chỉ compose mới set `WebSearch__Searx__BaseUrl`, nên chạy `dotnet run` trần thì web search báo "not configured" cho tới khi bạn tự set biến đó.

## Kiểm thử

```powershell
# Backend (có code coverage trong CI):
dotnet test .\LmKitOmniApi.Tests\LmKitOmniApi.Tests.csproj -c Release

# Frontend:
Set-Location LmKitOmniClient
npm ci
npm run test:unit
npx playwright install chromium
npm run test:e2e
```

Test dùng database/engine thật (Testcontainers, cần Docker) nằm ở project riêng và **không** nằm trong suite mặc định — xem [LmKitOmniApi.IntegrationTests/README.md](LmKitOmniApi.IntegrationTests/README.md).

### E2E full-stack

Dựng nguyên stack Docker tách biệt (API + data services thật) rồi chạy Playwright qua Nginx. Mọi tham số đi qua env var trên chính `docker-compose.prod.yml`, với `-p lmkit-fullstack-e2e` để không đụng vào stack thường:

```bash
./scripts/build-push.sh --no-push api client   # gate chạy image local, build trước
./scripts/e2e-fullstack.sh up                  # dựng stack, chờ healthy
./scripts/e2e-fullstack.sh test                # Playwright vào http://127.0.0.1:18080
./scripts/e2e-fullstack.sh down                # dừng; chỉ xóa volume của project e2e
```

```powershell
scripts\build-push.bat --no-push api client
scripts\e2e-fullstack.bat up
scripts\e2e-fullstack.bat test
scripts\e2e-fullstack.bat down
```

`./scripts/e2e-fullstack.sh ci` = `up` + `test` + `down` (`down` luôn chạy). `--help` liệt kê mọi override — port, thông tin admin, wait timeout, `E2E_GPU_COUNT`. Mặc định: client `18080`, api `15032`, postgres `15432`, qdrant `16333`/`16334`, redis `16379`, project `lmkit-fullstack-e2e`, **không xin GPU**.

Script là **nguồn duy nhất** của bộ env var này — đừng chép lại chúng vào tài liệu hay CI, vì đó chính là cách README và runbook trôi lệch khỏi nhau lần trước. `.github/workflows/ci.yml` (job `fullstack-e2e`) gọi đúng script này.

## Cấu hình quan trọng

| Thiết lập | Ý nghĩa |
|---|---|
| `JwtSettings__SecretKey` | Khóa ký JWT, tối thiểu 32 bytes |
| `REDIS_PASSWORD` | Mật khẩu Redis dùng chung cho server và connection string của API (không chứa dấu phẩy) |
| `AuthCookies__Secure` | Phải là `true` khi chạy HTTPS production |
| `Database__ApplyMigrations` | Tự chạy EF migration khi API start |
| `DataProtection__KeyPath` | Vị trí persistent key ring cho mã hóa approval/MCP header |
| `DataProtection__CertificatePath` | Tùy chọn: chứng chỉ PKCS#12 mã hóa key ring at rest |
| `AiModels__DefaultChat` | Model ID do server kiểm soát (chat/vision/embedding/reranker có slot riêng) |
| `AI_MODELS_HOST_DIR` | Thư mục host bind mount vào `/app/AIModels` (model local) |
| `AI_MODELS_CACHE_HOST_DIR` | Tùy chọn: thư mục host cho cache download `/app/Models` |
| `VectorStore__ApiKey` | Tùy chọn: api-key gửi kèm khi Qdrant có xác thực (`VectorStore__BaseUrl` hỗ trợ `https://`) |
| `SemaphoreLimits__Chat` | Số luồng inference chat đồng thời (mỗi slot model có limit riêng) |
| `LMKit__LicenseKey` | License thương mại LM-Kit (production) |

Tài liệu chi tiết:

- [Khả năng AI đang được hỗ trợ](LmKitOmniApi/docs/ai-agent-capabilities.md) — hiện trạng từng chức năng, bất biến vận hành
- [Known issues](LmKitOmniApi/docs/known-issues.md) — defect đã chẩn đoán nhưng chưa sửa
- [Deployment runbook](LmKitOmniApi/docs/runbooks/deployment.md) — quy trình vận hành/rollback
- [Đánh giá chức năng (ảnh chụp 2026-08-20)](LmKitOmniApi/docs/audits/2026-08-20-functional-assessment.md) — dated snapshot, không phải hiện trạng

## Ranh giới an toàn AI

- Server quyết định model ID — client không thể truyền model URL vào request.
- Model tải từ xa chỉ qua HTTPS, kiểm tra host/DNS, giới hạn kích thước/thời gian, tải nguyên tử.
- Vector RAG mặc định scope riêng tư theo `tenant + user`.
- Tools đi qua kiểm soát quyền, sandbox, timeout, giới hạn output, audit và HITL.
- Output model được escape trước khi render HTML ở frontend.
- AI endpoint dùng token-bucket rate limit per-user (kèm lớp distributed trên Redis).
- `/metrics` chỉ Admin đọc được, và guard phủ toàn bộ subtree `/metrics/*` chứ không chỉ path chính xác.

Tool mặc định an toàn bật cho ReAct: arithmetic, date/time, JSON, CSV, XML, statistics. Tool ghi đè file không bật mặc định.

## Giới hạn hiện tại

- Toàn bộ 4 slot model (chat/vision/embedding/reranker) mặc định trỏ file local trong `AIModels`, nhưng **repo không kèm file nào** — phải tự đặt vào. Chỉ khi chọn catalog id hoặc URL thì model mới được tải về volume `models` ở lần dùng đầu.
- Output chat đi qua output guardrail trước khi phát, nên phần đầu câu trả lời bị giữ lại một khoảng rồi mới stream — không phải nhỏ giọt từng token ngay từ đầu.
- Chưa có quota token per-user (hiện chỉ giới hạn số request/phút) và chưa đo chất lượng model bằng eval có model thật trong CI.
- Widget: API/quota/origin-allowlist đã xong nhưng **chưa nhúng được vào site khác** vì lỗi nginx nói ở trên; cũng chưa có portal tự đăng ký cho khách hàng.
- Voice room agent chỉ phục vụ **một** user đã cấu hình sẵn; phục vụ nhiều user cùng lúc cần một room dispatcher chưa được xây.
- Database agent hỗ trợ Oracle ở mức code + unit test, nhưng chưa có live container test như các engine khác.
- Chưa chạy load/chaos test và chưa có SLO/dashboard/alert cho production.
- Các defect đã chẩn đoán nhưng chưa sửa: [known-issues.md](LmKitOmniApi/docs/known-issues.md).
