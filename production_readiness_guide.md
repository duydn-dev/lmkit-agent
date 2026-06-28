# Hướng dẫn Nâng Cấp Hệ Thống Lên Production (Production Readiness Guide)

Tài liệu này tổng hợp các hạng mục cần thực hiện tiếp theo về mặt hạ tầng, bảo mật, và cấu hình phân tán để đưa hệ thống **LmKit Omni AI Agent Platform** vận hành an toàn và ổn định trên môi trường Production thực tế.

---

## 📋 Danh sách hạng mục cần nâng cấp

### 1. Phân tán trạng thái Resilience & Circuit Breaker (Độ ưu tiên: Cao)
*   **Hiện trạng:** Trạng thái đóng/mở mạch (`_circuitStates`) của các tool đang lưu cục bộ trong bộ nhớ RAM (`in-memory`) của instance ứng dụng.
*   **Rủi ro:** Khi chạy nhiều instance ứng dụng (sau Load Balancer/Kubernetes), trạng thái lỗi của các service liên kết (như Qdrant hay LLM Server) không được đồng bộ. Một instance có thể liên tục gửi request lỗi trong khi instance khác đã đóng mạch.
*   **Giải pháp:** 
    *   Sử dụng thư viện **Polly.Contrib** hoặc **Polly v8** tích hợp với một kho lưu trữ phân tán như **Redis** để lưu trạng thái Circuit Breaker chung.
    *   Đảm bảo khi một instance phát hiện lỗi hệ thống và ngắt mạch, tất cả các instance khác sẽ ngay lập tức chuyển sang chế độ ngắt mạch (Circuit Open) và sử dụng dữ liệu fallback.

### 2. Thu thập Telemetry & Giám sát Hệ thống (Độ ưu tiên: Cao)
*   **Hiện trạng:** Các metrics (tổng số token tiêu thụ, số lượng request, số lượng lỗi) đang được lưu tạm thời qua các biến `static long` trong `AgentTelemetryService`. Các số liệu này sẽ bị mất khi ứng dụng khởi động lại hoặc tự động thu hẹp container (Scale-down).
*   **Giải pháp:**
    *   Tích hợp bộ thư viện **OpenTelemetry SDK** (`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.Prometheus.AspNetCore`).
    *   Export log, trace và metrics sang các collector chuẩn như **Prometheus** và hiển thị dashboard giám sát trực quan trên **Grafana**.
    *   Lưu trữ thông tin lượng token sử dụng (token usage metrics) theo từng `TenantId` vào PostgreSQL để phục vụ cho việc đối soát chi phí vận hành mô hình ngôn ngữ lớn (LLM/VLM).

### 3. Quản lý Secret Keys an toàn (Độ ưu tiên: Cao)
*   **Hiện trạng:** JWT Secret Key, Postgres Connection String và Qdrant Endpoint đang được đặt trực tiếp dạng plaintext trong file `appsettings.json`.
*   **Giải pháp:**
    *   Sử dụng cơ chế ghi đè cấu hình thông qua **Environment Variables** (Biến môi trường) khi chạy Docker/Kubernetes. Cấu hình ASP.NET Core sẽ tự động ánh xạ cấu hình dạng `JwtSettings__SecretKey`.
    *   Trên môi trường Cloud, sử dụng các dịch vụ Key Vault chuyên dụng: **Azure Key Vault**, **AWS Secrets Manager**, hoặc **HashiCorp Vault**.

### 4. Tối ưu hóa Database PostgreSQL cho quy mô lớn (Độ ưu tiên: Trung bình)
*   **Hiện trạng:** Khi thực hiện recall memory, hệ thống tải tối đa 200 bản ghi mới nhất từ bảng `AgentMemories` để tính toán độ tương đồng ngữ nghĩa trên RAM.
*   **Giải pháp:**
    *   Thêm Database Indexes cho các cột được truy vấn thường xuyên: `TenantId`, `UserId`, và `MemoryKey`.
    *   Thiết lập một background job định kỳ (cron job) để dọn dẹp các memory đã hết hạn (`ExpiresAtUtc`) và lưu trữ (archive) các memory có mức độ tin cậy thấp (`Confidence < 0.3`).

### 5. Kích hoạt kỹ thuật tìm kiếm HyDE (Hypothetical Document Embeddings) (Độ ưu tiên: Thấp)
*   **Hiện trạng:** Kỹ thuật sinh tài liệu giả định nhằm tối ưu hóa chất lượng RAG đã được viết sẵn trong `QueryExpansionService.GenerateHypotheticalDocumentAsync` nhưng chưa được cắm vào pipeline.
*   **Giải pháp:**
    *   Bổ sung tham số cấu hình bật/tắt HyDE trong RAG.
    *   Khi người dùng hỏi một câu hỏi dạng khái niệm phức tạp, hãy kích hoạt HyDE để sinh câu trả lời giả định trước, sau đó dùng embedding của câu trả lời giả định đó đi tìm kiếm trên Qdrant DB. Kỹ thuật này sẽ cho độ khớp tài liệu thực tế cao hơn so với tìm kiếm bằng câu hỏi ngắn ban đầu.

---

## 🛠️ Dockerization: Gợi ý file cấu hình deploy nhanh

Để chuẩn bị deploy môi trường Production hoặc Staging thử nghiệm, bạn có thể tham khảo cấu hình mẫu sau:

### Dockerfile (Đặt tại thư mục root của LmKitOmniApi)
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["LmKitOmniApi/LmKitOmniApi.csproj", "LmKitOmniApi/"]
RUN dotnet restore "LmKitOmniApi/LmKitOmniApi.csproj"
COPY . .
WORKDIR "/src/LmKitOmniApi"
RUN dotnet build "LmKitOmniApi.csproj" -c Release -o /app/build
RUN dotnet publish "LmKitOmniApi.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
ENTRYPOINT ["dotnet", "LmKitOmniApi.dll"]
```

### docker-compose.yml (Mẫu triển khai multi-service)
```yaml
version: '3.8'

services:
  api:
    build:
      context: .
      dockerfile: LmKitOmniApi/Dockerfile
    ports:
      - "5032:5000"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - PostgreSql=Server=postgres;Port=5432;Database=LmKitAgent;Username=postgres;Password=YOUR_SECURE_PASSWORD;
      - VectorStore__BaseUrl=http://qdrant:6334
      - JwtSettings__SecretKey=YOUR_PRODUCTION_JWT_SECRET_KEY_HERE
    depends_on:
      - postgres
      - qdrant

  postgres:
    image: postgres:15-alpine
    ports:
      - "5432:5432"
    environment:
      - POSTGRES_PASSWORD=YOUR_SECURE_PASSWORD
      - POSTGRES_DB=LmKitAgent
    volumes:
      - pgdata:/var/lib/postgresql/data

  qdrant:
    image: qdrant/qdrant:latest
    ports:
      - "6333:6333"
      - "6334:6334"
    volumes:
      - qdrantdata:/qdrant/storage

volumes:
  pgdata:
  qdrantdata:
```
