# Kế hoạch đo tốc độ phản hồi thực tế của AI Agent (LmKitOmniApi)

> Mục tiêu: có **con số thực đo trên máy thật** cho từng loại tương tác (chat thường, chat + RAG,
> tool, vision, chạy song song), xác định **nút cổ chai**, và có quy trình A/B kiểm chứng từng tối ưu.
> Kế hoạch bám đúng đường ống hiện có: `ChatController (SSE)` → `StreamChatCommandHandler`
> → `AgentOrchestrator.StreamProcessQueryAsync` → `LmModelManager` (per-model semaphore) → LM-Kit runtime.

## 1. Định nghĩa chỉ số cần đo

| Chỉ số | Ý nghĩa | Cách đo |
|---|---|---|
| **TTFT** (Time To First Token) | Thời gian từ lúc gửi request đến byte/chunk SSE đầu tiên | Parse SSE ở client bench (k6), đo `performance.now()` lúc nhận chunk đầu |
| **TPOT** (Time Per Output Token) | Tốc độ sinh token sau chunk đầu | Tổng thời gian stream / số chunk hoặc token ước lượng |
| **E2E** | Tổng thời gian toàn bộ câu trả lời | Thời điểm đóng stream − thời điểm gửi request |
| **Tool duration** | Thời gian từng tool invocation | Histogram sẵn có `agent_tool_duration_ms` qua `/metrics` |
| **Load/queue time** | Thời gian chờ semaphore `SemaphoreLimits` khi request đồng thời | Chênh lệch TTFT giữa chạy đơn và chạy song song |
| **Cold vs Warm load** | Thời gian nạp model lần đầu vs đã load | Log `LmModelManager` + test `LmModelLiveSmokeTests` có sẵn |

Ngưỡng tham chiếu UX (chat text, tiếng Việt):

| Mức | TTFT | Cảm nhận |
|---|---|---|
| Tốt | < 1.5s | Như ChatGPT nội bộ |
| Chấp nhận | < 4s | Người dùng vẫn chịu được |
| Cần tối ưu | > 4s | Phải fix trước khi mở cho nhiều user |

TPOT: với model 27B Q1_0 chạy GPU, kỳ vọng thực tế ~10–30 tok/s (GPU), ~2–6 tok/s (CPU). Đo ra thấp hơn
hẳn mức này là có bottleneck khác (semaphore, prompt dài, tool chặn).

## 2. Ba tầng đo (từ trong ra ngoài)

### Tầng 0 — Micro benchmark trong test (đã có khung)
- Mở rộng pattern `LmModelLiveSmokeTests.Registry_BonsaiVisionLatencyBenchmark` (Stopwatch + ITestOutputHelper):
  - Chat text ngắn/vừa/dài (đo TTFT bằng stream từng chunk + E2E).
  - Vision đã đo: ảnh nhỏ ~6s, ảnh lớn ~60s (kết quả 2026-09-06, GPU CUDA13).
- Ưu điểm: không cần auth HTTP; nhược: không phản ánh overhead controller/auth/rate-limit.
- Chạy: `dotnet test --filter "FullyQualifiedName~LmModelLiveSmokeTests"` (tự skip khi thiếu model file).

### Tầng 1 — Đo qua HTTP thật với k6 (chính)
- API trả SSE; k6 hỗ trợ stream qua `http` + xử lý từng byte với `response.body` khi `decompress: false`
  hoặc dùng script streaming riêng; thay thế: script Node.js nhỏ dùng `fetch` + ReadableStream nếu k6
  parse SSE không tiện — chọn 1 công cụ, giữ cố định để A/B hợp lệ.
- Script tính: TTFT, E2E, số chunk. N-result ghi CSV (`scenario, iter, ttft_ms, e2e_ms, chunks`).
- Auth: dùng luồng login thật (JWT) của `AuthController`; tạo user bench riêng trong tenant riêng để
  không ảnh hưởng dữ liệu thật và rate-limit partition theo user không đè lên nhau.

### Tầng 2 — Quan sát hệ thống khi bench chạy
- Prometheus scrape `/metrics` (endpoint đã gate role Admin): theo dõi
  `agent_requests_total`, `agent_tool_duration_ms`, `agent_errors_total` trong suốt buổi chạy.
- OTLP trace: đặt `OTEL_EXPORTER_OTLP_ENDPOINT` vào Jaeger/Aspire Dashboard để thấy span từng
  `ReAct.Iteration.N` — xác định vòng lặp nào/tool nào chiếm thời gian.

## 3. Ma trận kịch bản (mỗi kịch bản 20–50 lần, ghi p50/p95/p99)

| # | Kịch bản | Cấu hình bật | Mục đích đo |
|---|---|---|---|
| S1 | Chat thường, câu ngắn (~1 câu) | mặc định | TTFT baseline của Bonsai |
| S2 | Chat thường, câu dài (~300 từ) | mặc định | Độ trễ theo độ dài prompt |
| S3 | Chat + RAG (kèm document đã upload) | RAG pipeline | Chi phí retrieve + rerank |
| S4 | Chat + tool `run_python` | bật CodeInterpreter | Overhead container spin-up (~1–3s) + sinh file |
| S5 | Vision ảnh nhỏ (< 500KB) và ảnh lớn (> 1200px) | mặc định | Đã đo sơ bộ: 6–60s; chuẩn hóa lại |
| S6 | N user song song (1 → 2 → 4 → 8) | mặc định | TTFT tăng do `SemaphoreLimits:Chat=1` |
| S7 | Cold start: restart API → request đầu tiên | WarmupChatModel=false rồi =true | Định lượng giá trị warmup |
| S8 | Deep research / multi-agent delegation | theo luồng | Tổng E2E các vòng ReAct |

Lưu ý môi trường bench:
- **Tắt rate-limit cản trở đo**: nâng `RateLimiting:AiRequestsPerWindow` (mặc định 10/60s) qua
  `appsettings.Bench.json` hoặc env var `RateLimiting__AiRequestsPerWindow=1000` — chỉ môi trường bench.
- Chạy bench khi máy không chạy việc GPU/CPU nặng khác; ghi rõ GPU/CPU/RAM (máy hiện tại 32GB RAM).
- Mỗi kịch bản chạy **sau khi model đã load** (gọi 1 request làm ấm) trừ đúng S7 đo cold start.

## 4. Quy trình thực hiện từng bước

1. **Chuẩn bị dữ liệu**: 50 câu hỏi tiếng Việt thật (10 ngắn, 30 vừa, 10 dài); 4 ảnh (2 nhỏ, 2 lớn);
   2 document cho RAG. Đặt trong `LmKitOmniApi.Tests/bench-fixtures/` (commit câu hỏi, không commit ảnh lớn).
2. **Chuẩn bị môi trường**: `dotnet run --project LmKitOmniApi` với profile Bench; xác nhận `/health/ready`.
3. **Chạy S1–S5 tuần tự** (single-user), ghi CSV từng file: `results-s1.csv`…
4. **Chạy S6 tăng dần**: giữ nguyên dataset, chỉ tăng concurrency; ghi TTFT p95 tại mỗi mức.
5. **Chạy S7 hai lần**: lần 1 `WarmupChatModel=false`, lần 2 `=true`, so TTFT request đầu.
6. **Thu số liệu Prometheus** trong toàn bộ buổi chạy (export snapshot JSON trước/sau).
7. **Phân tích**: script Python/Excel vẽ box-plot TTFT/E2E từng kịch bản; bảng so ngưỡng mục 1.
8. **Kết luận + backlog tối ưu** theo playbook mục 5, mỗi mục tối ưu A/B lại đúng kịch bản tương ứng.

## 5. Playbook nút cổ chai dự kiến → tối ưu

| Triệu chứng khi đo | Nguyên nhân nghiêm | Tối ưu |
|---|---|---|
| TTFT request đầu sau restart ~40–50s | Cold model load (Bonsai đo được 47s) | Bật `AiModels:WarmupChatModel=true`; cân nhắc `RequireChatModelReady` cho readiness |
| S6: TTFT p95 tăng vọt từ 2 user trở lên | `SemaphoreLimits:Chat=1` xếp hàng | Tăng limit nếu VRAM/RAM cho phép; hoặc model nhỏ hơn cho chat nhẹ |
| S5 ảnh lớn 60s, ảnh nhỏ 6s | Vision token tỉ lệ diện tích ảnh | Downscale ≤1024px trước khi tạo Attachment (sửa handler/bench lại) |
| S3 chậm hơn S1 nhiều | Rerank + embedding lần đầu | Kiểm tra model rerank đã load (warmup thêm embedding/reranker) |
| E2E dài dù TTFT nhanh | Model sinh dài quá (verbose) | Giới hạn max tokens/định dạng trả lời ngắn trong system prompt |
| `agent_tool_duration_ms` cao ở run_python | Container spin-up | Giữ image nhỏ (python:3.12-slim), cân nhắc warm container |
| TPOT thấp bất thường (~<2 tok/s) | GPU rơi về CPU / quant Q1_0 chậm trên vài layer | Kiểm tra log backend Cuda13; thử quant cao hơn nếu là giới hạn kiến trúc |

## 6. Sản phẩm bàn giao

- Thư mục `bench/` mới: script k6/Node + dataset câu hỏi + script tổng hợp CSV → report.
- Báo cáo `docs/performance-benchmark-results.md` ghi môi trường, ngày đo, bảng p50/p95/p99 từng kịch bản.
- (Tùy chọn) Grafana dashboard JSON cho `agent_tool_duration_ms` để theo dõi lâu dài.

## Kết quả đo sẵn có (làm mốc so sánh, 2026-09-06, GPU CUDA13, Bonsai-27B Q1_0)

- Cold load: ~47s; warm load (model trong RAM cache): ~1.6s.
- Vision: `dog1.jpg` (~0.5MB) 6.0s; `cat1.jpg` (1200×1200) 60.8s; lặp lại 33.5s (sai số 1 lần đo lớn).
- Độ chính xác nhận diện: 3/3 đúng ("dog", "cat").
