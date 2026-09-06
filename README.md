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
| AI runtime | LM-Kit.NET 2026.9 — inference local, hỗ trợ CUDA 13/12 (Windows + Linux), Vulkan, CPU |

## Chức năng chính

**Chat & Agents**
- Chat streaming (SSE) với lịch sử, chia sẻ link, quản lý phiên
- ReAct agent với tool calling; multi-agent với supervisor điều phối (research, database, content…)
- Human-in-the-loop: các hành động nhạy cảm cần duyệt trước khi chạy
- Agent memory theo user (có bước xác nhận consent)

**Tri thức & tài liệu**
- RAG hybrid search trên Qdrant: query expansion → dense + sparse → RRF → **cross-encoder rerank** (`bge-m3`), trả kết quả kèm citation nguồn
- Upload & xử lý document theo vòng đời (kể cả hàng đợi vector hóa, dead-letter)
- Vision/OCR (phân tích ảnh, trích text), speech STT và TTS (Piper), voice room qua LiveKit (tùy chọn)

**Tools & tích hợp**
- MCP server integration chuẩn 2026-07-28 (SDK chính thức, secrets mã hóa, SSRF/DNS guard)
- Tool sandbox: JavaScript (Jint) và Python (container riêng, giới hạn CPU/RAM/timeout)
- Computer-use (điều khiển máy tính qua screenshot + grounding, off by default)
- Database agent cho SQL Server/Postgres/MySQL/Mongo/SQLite (read-only mặc định)
- Web search qua **SearXNG self-hosted** (mặc định, không cần API key) → Brave/Tavily API (tùy chọn) → fallback DuckDuckGo; web read an toàn SSRF
- Content creation pipeline (dàn ý → viết → biên tập)

**Widget & API**
- Widget chat nhúng công khai: API key theo tenant (rotate, hash at rest), origin allowlist, token ngắn hạn, quota theo phút/ngày
- API key cho tích hợp machine-to-machine (`X-API-Key`)
- Notification in-app (chuông thông báo cho kết quả tác vụ định kỳ)

**Bảo mật**
- Phân tenant xuyên suốt, JWT cookie auth, rate limit AI per-user (local + distributed Redis), output guardrail, audit log có hash, DataProtection key ring mã hóa được

## Chạy trên máy local

### Yêu cầu

- **Docker Desktop** (phân bổ tối thiểu 12 GB RAM cho Docker)
- Không cần cài .NET/Node trên máy — mọi thứ chạy trong container

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
| `AI_DEFAULT_CHAT` / `AI_DEFAULT_VISION` / `AI_DEFAULT_EMBEDDING` / `AI_DEFAULT_RERANKER` | Không | Mặc định đã trỏ hết vào model local: `bonsai` (27B), `glm-ocr`, `bge-m3` (dùng chung cho cả embedding lẫn reranker) |

> Hai file compose: **`docker-compose.yml`** chỉ chứa hạ tầng (Postgres/Qdrant/Redis) cho dev debug trực tiếp; **`docker-compose.prod.yml`** là full stack (Nginx + API + hạ tầng). Tất cả lệnh full-stack bên dưới dùng `-f docker-compose.prod.yml`.

### Bước 2 — Build image (lần đầu)

File compose **pull image có sẵn** từ Docker Hub. Nếu image chưa được publish, build local trước:

```powershell
# Windows:
scripts\build-push.bat --no-push

# Linux/macOS/WSL:
./scripts/build-push.sh --no-push
```

Image API dùng `ubuntu:22.04` + runtime CUDA 12+13 (thư viện `cudart`+`cublas` thôi, không phải cả toolkit) — GPU hoạt động qua driver host; không có GPU thì tự fallback CPU. Nếu image đã có trên Docker Hub (của bạn hoặc team) thì bỏ qua bước này.

### Bước 3 — Chạy stack

```powershell
docker compose -f docker-compose.prod.yml --env-file .env up -d
```

Service `api` khai báo GPU reservation NVIDIA sẵn — chạy được ngay trên máy có GPU, tự fallback Vulkan/CPU trên máy không có.

Stack gồm: `client` (Nginx + Vue) → `api` → PostgreSQL + Qdrant + Redis. EF migration tự chạy lúc API start.

### Bước 4 — Mở và kiểm tra

1. Mở **http://localhost** và đăng nhập bằng tài khoản bootstrap đã tạo ở Bước 1.
2. Kiểm tra nhanh:

```powershell
docker compose -f docker-compose.prod.yml ps                    # các service healthy
curl http://localhost:5032/health    # 200 OK
docker compose -f docker-compose.prod.yml logs -f api           # xem model load + backend LM-Kit (CUDA/Vulkan/CPU)
```

3. Sau khi chạy thử xong: **xóa 3 dòng `BOOTSTRAP_ADMIN_*`** trong `.env` rồi `docker compose -f docker-compose.prod.yml --env-file .env up -d`.

> Compose chạy HTTP thuần cho local nên `AUTH_COOKIE_SECURE=false`. Deploy ra Internet phải có TLS và bật `AUTH_COOKIE_SECURE=true`.

### Dừng / xóa dữ liệu

```powershell
docker compose -f docker-compose.prod.yml down          # dừng, giữ dữ liệu
docker compose -f docker-compose.prod.yml down -v       # dừng + xóa volume (mất DB, Qdrant, cache model)
```

## Model AI local (thư mục `AIModels`)

Model local được đọc từ `/app/AIModels` trong container, bind mount từ host qua `AI_MODELS_HOST_DIR` (mặc định `./LmKitOmniApi/AIModels`) — **không cần download**:

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

Image API build cho `linux-x64`, kèm native backend **CUDA 13, CUDA 12, Vulkan, AVX/AVX2** — tự chọn backend lúc chạy theo thứ tự **CUDA 13 → CUDA 12 → Vulkan → CPU**, một image chạy được trên mọi host.

```bash
docker compose -f docker-compose.prod.yml --env-file .env up -d

# Xác nhận backend đang dùng:
docker compose -f docker-compose.prod.yml logs api | grep -i backend
```

- Linux backend packages **không chứa CUDA runtime** (`libcudart/libcublas`) — image `api` (target `final-slim`) tự cài đúng 2 thư viện đó cho cả CUDA 12 và 13 từ apt repo NVIDIA (~3.8GB/image, ~0.6GB nhỏ hơn bản base đầy đủ và có đủ file backend CUDA).
- ARM64 (Jetson/Grace): publish với `-r linux-arm64`, các package `*.linux-arm64` tự kích hoạt.

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
dotnet run --project LmKitOmniApi        # API: http://localhost:5032

# 3. Frontend (terminal 2):
Set-Location LmKitOmniClient
npm ci
npm run dev                              # UI: http://localhost:5173 (proxy /api và /hubs về 5032)
```

Vite dev server đã proxy `/api` và `/hubs` (WebSocket) về `http://localhost:5032`, và `http://localhost:5173` có sẵn trong CORS allowlist.

## Kiểm thử

```powershell
# Backend (794+ test, có code coverage):
dotnet test .\LmKitOmniApi.Tests\LmKitOmniApi.Tests.csproj -c Release

# Frontend:
Set-Location LmKitOmniClient
npm ci
npm run test:unit
npx playwright install chromium
npm run test:e2e
```

E2E full-stack (dựng stack Docker tách biệt, API + data services thật) — mọi tham số qua env var, không cần file compose riêng:

```bash
BOOTSTRAP_ADMIN_ENABLED=true BOOTSTRAP_ADMIN_EMAIL=e2e-admin@example.test \
BOOTSTRAP_ADMIN_PASSWORD='E2e-Admin-2026!' \
API_HOST_PORT=15032 CLIENT_HOST_PORT=18080 POSTGRES_HOST_PORT=15432 \
QDRANT_HTTP_HOST_PORT=16333 QDRANT_GRPC_HOST_PORT=16334 REDIS_HOST_PORT=16379 \
docker compose -f docker-compose.prod.yml --env-file .env.example -p lmkit-fullstack-e2e up -d --wait --wait-timeout 180
# Playwright cần: E2E_BASE_URL=http://127.0.0.1:18080 (và thông tin admin ở trên)
# Mẹo: chạy ./scripts/build-push.sh --no-push trước để e2e commit hiện tại thay vì tag đã publish.
# Compose prod và dev dùng chung project name — khi chuyển mode, chạy `down` trước để dọn container cũ.

# ... chạy test, rồi dọn:
BOOTSTRAP_ADMIN_ENABLED=true BOOTSTRAP_ADMIN_EMAIL=e2e-admin@example.test \
BOOTSTRAP_ADMIN_PASSWORD='E2e-Admin-2026!' \
API_HOST_PORT=15032 CLIENT_HOST_PORT=18080 POSTGRES_HOST_PORT=15432 \
QDRANT_HTTP_HOST_PORT=16333 QDRANT_GRPC_HOST_PORT=16334 REDIS_HOST_PORT=16379 \
docker compose -f docker-compose.prod.yml --env-file .env.example -p lmkit-fullstack-e2e down -v --remove-orphans
```

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
| `SemaphoreLimits__Chat` | Số luồng inference chat đồng thời (mỗi slot model có limit riêng) |
| `LMKit__LicenseKey` | License thương mại LM-Kit (production) |

Quy trình vận hành/rollback: [deployment runbook](LmKitOmniApi/docs/runbooks/deployment.md). Đánh giá chức năng hiện tại: [audit report](LmKitOmniApi/docs/audits/2026-08-20-functional-assessment.md).

## Ranh giới an toàn AI

- Server quyết định model ID — client không thể truyền model URL vào request.
- Model tải từ xa chỉ qua HTTPS, kiểm tra host/DNS, giới hạn kích thước/thời gian, tải nguyên tử.
- Vector RAG mặc định scope riêng tư theo `tenant + user`.
- Tools đi qua kiểm soát quyền, sandbox, timeout, giới hạn output, audit và HITL.
- Output model được escape trước khi render HTML ở frontend.
- AI endpoint dùng token-bucket rate limit per-user (kèm lớp distributed trên Redis).

Tool mặc định an toàn bật cho ReAct: arithmetic, date/time, JSON, CSV, XML, statistics. Tool ghi đè file không bật mặc định.

## Giới hạn hiện tại

- Toàn bộ 4 slot model (chat/vision/embedding/reranker) mặc định trỏ file local trong `AIModels` — chạy không cần internet. Chỉ khi chọn catalog id hoặc URL thì model mới được tải về volume `models` ở lần dùng đầu.
- Chưa có quota token per-user (hiện chỉ giới hạn số request/phút) và chưa đo chất lượng model bằng eval có model thật trong CI.
- Widget nhúng hoạt động với widget key + origin allowlist; chưa có portal tự đăng ký cho khách hàng.
- MCP config scoped theo tenant-admin, secrets mã hóa; protocol 2026-07-28 kèm fallback cho server cũ.
- Chưa chạy load/chaos test và SLO/dashboard/alert cho production.
