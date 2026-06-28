# Khả Năng Của AI Agent — OllamaAgent

> Tài liệu này mô tả đầy đủ các khả năng của AI Agent trong hệ thống **OllamaAgent** — một nền tảng AI Agent backend (.NET) + frontend (Angular) tích hợp nhiều công nghệ như RAG, Vision, OCR, Tool Calling, Multi-Agent, và hơn thế nữa.

---

## 📋 Mục Lục

1. [Chat & Hội Thoại Thông Minh](#1-chat--hội-thoại-thông-minh)
2. [Vision & Nhận Dạng Ảnh](#2-vision--nhận-dạng-ảnh)
3. [OCR & Trích Xuất Văn Bản Từ Ảnh/PDF Scan](#3-ocr--trích-xuất-văn-bản-từ-ảnhpdf-scan)
4. [RAG — Retrieval-Augmented Generation](#4-rag--retrieval-augmented-generation)
5. [Phân Tích & Chỉnh Sửa Tài Liệu Office (Word/Excel/PPT)](#5-phân-tích--chỉnh-sửa-tài-liệu-office-wordexcelppt)
6. [Multi-Agent Orchestration](#6-multi-agent-orchestration)
7. [Agentic Planning (ReAct + Task Decomposition)](#7-agentic-planning-react--task-decomposition)
8. [Model Router — Chọn Model Thông Minh](#8-model-router--chọn-model-thông-minh)
9. [Agent Memory & Dreaming — Trí Nhớ Dài Hạn](#9-agent-memory--dreaming--trí-nhớ-dài-hạn)
10. [MCP — Model Context Protocol](#10-mcp--model-context-protocol)
11. [Widget Chat — Nhúng Vào Website Bên Thứ Ba](#11-widget-chat--nhúng-vào-website-bên-thứ-ba)
12. [Tìm Kiếm Web](#12-tìm-kiếm-web)
13. [Xác Thực & Đa Tenant (Multi-Tenant)](#13-xác-thực--đa-tenant-multi-tenant)
14. [Quản Trị Hệ Thống](#14-quản-trị-hệ-thống)
15. [Lưu Trữ & Xử Lý File](#15-lưu-trữ--xử-lý-file)
16. [Thông Báo & Real-time](#16-thông-báo--real-time)
17. [Observability & Giám Sát](#17-observability--giám-sát)
18. [Background Jobs & Tác Vụ Nền](#18-background-jobs--tác-vụ-nền)
19. [Kiểm Toán & Audit Log](#19-kiểm-toán--audit-log)
20. [Embedding & Vector Search](#20-embedding--vector-search)
21. [Tổng Hợp Kiến Trúc Hạ Tầng](#21-tổng-hợp-kiến-trúc-hạ-tầng)

---

## 1. Chat & Hội Thoại Thông Minh

### Mô tả
Hệ thống hỗ trợ chat streaming qua Server-Sent Events (SSE) với đầy đủ tính năng quản lý phiên, lịch sử, và tóm tắt hội thoại.

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Stream chat real-time** | `POST /api/chat/stream` — trả về SSE stream, hỗ trợ tool-calling, reasoning tokens |
| **Quản lý phiên chat** | Tự động tạo/kế thừa `chatSessionId`, phân trang lịch sử |
| **Lịch sử chat** | `GET /api/chat/sessions/{id}/messages` — phân trang, tải 20 message gần nhất |
| **Tóm tắt hội thoại** | Tự động tóm tắt (summarize) khi phiên đủ dài, lưu trong `chat_sessions.conversation_summary` |
| **Context window thông minh** | Chỉ nạp summary + N message gần nhất (không nhét toàn bộ history vào prompt) |
| **Cache Redis** | Cache context session (summary + recent messages) + cache trang đầu history |
| **Xóa phiên chat** | `DELETE /api/chat/sessions/{id}` — cascade xóa toàn bộ tin nhắn |

### 🔗 Endpoints liên quan
- [`ChatEndpoints.cs`](src/API/Endpoints/ChatEndpoints.cs)
- [`StreamChatPipeline.cs`](src/API/Endpoints/StreamChatPipeline.cs)
- [`IChatConversationPersistenceService`](src/API/Application/Abstractions/Chat/IChatConversationPersistenceService.cs)
- [`IChatHistoryCacheService`](src/API/Application/Abstractions/Chat/IChatHistoryCacheService.cs)

---

## 2. Vision & Nhận Dạng Ảnh

### Mô tả
Hệ thống có khả năng nhận dạng và phân tích ảnh thông qua các model vision (multimodal) của LM Studio.

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Nhận dạng ảnh trong chat** | Upload ảnh → system tự động phát hiện và gửi qua vision model |
| **Route thông minh** | `IChatStreamRoutePlanner` quyết định khi nào dùng vision, khi nào OCR |
| **Giới hạn số ảnh** | Cấu hình `MaxVisionImagesPerRequest` (mặc định 4 ảnh) |
| **Tối ưu payload** | Resize ảnh về `MaxLongEdgePixels` (2048px), chọn encoding lossless/lossy |
| **Hỗ trợ lossless/lossy** | Tự động chọn encoding tối ưu dựa trên tỷ lệ nén |

### 🔗 Endpoints & Config
- [`ChatRouting:EnableVisionRouting`](src/API/appsettings.json:14)
- [`ChatImagePayload`](src/API/appsettings.json:82)
- [`IChatStreamRoutePlanner`](src/API/Application/Abstractions/Chat/IChatStreamRoutePlanner.cs)
- [`IVisionAttachmentPayloadBuilder`](src/API/Application/Abstractions/AI/IVisionAttachmentPayloadBuilder.cs)

---

## 3. OCR & Trích Xuất Văn Bản Từ Ảnh/PDF Scan

### Mô tả
Hệ thống có pipeline OCR (Tesseract + Aspose) để đọc chữ từ ảnh, PDF scan, và tài liệu không có text layer.

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **OCR ảnh raster** | Đọc text từ ảnh PNG/JPEG trong chat |
| **PDF scan** | Render từng trang thành ảnh → OCR → ghép text theo thứ tự |
| **PDF có text layer** | Đọc trực tiếp bằng parser (PdfPig) |
| **OCR cleanup bằng AI** | Gọi model LM để sửa lỗi OCR (dấu tiếng Việt, ký tự sai) |
| **Đa ngôn ngữ** | Hỗ trợ `eng+vie` (tiếng Anh + tiếng Việt), auto-detect |
| **OCR trong RAG ingest** | Tự động OCR nếu file scan trước khi chunk + embedding |
| **OCR trong chat stream** | Stream OCR preview text vào `AttachmentContext` cho model |

### 🔗 Endpoints & Config
- [`Ocr` config](src/API/appsettings.json:165)
- [`OcrCleanup` config](src/API/appsettings.json:67)
- [`IOcrService`](src/API/Application/Abstractions/Content/IOcrService.cs)
- [`IOcrTextCleanupService`](src/API/Application/Abstractions/Content/IOcrTextCleanupService.cs)
- [`IOcrPageImageGenerator`](src/API/Application/Abstractions/Content/IOcrPageImageGenerator.cs)

---

## 4. RAG — Retrieval-Augmented Generation

### Mô tả
Hệ thống có pipeline RAG hoàn chỉnh: ingest tài liệu → chunk → embedding → hybrid search (vector + keyword) → rerank → trả lời.

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Ingest tài liệu** | `POST /api/rag/ingest` — upload → parse → chunk → embedding → upsert Qdrant |
| **Hybrid Search** | Kết hợp vector search (Qdrant) + keyword full-text search (PostgreSQL FTS) |
| **Score Fusion** | Normalized score fusion + Rank Fusion (RRF) |
| **Rerank** | MMR selection để giảm trùng lặp + structured intent boost (cell/range/sheet/slide/heading) |
| **Multi-tenant filter** | Filter Qdrant + SQL theo `tenant_id`; điểm global (tenant rỗng) khớp mọi tenant |
| **Cache Redis** | Cache answer RAG theo tenant + session + fingerprint history |
| **RAG trong chat stream** | `enableIndexedKnowledge=true` — embed câu hỏi + hybrid retrieval → nối vào `AttachmentContext` |
| **ChatIndexedKnowledgeRouter** | LLM-based router quyết định có cần retrieval không (tránh gọi embedding vô ích) |
| **Guardrails** | Chỉ trả lời từ context; nếu thiếu ngữ cảnh → từ chối; bỏ qua instruction trong document |

### 🔗 Endpoints & Config
- [`POST /api/rag/ingest`](src/API/Endpoints/RagEndpoints.cs:28)
- [`POST /api/rag/ask`](src/API/Endpoints/RagEndpoints.cs:50)
- [`RagRetrieval` config](src/API/appsettings.json:141)
- [`IRagContextRetriever`](src/API/Application/Abstractions/Rag/IRagContextRetriever.cs)
- [`IAdaptiveRetrievalService`](src/API/Application/Abstractions/Rag/IAdaptiveRetrievalService.cs)
- [`IDocumentIndexingService`](src/API/Application/Abstractions/Rag/IDocumentIndexingService.cs)

---

## 5. Phân Tích & Chỉnh Sửa Tài Liệu Office (Word/Excel/PPT)

### Mô tả
Hệ thống tích hợp **Aspose** + **Semantic Kernel** cho phép AI Agent gọi tool để đọc, phân tích, chỉnh sửa, và tạo tài liệu Office.

### Tính năng cụ thể
#### 📝 Word (.docx)
| Tính năng | Mô tả |
|-----------|-------|
| **Đọc & trích xuất** | Đọc plain text, trích xuất theo section, đọc cấu trúc heading |
| **Tìm & thay thế** | `ReplaceWordTextAsync` — tìm kiếm và thay thế văn bản |
| **Chèn nội dung** | Chèn đoạn văn (`InsertWordParagraph`), bảng (`InsertWordTable`), hình ảnh (`InsertWordImageWithCaption`) |
| **Header/Footer** | Cập nhật header và footer |
| **Template & Merge** | `ApplyWordTemplate`, `MergeWordDocuments`, `MailMergeWord` |
| **So sánh & Track changes** | `CompareWordDocuments`, `TrackWordChanges`, `AcceptRejectWordRevisions` |
| **Mục lục (TOC)** | `AddOrUpdateWordToc` — tự động tạo mục lục |
| **Watermark** | Chèn watermark văn bản |
| **Bảo vệ tài liệu** | `ProtectWordDocument` với password |
| **Chuyển đổi** | `ExportWordToPdf`, `ConvertMarkdownToWord`, `GenerateWordReportFromJson` |
| **Xử lý nâng cao** | Redact (che thông tin nhạy cảm), styling, normalize formatting, comment, split by heading |
| **Tạo mới** | `CreateWordDocumentFromLayoutAsync` — tạo Word từ layout JSON |

#### 📊 Excel (.xls/.xlsx)
| Tính năng | Mô tả |
|-----------|-------|
| **Đọc & kiểm tra** | `InspectExcelWorkbook` — xem cấu trúc workbook |
| **Thao tác cell** | `UpdateExcelCell`, `UpdateExcelRange` — ghi dữ liệu vào cell/range |
| **Sheet** | `AddExcelSheet` — thêm sheet mới |
| **Chart & Pivot** | `CreateExcelChart`, `CreateExcelPivotTable`, `CreateExcelDashboardSheet` |
| **Định dạng có điều kiện** | `ApplyExcelConditionalFormatting` |
| **Data validation** | `ValidateExcelDataRules`, `AddExcelDropdownList` |
| **Import/Export** | `ImportExcelDataFromJson`, `ExportExcelToPdf`, `ExportExcelSelectedSheetsToPdf` |
| **Công thức** | `RecalculateExcelFormulas`, `ApplyExcelFormulaToColumn` |
| **Named Range & Filter** | `CreateExcelNamedRange`, `FilterExcelRange`, `SortExcelRange` |
| **Tạo mới từ dữ liệu** | `CreateExcelWorkbookFromTextAsync`, `CreateExcelWorkbookWithDataAsync`, `CreateExcelWorkbookWithMultipleSheetsAsync` |
| **Tạo Chart từ JSON** | `CreateExcelChartFromJsonAsync` — tự động detect data range |

#### 📽️ PowerPoint (.pptx)
| Tính năng | Mô tả |
|-----------|-------|
| **Đọc & kiểm tra** | `InspectPowerPointDeck` — xem nội dung từng slide |
| **Slide** | `AddPowerPointSlide`, `DuplicatePowerPointSlide`, `ReorderPowerPointSlides`, `DeletePowerPointSlide` |
| **Chỉnh sửa nội dung** | `ReplacePowerPointText`, `UpdatePowerPointTitle`, `UpdatePowerPointFooterAndSlideNumbers` |
| **Hình ảnh & Shapes** | `InsertPowerPointImage`, `ReplacePowerPointImage`, `GroupPowerPointShapes`, `AlignPowerPointShapes` |
| **Chart** | `CreatePowerPointChart`, `AddPowerPointChartFromExcelData` |
| **Slide chuyên dụng** | `CreatePowerPointAgendaSlide`, `CreatePowerPointComparisonSlide`, `CreatePowerPointTimelineSlide` |
| **Speaker Notes** | `AddPowerPointSpeakerNotes` |
| **Theme & Transitions** | `ApplyPowerPointTheme`, `ApplyPowerPointTransitions`, `UsePowerPointSlideMaster` |
| **Export** | `ExportPowerPointToPdf`, `ExportPowerPointSlideToImage` |
| **Tạo mới từ JSON** | `CreatePowerPointFromLayoutAsync`, `GeneratePowerPointDeckFromJsonAsync`, `GeneratePowerPointSlidesFromOutlineAsync` |

### 🔗 Code liên quan
- [`IOfficeDocumentToolService`](src/API/Application/Abstractions/AI/IOfficeDocumentToolService.cs) — ~100+ methods
- [`IAgentKernelFactory`](src/API/Application/Abstractions/AI/IAgentKernelFactory.cs)

---

## 6. Multi-Agent Orchestration

### Mô tả
Hệ thống hỗ trợ **đa tác tử** (multi-agent) với kiến trúc Coordinator: phân tích → phân công → thực thi → phản biện.

### Các Agent chuyên biệt
| Agent Type | Vai trò |
|------------|---------|
| **🔬 Researcher** | Tìm kiếm và thu thập thông tin, dữ liệu |
| **✍️ Writer** | Viết nội dung dựa trên dữ liệu thu thập được |
| **🔍 Critic** | Phản biện, kiểm tra chất lượng, đề xuất cải thiện |

### Luồng xử lý
```
User Query → Orchestrator.AnalyzeAsync()
    ↓
OrchestrationPlan (UseMultiAgent | SubTasks)
    ↓
[Researcher] → [Writer] → [Critic] (streaming)
    ↓
Kết quả hợp nhất → SSE stream
```

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Phân tích truy vấn** | `IAgentOrchestrator.AnalyzeAsync` — quyết định dùng multi-agent hay single ReAct |
| **Stream kết quả** | `IAsyncEnumerable<AgentPlanStep>` — stream từng bước plan-action, plan-observation, plan-reflection |
| **Fallback** | Nếu multi-agent fail, tự động fallback về single chat |
| **Context passing** | `AgentContext` — research data → draft → critique, citations được truyền tuần tự |
| **Cấu hình linh hoạt** | `MultiAgent` config với model riêng cho từng agent |

### 🔗 Code liên quan
- [`IAgentOrchestrator`](src/API/Application/Abstractions/AI/IAgentOrchestrator.cs)
- [`ISpecializedAgent`](src/API/Application/Abstractions/AI/ISpecializedAgent.cs)
- [`MultiAgent` config](src/API/appsettings.json:56)

---

## 7. Agentic Planning (ReAct + Task Decomposition)

### Mô tả
Triển khai **Hermes-style ReAct** (Reason + Act) pattern với khả năng phân rã tác vụ (task decomposition), tự suy luận (reflection), và đầu ra cấu trúc (structured output).

### Các bước trong Agentic Planning
| Step Kind | Mô tả |
|-----------|-------|
| `plan-decomposition` | LLM phân tích query → chia nhỏ thành subtasks |
| `plan-reasoning` | LLM suy luận, quyết định hành động tiếp theo |
| `plan-action` | Tool function đang được gọi (web search, read link, v.v.) |
| `plan-observation` | Kết quả tool trả về |
| `plan-reflection` | Self-critique kiểm tra kết quả đã đạt yêu cầu chưa |
| `plan-content` | Nội dung response cuối cùng (text) |
| `plan-structured` | Response cuối cùng dạng structured JSON |
| `plan-error` | Lỗi xảy ra trong quá trình planning |

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Task Decomposition** | Chia câu hỏi phức tạp thành nhiều bước nhỏ |
| **Reflection** | Tự đánh giá kết quả sau mỗi bước |
| **Structured Output** | Ép LLM trả về JSON schema cho kết quả cuối |
| **Max retries** | Cấu hình `MaxRetriesPerStep`, `MinConfidenceThreshold` |
| **Max steps** | Giới hạn `MaxSteps` (mặc định 8) |
| **Model riêng** | Có thể cấu hình model riêng cho planner |

### 🔗 Code liên quan
- [`IAgenticPlanner`](src/API/Application/Abstractions/AI/IAgenticPlanner.cs)
- [`AgentPlanner` config](src/API/appsettings.json:28)

---

## 8. Model Router — Chọn Model Thông Minh

### Mô tả
Hệ thống có khả năng tự động **phân loại tác vụ** và **chọn model phù hợp nhất** dựa trên độ phức tạp và loại công việc.

### Các loại tác vụ
| Task Type | Khi nào dùng |
|-----------|--------------|
| `chat` | Hội thoại thông thường |
| `reasoning` | Suy luận phức tạp, toán học, logic |
| `vision` | Phân tích ảnh |
| `code` | Sinh code, debug |
| `summarization` | Tóm tắt văn bản dài |
| `classification` | Phân loại intent, routing |

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Phân loại thông minh** | `ClassifyTaskAsync` — LLM phân loại query + heuristic |
| **Chọn model tối ưu** | `SelectModel` — dùng model nhanh cho chat, model mạnh cho reasoning |
| **Tối ưu chi phí** | Tránh dùng model mạnh cho tác vụ đơn giản |
| **Cấu hình linh hoạt** | Config riêng `FastChatModel`, `ReasoningModel`, `VisionModel`, v.v. |

### 🔗 Code liên quan
- [`IModelRouterService`](src/API/Application/Abstractions/AI/IModelRouterService.cs)
- [`ModelRouter` config](src/API/appsettings.json:47)

---

## 9. Agent Memory & Dreaming — Trí Nhớ Dài Hạn

### Mô tả
Hệ thống có cơ chế **"giấc mơ"** (dreaming) cho AI Agent — định kỳ xem xét, dọn dẹp, hợp nhất và sinh insight từ bộ nhớ của agent.

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Lưu trữ memories** | Bảng `AgentMemory` — lưu các ký ức của agent |
| **Định kỳ dreaming** | Cron job (`0 10 */6 * * *` — mỗi 6 giờ) |
| **Prune** | Xóa memories có confidence < `PruneConfidenceThreshold` (0.3) |
| **Merge** | Hợp nhất các memories tương tự nhau |
| **Sinh insights** | Tạo insight mới từ các memories hiện có |
| **Multi-tenant** | `DreamAsync(tenantId)` — mỗi tenant có bộ nhớ riêng |

### 🔗 Code liên quan
- [`IDreamingService`](src/API/Application/Abstractions/AI/IDreamingService.cs)
- [`Dreaming` config](src/API/appsettings.json:171)
- [`AgentMemory` entity](src/API/Domain/Entities/AgentMemory.cs)

---

## 10. MCP — Model Context Protocol

### Mô tả
Hệ thống triển khai **Model Context Protocol (MCP)** — cho phép các AI client bên ngoài như Claude Desktop, Cursor, VS Code Copilot gọi tools của hệ thống.

### Endpoints MCP
| Endpoint | Mô tả |
|----------|-------|
| `GET /mcp/health` | Health check (public) |
| `GET /mcp/tools` | Danh sách tools có sẵn (yêu cầu JWT) |
| `POST /mcp/execute` | Thực thi tool (yêu cầu JWT, tenant isolation) |
| `GET /mcp/config` | Hướng dẫn cấu hình MCP cho tenant (Claude Desktop, Cursor) |

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Multi-tenant isolation** | Mỗi tenant chỉ thấy data của mình qua JWT |
| **Hỗ trợ Claude Desktop** | Trả về config JSON cho Claude Desktop |
| **Hỗ trợ Cursor** | Trả về cấu hình cho Cursor IDE |
| **Công thức curl** | Hướng dẫn gọi MCP bằng curl |

### 🔗 Code liên quan
- [`McpEndpoints.cs`](src/API/Endpoints/McpEndpoints.cs)

---

## 11. Widget Chat — Nhúng Vào Website Bên Thứ Ba

### Mô tả
Hệ thống cung cấp **chat widget nhúng** (embeddable widget) cho phép khách hàng tích hợp AI chat vào website của họ.

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Widget Iframe** | `GET /widget/{widgetApiKey}` — serve trang iframe chat |
| **Widget SDK** | `GET /widget-sdk.js` — JavaScript SDK để nhúng |
| **Xác thực anonymous** | `POST /api/widget/auth` — cấp JWT cho anonymous user qua API key |
| **Quản lý widget** | CRUD widget settings, regenerate API key |
| **Multi-tenant** | Mỗi tenant có widget API key riêng |
| **CORS** | Policy `WidgetCors` — allow any origin |

### 🔗 Code liên quan
- [`WidgetEndpoints.cs`](src/API/Endpoints/WidgetEndpoints.cs)
- [`TenantWidgetSettings` entity](src/API/Domain/Entities/TenantWidgetSettings.cs)
- [`docs/widget-embed-guide.md`](docs/widget-embed-guide.md)

---

## 12. Tìm Kiếm Web

### Mô tả
AI Agent có khả năng tìm kiếm thông tin trên web để trả lời các câu hỏi cần dữ liệu real-time.

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Công cụ tìm kiếm** | DuckDuckGo (mặc định) + SearXNG (tự host) |
| **Content enrichment** | Đọc nội dung trang web đã tìm được |
| **Tool-calling** | Agent tự gọi `web_search` + `read_link` khi cần |
| **Search summary** | Báo cáo kết quả tìm kiếm (số trang đã đọc) |
| **Cite nguồn** | Trích dẫn nguồn dạng `[title](url)` |

### 🔗 Code liên quan
- [`WebSearch` config](src/API/appsettings.json:130)
- [`IWebSearchService`](src/API/Application/Abstractions/Web/IWebSearchService.cs)

---

## 13. Xác Thực & Đa Tenant (Multi-Tenant)

### Mô tả
Hệ thống hỗ trợ **xác thực JWT** đầy đủ với phân quyền và cách ly dữ liệu giữa các tenant.

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Đăng nhập** | `POST /api/auth/login` — nhận JWT access token |
| **Refresh token** | `POST /api/auth/refresh` — làm mới token (lưu Redis) |
| **Đăng xuất** | `POST /api/auth/logout` — thu hồi refresh token |
| **Thông tin user** | `GET /api/auth/me` — thông tin từ JWT claims |
| **Phân quyền** | Roles: `Administrator`, `Member`, `WidgetOrMemberOrAdministrator` |
| **Tenant isolation** | Mọi query đều filter theo `tenant_id` từ JWT |
| **Tenant API Key** | `X-API-KEY` cho cache/admin APIs, có rate limiting |
| **JWT Bootstrap** | Tự động tạo admin mặc định khi khởi chạy lần đầu |

### 🔗 Code liên quan
- [`AuthEndpoints.cs`](src/API/Endpoints/AuthEndpoints.cs)
- [`Jwt` config](src/API/appsettings.json:105)
- [`AuthBootstrap` config](src/API/appsettings.json:112)
- [`ITenantApiKeyService`](src/API/Application/Abstractions/Security/ITenantApiKeyService.cs)

---

## 14. Quản Trị Hệ Thống

### Mô tả
Hệ thống cung cấp đầy đủ API quản trị cho Administrator.

### Tính năng cụ thể
| Tính năng | Endpoint |
|-----------|----------|
| **Quản lý Tenant** | CRUD `/api/admin/tenants` |
| **Quản lý User** | CRUD `/api/admin/users` |
| **API Keys tenant** | Xem & cấp `/api/admin/tenants/{id}/api-keys` |
| **Tài liệu tenant** | Xem tài liệu theo tenant `/api/admin/tenants/{id}/documents` |
| **Job monitoring** | Xem danh sách job, lịch sử chạy, bật/tắt job |
| **Xóa lịch sử job** | DELETE `/api/admin/jobs/{jobId}/occurrences` |
| **Widget settings** | CRUD widget cho từng tenant |

### 🔗 Code liên quan
- [`AdminEndpoints.cs`](src/API/Endpoints/AdminEndpoints.cs)

---

## 15. Lưu Trữ & Xử Lý File

### Mô tả
Hệ thống tích hợp **MinIO** (object storage tương thích S3) để lưu trữ và xử lý file.

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Upload trực tiếp** | `POST /api/files/upload` — upload file lên MinIO |
| **Presigned Upload** | `POST /api/files/presign-upload` — URL upload cho client |
| **Presigned Download** | `GET /api/files/presign-download` — URL download cho client |
| **Bucket tạm** | `chat-temp` — file tạm cho attachments trong chat |
| **Bucket RAG** | `rag-documents` — file gốc cho pipeline RAG |
| **Bảo mật** | Presigned URL có expiry time, tenant isolation |

### 🔗 Code liên quan
- [`FileEndpoints.cs`](src/API/Endpoints/FileEndpoints.cs)
- [`Minio` config](src/API/appsettings.json:96)
- [`IMinioStorageService`](src/API/Application/Abstractions/Storage/IMinioStorageService.cs)

---

## 16. Thông Báo & Real-time

### Mô tả
Hệ thống hỗ trợ thông báo real-time qua **SignalR** và REST API.

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Danh sách thông báo** | `GET /api/notifications` — phân trang |
| **Đếm chưa đọc** | `GET /api/notifications/unread-count` |
| **Đánh dấu đã đọc** | `PUT /api/notifications/{id}/read` |
| **Đọc tất cả** | `PUT /api/notifications/read-all` |
| **Real-time** | SignalR Hub `/hubs/notifications` |

### 🔗 Code liên quan
- [`NotificationEndpoints.cs`](src/API/Endpoints/NotificationEndpoints.cs)
- [`NotificationHub`](src/API/Infrastructure/Hubs/NotificationHub.cs)

---

## 17. Observability & Giám Sát

### Mô tả
Hệ thống được trang bị **OpenTelemetry**, **Prometheus**, và **health checks** để giám sát vận hành.

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Health checks** | `GET /health/live`, `GET /health/ready` — PostgreSQL, Redis, MinIO, LM Studio |
| **Prometheus metrics** | `GET /metrics` — HTTP metrics, RAG metrics |
| **OTLP Exporter** | Gửi traces + metrics đến OpenTelemetry Collector |
| **Metrics agent** | `agent_plan_steps_total`, `agent_tool_calls_total`, `agent_planning_time_ms` |
| **Metrics MCP** | `mcp_tool_calls_total` |
| **Traces** | `Agent.PlanAndExecute`, `Agent.Step.*`, `Agent.ToolCall.*`, `MCP.Execute.*` |
| **Correlation ID** | Mọi request đều có `X-Correlation-Id` |
| **Structured logging** | Logging qua middleware với request info |

### 🔗 Code liên quan
- [`SystemEndpoints.cs`](src/API/Endpoints/SystemEndpoints.cs)
- [`Telemetry` config](src/API/appsettings.json:153)
- [`CorrelationIdMiddleware`](src/API/Infrastructure/Middleware/CorrelationIdMiddleware.cs)
- [`deploy/observability/`](deploy/observability/)

---

## 18. Background Jobs & Tác Vụ Nền

### Mô tả
Hệ thống sử dụng **TickerQ** để quản lý các tác vụ nền (background jobs).

### Các Job hiện tại
| Job | Mô tả |
|-----|-------|
| **IndexPendingDocuments** | Index tài liệu mới ingest vào Qdrant |
| **ReEmbedPendingChunks** | Tạo lại embedding cho chunks đã xử lý |
| **CleanupApplicationData** | Dọn dẹp dữ liệu tạm, audit log cũ |
| **MonitorDependencyHealth** | Kiểm tra health của các dependency |

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Persistence** | Job state lưu qua EF Core |
| **Distributed coordination** | Redis-based heartbeat giữa các node |
| **Max concurrency** | Cấu hình riêng cho từng job |
| **OpenTelemetry** | Instrumentation cho jobs |
| **Monitor** | API xem lịch sử chạy, bật/tắt job |

### 🔗 Code liên quan
- [`TickerQ` config](src/API/appsettings.json:182)

---

## 19. Kiểm Toán & Audit Log

### Mô tả
Hệ thống ghi lại tất cả các thay đổi quan trọng phục vụ kiểm toán và truy vết.

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Audit log** | Lưu các thay đổi dữ liệu quan trọng |
| **Theo dõi thao tác** | Ghi lại user, thời gian, hành động |
| **Phân trang & tìm kiếm** | Tra cứu lịch sử audit |

### 🔗 Code liên quan
- [`IAuditLogService`](src/API/Application/Abstractions/Auditing/IAuditLogService.cs)
- [`AuditLog` entity](src/API/Domain/Entities/AuditLog.cs)

---

## 20. Embedding & Vector Search

### Mô tả
Hệ thống sử dụng **Qdrant** làm vector database và **LM Studio** làm embedding service.

### Tính năng cụ thể
| Tính năng | Mô tả |
|-----------|-------|
| **Embedding model** | `text-embedding-embeddinggemma-300m` (dimensions: 768) |
| **Vector database** | Qdrant với cosine distance |
| **Collection** | `document_chunks` — lưu vector embeddings + payload (tenant_id, embedding_model) |
| **Chunking** | Chia tài liệu thành chunks (size: 1200, overlap: 200) |
| **Metadata** | Mỗi chunk lưu `metadata_json` (section, slide, sheet, v.v.) |
| **Chunking thông minh** | Cấu trúc heading-based cho Word, sheet-based cho Excel, slide-based cho PPT |

### 🔗 Code liên quan
- [`VectorStore` config](src/API/appsettings.json:120)
- [`Chunking` config](src/API/appsettings.json:137)
- [`IVectorStoreService`](src/API/Application/Abstractions/Rag/IVectorStoreService.cs)
- [`ITextChunkingService`](src/API/Application/Abstractions/Text/ITextChunkingService.cs)

---

## 21. Tổng Hợp Kiến Trúc Hạ Tầng

### Công nghệ sử dụng

| Công nghệ | Vai trò |
|-----------|---------|
| **.NET 8/9** | Backend API (ASP.NET Core Minimal API) |
| **Angular** | Frontend admin |
| **PostgreSQL** | Database chính, full-text search, vector extension |
| **Redis** | Cache (RAG, session, history), distributed coordination |
| **MinIO** | Object storage (S3-compatible) |
| **Qdrant** | Vector database |
| **LM Studio** | LLM serving (OpenAI-compatible) |
| **Tesseract** | OCR engine |
| **Aspose** | Office document processing (Word, Excel, PowerPoint) |
| **Semantic Kernel** | AI Orchestration, Tool Calling |
| **MediatR** | CQRS pattern |
| **TickerQ** | Background job scheduling |
| **SignalR** | Real-time notifications |
| **OpenTelemetry** | Observability (traces, metrics) |
| **Prometheus** | Metrics scraping |
| **SearXNG** | Self-hosted search engine |
| **DuckDuckGo** | Web search (default) |
| **Docker** | Containerization |

### Sơ đồ kiến trúc tổng quan

```
┌─────────────────────────────────────────────────────────┐
│                    Frontend (Angular)                    │
└────────────────────────┬────────────────────────────────┘
                         │ HTTP / SSE / SignalR
┌────────────────────────▼────────────────────────────────┐
│              API Gateway (ASP.NET Core)                  │
│  ┌──────────┬──────────┬──────────┬──────────────────┐  │
│  │ Chat     │ RAG      │ Admin    │ MCP              │  │
│  │ Endpoints│ Endpoints│ Endpoints│ Endpoints        │  │
│  └────┬─────┴────┬─────┴────┬─────┴──────────────────┘  │
│       │          │          │                           │
│  ┌────▼──────────▼──────────▼──────────────────────┐    │
│  │           Application Layer (MediatR)            │    │
│  │  ┌──────────┬───────────┬────────────────────┐  │    │
│  │  │ Chat     │ RAG       │ AI Agent           │  │    │
│  │  │ Pipeline │ Pipeline  │ (SK + Multi-Agent) │  │    │
│  │  └──────────┴───────────┴────────────────────┘  │    │
│  └────────────────────┬────────────────────────────┘    │
│                       │                                 │
│  ┌────────────────────▼────────────────────────────┐    │
│  │         Infrastructure Layer                     │    │
│  │  ┌──────┬──────┬──────┬──────┬──────┬────────┐  │    │
│  │  │ EF   │ Redis│ MinIO│Qdrant│ LM   │TickerQ │  │    │
│  │  │ Core │      │      │      │Studio│        │  │    │
│  │  └──────┴──────┴──────┴──────┴──────┴────────┘  │    │
│  └───────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────┘
```

### Liên kết tài liệu tham khảo

- [System Architecture](docs/system-architecture.md)
- [Usage Guide](docs/usage-guide.md)
- [Database Structure](docs/database-structure.md)
- [Business Flows](docs/business-flows.md)
- [Widget Embed Guide](docs/widget-embed-guide.md)
- [Deployment Guide](docs/deployment-ubuntu-swarm-nginx.md)
- [Observability Stack](docs/observability-stack.md)
- [README](../README.md)
