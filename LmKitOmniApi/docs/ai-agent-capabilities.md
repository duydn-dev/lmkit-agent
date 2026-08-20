# Khả năng AI đang được hỗ trợ

Tài liệu này mô tả các đường chạy có thật trong `LmKitOmniApi`. Một chức năng chỉ được liệt kê là hỗ trợ khi có API/runtime tương ứng và được build trong product project.

## Agent runtime

| Khả năng | Hiện trạng | Ghi chú |
|---|---|---|
| Chat có lịch sử | Hỗ trợ | Session theo tenant/user, SSE, giới hạn history và cache. |
| ReAct planning | Hỗ trợ | LM-Kit native agent, vòng lặp hữu hạn, structured tool calls. |
| Multi-agent | Hỗ trợ có điều kiện | Supervisor cùng Research, Analysis và Vision specialists; chưa có quality/load benchmark production. |
| Human approval | Hỗ trợ | Tool có rủi ro phải qua permission, sandbox và approval; quyền được kiểm tra lại khi thực thi. |
| Agent memory | Hỗ trợ | Memory theo tenant/user, semantic recall, retention worker và UI xem/xác nhận/xóa. Fact heuristic không vào prompt trước khi được xác nhận. |
| Graph memory | Không hỗ trợ | Không có graph runtime được đăng ký. Các entity schema cũ không đồng nghĩa với một chức năng product. |

## Công cụ agent

Các LM-Kit Default Tools được bật mặc định đều là read-only, deterministic và không truy cập filesystem/process/environment:

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
- MCP REST-adapter tools được tenant admin cấu hình.

Không có hàng trăm tool Office/PDF/Image tự sinh. Các source placeholder từng ném `NotImplementedException` đã bị xóa; muốn bổ sung tool mới phải có implementation, ownership policy và test riêng.

## RAG và tài liệu

| Khả năng | Hiện trạng |
|---|---|
| Upload và magic-byte validation | Hỗ trợ |
| Markdown conversion/OCR | Hỗ trợ theo model/config |
| Background vectorization | Có atomic claim, lease, retry và failed state |
| Dense + sparse + RRF + reranking | Hỗ trợ |
| Tenant/owner vector ACL | Bắt buộc |
| Citation | File và chunk locator |
| Delete | Xóa vector, file và DB record theo ownership |
| Shared-document ACL | Chưa hỗ trợ |

## Vision, speech và text analysis

- Vision: analyze, classify, OCR và remove-background.
- Speech: transcription và language detection.
- Text: sentiment/NER/PII, classification, language, keywords và embeddings.
- Mọi endpoint AI có authentication, input cap, Redis-backed per-user rate limiting (local fallback) và per-model concurrency gate.
- Production có thể bật model warmup và buộc readiness phải có license/chat model đã load.
- LiveKit hiện chỉ cung cấp media transport ở development profile; chưa phải backend voice-agent production.

## MCP

Hệ thống hiện có REST adapter với discovery `/mcp/tools` và invocation `/mcp/invoke`, tenant-scoped CRUD, SSRF guard và encrypted headers. Đây **không phải** MCP standard transport/capability negotiation. Không dùng tên “MCP chuẩn” cho đến khi có integration test với standard server/client.

## Web client

- Login, chat, document, user administration, memory và MCP settings có UI.
- Model output được escape trước khi áp dụng formatting giới hạn.
- Web URL chỉ chấp nhận `http`/`https` trước khi tạo link.
- `/widget/chat` là compact chat view yêu cầu application session. Nó không phải public embeddable widget, không có anonymous widget JWT, SDK hay public API key flow.

## Những chức năng không được công bố

Các database entity `TenantApiKey`, `TenantWidgetSettings`, `GraphEntity` và `Notification` là schema kế thừa/scaffolding. Chúng không tự tạo thành API hay product capability. UI và tài liệu không được quảng cáo chúng cho đến khi có contract, authorization, lifecycle và test end-to-end.

## Bằng chứng và release gates

Xem:

- [Đánh giá chức năng](audits/2026-08-20-functional-assessment.md)
- [ADR AI runtime](adr/ADR-001-ai-runtime-upgrade.md)
- [Runbook triển khai](runbooks/deployment.md)

Trước production vẫn phải chạy model/license smoke trên artifact thật, AI golden evaluation, browser E2E có inference model thật, load test và backup/rollback drill. Browser contract E2E/axe và full-stack E2E qua Nginx/API/PostgreSQL/Redis/Qdrant đã chạy trong CI; full-stack hiện chưa gọi model hoặc MCP server thật.
