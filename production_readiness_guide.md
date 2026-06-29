# Hướng dẫn Nâng Cấp Hệ Thống Lên Production (Production Readiness Guide)

Tài liệu này tổng hợp các hạng mục cần thực hiện tiếp theo về mặt hạ tầng, bảo mật, và cấu hình phân tán để đưa hệ thống **LmKit Omni AI Agent Platform** vận hành an toàn và ổn định trên môi trường Production thực tế.

> [!CAUTION]
> Dự án hiện đang target `net10.0` nhưng Dockerfile vẫn sử dụng image `dotnet/sdk:8.0` và `dotnet/aspnet:8.0`. Ngoài ra, `appsettings.json` đang chứa **mật khẩu PostgreSQL thật** (`1Qaz2wsx`) và **JWT Secret Key hardcoded** — cần xử lý khẩn cấp trước khi push lên bất kỳ repo công khai nào.

---

## 📋 Danh sách hạng mục cần nâng cấp

### 1. 🔴 Sửa lỗi Critical: Dockerfile không khớp .NET version (Độ ưu tiên: Khẩn cấp)
*   **Hiện trạng:** Project `.csproj` target `net10.0`, nhưng Dockerfile đang dùng `mcr.microsoft.com/dotnet/sdk:8.0` và `mcr.microsoft.com/dotnet/aspnet:8.0`.
*   **Hậu quả:** Container sẽ **không thể build** được ứng dụng do SDK không tương thích.
*   **Sửa:**
    ```dockerfile
    # Build stage
    FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
    # ...
    # Runtime stage
    FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
    ```

### 2. 🔴 Secret Keys lộ trong source code (Độ ưu tiên: Khẩn cấp)
*   **Hiện trạng:** File `appsettings.json` chứa trực tiếp:
    *   PostgreSQL password: `1Qaz2wsx`
    *   JWT SecretKey: `ThisIsAVerySecureAndLongSecretKeyForHermesAgent2026`
    *   IP nội bộ: `192.168.123.30`
*   **Rủi ro:** Bất kỳ ai có quyền đọc repo đều có thể truy cập database và giả mạo JWT token.
*   **Giải pháp:**
    1.  Tạo file `appsettings.Production.json` (không commit lên git, thêm vào `.gitignore`).
    2.  Sử dụng **Environment Variables** khi chạy Docker/Kubernetes. ASP.NET Core tự động ánh xạ cấu hình dạng:
        ```
        PostgreSql=Server=...;Password=REAL_PASSWORD;
        JwtSettings__SecretKey=REAL_SECRET
        ```
    3.  Trên môi trường Cloud: sử dụng **Azure Key Vault**, **AWS Secrets Manager**, hoặc **HashiCorp Vault**.
    4.  Thay giá trị trong `appsettings.json` bằng placeholder:
        ```json
        "PostgreSql": "Server=localhost;Port=5432;Database=LmKitAgent;Username=postgres;Password=CHANGE_ME;",
        "JwtSettings": {
          "SecretKey": "CHANGE_ME_USE_ENV_VAR_IN_PRODUCTION"
        }
        ```

### 3. 🔴 Database Migration dùng `EnsureCreated()` (Độ ưu tiên: Khẩn cấp)
*   **Hiện trạng:** `Program.cs` sử dụng `dbContext.Database.EnsureCreated()` — phương thức này **không hỗ trợ migration** (nếu schema thay đổi sau lần chạy đầu, nó sẽ không cập nhật bảng mới/cột mới).
*   **Rủi ro:** Mất dữ liệu hoặc schema lỗi thời khi nâng cấp ứng dụng.
*   **Giải pháp:**
    ```csharp
    // Thay thế:
    // dbContext.Database.EnsureCreated();
    
    // Bằng:
    dbContext.Database.Migrate();
    ```
    Và bắt đầu sử dụng EF Core Migrations:
    ```bash
    dotnet ef migrations add InitialCreate
    dotnet ef database update
    ```

### 4. 🟡 Phân tán trạng thái Resilience & Circuit Breaker (Độ ưu tiên: Cao)
*   **Hiện trạng:** Trạng thái đóng/mở mạch (`_circuitStates`) trong `AgentResiliencePolicy.cs` đang lưu cục bộ trong bộ nhớ RAM (`in-memory`) bằng `Dictionary<string, CircuitBreakerState>`.
*   **Rủi ro:** Khi chạy nhiều instance (Load Balancer/Kubernetes), trạng thái lỗi không được đồng bộ. Một instance có thể liên tục gửi request lỗi trong khi instance khác đã đóng mạch.
*   **Giải pháp:**
    *   Sử dụng **Polly v8** (`Microsoft.Extensions.Resilience`) tích hợp với **Redis** hoặc **IDistributedCache** để lưu trạng thái Circuit Breaker chung.
    *   Đảm bảo khi một instance phát hiện lỗi hệ thống và ngắt mạch, tất cả các instance khác cũng nhận được thông tin.
    *   Polly v8 cung cấp `AddResiliencePipeline()` tương thích trực tiếp với `IServiceCollection`, sạch hơn cách tự code hiện tại.

### 5. 🟡 Thu thập Telemetry & Giám sát Hệ thống (Độ ưu tiên: Cao)
*   **Hiện trạng:** Các metrics trong `AgentTelemetryService` đang được lưu bằng các biến `static long` trong RAM. Dữ liệu sẽ **mất sạch** khi ứng dụng restart hoặc khi container bị scale-down.
*   **Vấn đề thêm:** `Dictionary<string, long> _toolUsageCount` sử dụng `lock` — không thread-safe tối ưu cho high-concurrency. Nên dùng `ConcurrentDictionary`.
*   **Giải pháp:**
    *   Tích hợp **OpenTelemetry SDK** (`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.Prometheus.AspNetCore`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`).
    *   Export log, trace và metrics sang **Prometheus** + **Grafana** dashboard.
    *   Lưu thông tin token usage theo `TenantId` vào PostgreSQL để phục vụ đối soát chi phí.
    *   Thay `Dictionary + lock` bằng `ConcurrentDictionary` trong `AgentTelemetryService`.

### 6. 🟡 Thiếu Health Check Endpoints (Độ ưu tiên: Cao)
*   **Hiện trạng:** Không có endpoint `/health` hay `/ready`. Kubernetes/Docker Swarm không thể kiểm tra trạng thái ứng dụng.
*   **Giải pháp:**
    ```csharp
    // Program.cs
    builder.Services.AddHealthChecks()
        .AddNpgSql(builder.Configuration["PostgreSql"]!)
        .AddUrlGroup(new Uri(builder.Configuration["VectorStore:BaseUrl"]! + "/healthz"), "qdrant");
    
    // Sau app.MapControllers();
    app.MapHealthChecks("/health");
    ```
    Cần thêm NuGet: `AspNetCore.HealthChecks.NpgSql`, `AspNetCore.HealthChecks.Uris`.

### 7. 🟡 Thiếu Rate Limiting cho API (Độ ưu tiên: Cao)
*   **Hiện trạng:** Không có cơ chế giới hạn tần suất gọi API. Người dùng/bot có thể gọi không giới hạn → tốn tài nguyên LLM đắt đỏ, gây nghẽn hệ thống.
*   **Giải pháp:** ASP.NET Core 10 có sẵn Rate Limiting middleware:
    ```csharp
    builder.Services.AddRateLimiter(options =>
    {
        options.AddTokenBucketLimiter("ai-agent", opt =>
        {
            opt.TokenLimit = 10;
            opt.ReplenishmentPeriod = TimeSpan.FromMinutes(1);
            opt.TokensPerPeriod = 5;
        });
    });
    
    // Middleware
    app.UseRateLimiter();
    ```

### 8. 🟡 CORS Policy quá mở (Độ ưu tiên: Cao)
*   **Hiện trạng:** Policy tên `"AllowAll"` nhưng thực tế chỉ cho phép `localhost:5173`. Tuy nhiên, **trong Production cần thay bằng domain thực tế** và không nên đặt tên `AllowAll` gây nhầm lẫn.
*   **Giải pháp:** Đổi tên policy và đọc origins từ cấu hình:
    ```csharp
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("ProductionCors", policy =>
        {
            var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() 
                          ?? new[] { "http://localhost:5173" };
            policy.WithOrigins(origins)
                  .AllowCredentials()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });
    ```

### 9. 🟢 Tối ưu hóa Database PostgreSQL (Độ ưu tiên: Trung bình)
*   **Hiện trạng:** Khi recall memory, hệ thống tải tối đa 200 bản ghi từ `AgentMemories` để tính toán trên RAM.
*   **Giải pháp:**
    *   Thêm Database Indexes cho các cột: `TenantId`, `UserId`, `MemoryKey`.
    *   Thiết lập background job định kỳ dọn dẹp memory hết hạn (`ExpiresAtUtc`) và archive memory có `Confidence < 0.3`.
    *   Sử dụng `pgvector` extension nếu muốn chuyển vector search từ Qdrant sang PostgreSQL để giảm số service cần vận hành.

### 10. 🟢 Structured Logging (Độ ưu tiên: Trung bình)
*   **Hiện trạng:** Sử dụng `ILogger` mặc định với nhiều emoji (📊, ⚡, 🔄) trong log message. Log chỉ xuất ra Console, không có cấu trúc JSON cho log aggregation.
*   **Giải pháp:**
    *   Tích hợp **Serilog** với Sink: `Console` (JSON format), `File` (rolling), `Seq` hoặc `Elasticsearch`.
    *   Loại bỏ emoji khỏi log messages (không parse được trong log aggregators).
    *   Sử dụng Structured Logging Properties thay vì string interpolation:
    ```csharp
    // Không nên:
    _logger.LogInformation($"📊 Started: {operationName}");
    // Nên:
    _logger.LogInformation("Agent execution started: {OperationName} for tenant {TenantId}", operationName, tenantId);
    ```

### 11. 🟢 Kích hoạt kỹ thuật HyDE (Độ ưu tiên: Thấp)
*   **Hiện trạng:** `QueryExpansionService.GenerateHypotheticalDocumentAsync` đã viết sẵn nhưng chưa được gọi trong RAG pipeline (`RagPipelineService`).
*   **Giải pháp:**
    *   Bổ sung cấu hình `"RagSettings": { "EnableHyDE": true }` trong `appsettings.json`.
    *   Trong `RagPipelineService`, khi query phức tạp (dài > 30 từ hoặc bắt đầu bằng "giải thích", "tại sao", "so sánh"), kích hoạt HyDE để sinh document giả định, dùng embedding của nó đi tìm trên Qdrant.

### 12. 🟢 Data Seeding mặc định không an toàn (Độ ưu tiên: Trung bình)
*   **Hiện trạng:** `Program.cs` seed tài khoản admin mặc định với password `"admin"`, không yêu cầu đổi mật khẩu.
*   **Giải pháp:**
    *   Sinh mật khẩu ngẫu nhiên khi seed lần đầu và in ra console.
    *   Hoặc đánh dấu `MustChangePassword = true` trên entity `User`, bắt người dùng đổi mật khẩu ngay lần đăng nhập đầu.

---

## 🛠️ Dockerization: Cấu hình deploy đã chỉnh sửa

### Dockerfile (Đã sửa → .NET 10)
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["LmKitOmniApi/LmKitOmniApi.csproj", "LmKitOmniApi/"]
RUN dotnet restore "LmKitOmniApi/LmKitOmniApi.csproj"
COPY . .
WORKDIR "/src/LmKitOmniApi"
RUN dotnet build "LmKitOmniApi.csproj" -c Release -o /app/build
RUN dotnet publish "LmKitOmniApi.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Health check để Docker/K8s kiểm tra trạng thái
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
ENTRYPOINT ["dotnet", "LmKitOmniApi.dll"]
```

### docker-compose.yml (Đã nâng cấp: thêm Redis, health check, restart policy, giới hạn resource)
```yaml
services:
  api:
    build:
      context: .
      dockerfile: LmKitOmniApi/Dockerfile
    ports:
      - "5032:5000"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - PostgreSql=Server=postgres;Port=5432;Database=LmKitAgent;Username=postgres;Password=${POSTGRES_PASSWORD};
      - VectorStore__BaseUrl=http://qdrant:6334
      - JwtSettings__SecretKey=${JWT_SECRET_KEY}
    depends_on:
      postgres:
        condition: service_healthy
      qdrant:
        condition: service_started
      redis:
        condition: service_healthy
    deploy:
      resources:
        limits:
          memory: 2G
          cpus: "2.0"
    restart: unless-stopped

  postgres:
    image: postgres:17-alpine
    ports:
      - "5432:5432"
    environment:
      - POSTGRES_PASSWORD=${POSTGRES_PASSWORD}
      - POSTGRES_DB=LmKitAgent
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres"]
      interval: 10s
      timeout: 5s
      retries: 5
    restart: unless-stopped

  qdrant:
    image: qdrant/qdrant:v1.13.6
    ports:
      - "6333:6333"
      - "6334:6334"
    volumes:
      - qdrantdata:/qdrant/storage
    restart: unless-stopped

  redis:
    image: redis:7-alpine
    ports:
      - "6379:6379"
    volumes:
      - redisdata:/data
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 10s
      timeout: 5s
      retries: 5
    restart: unless-stopped

volumes:
  pgdata:
  qdrantdata:
  redisdata:
```

### .env (Tạo file này, **KHÔNG commit lên git**)
```env
POSTGRES_PASSWORD=YourSuperSecurePasswordHere!
JWT_SECRET_KEY=YourProductionJwtSecretKeyAtLeast64CharsLong_ChangeMe!
```

---

## 📝 Checklist tóm tắt

| # | Hạng mục | Mức độ | Trạng thái |
|---|----------|--------|------------|
| 1 | Dockerfile sai .NET version (8.0 → 10.0) | 🔴 Khẩn cấp | ✅ Đã sửa |
| 2 | Secret Keys lộ trong appsettings.json | 🔴 Khẩn cấp | ⬜ Chưa sửa (theo yêu cầu) |
| 3 | `EnsureCreated()` → `Migrate()` | 🔴 Khẩn cấp | ✅ Đã sửa |
| 4 | Circuit Breaker phân tán (Redis) | 🟡 Cao | ⬜ Cần tích hợp Redis riêng |
| 5 | ConcurrentDictionary thay Dictionary+lock | 🟡 Cao | ✅ Đã sửa |
| 6 | Health Check Endpoints | 🟡 Cao | ✅ Đã sửa |
| 7 | Rate Limiting cho API | 🟡 Cao | ✅ Đã sửa |
| 8 | CORS Policy cấu hình hóa | 🟡 Cao | ✅ Đã sửa |
| 9 | DB Indexes + Cleanup Job | 🟢 Trung bình | ⬜ Cần tạo EF Migration |
| 10 | Structured Logging (Serilog) | 🟢 Trung bình | ⬜ Cần cài package riêng |
| 11 | Kích hoạt HyDE trong RAG | 🟢 Thấp | ✅ Đã sửa |
| 12 | Data Seeding password an toàn | 🟢 Trung bình | ✅ Đã sửa |

