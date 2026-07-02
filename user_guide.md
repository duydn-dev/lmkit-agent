# Hướng Dẫn Sử Dụng LmKitAgent Omni (Tiêu Chuẩn Gemini 3.1)

Dựa trên tiêu chuẩn của các mô hình đa phương thức và Agentic hiện đại như Gemini 3.1, hệ thống LmKitAgent Omni được thiết kế không chỉ để chat đơn thuần mà còn có khả năng tự động phân tích ngữ cảnh, lập kế hoạch (ReAct) và gọi các bộ công cụ (Tools/Skills) một cách thông minh. 

Dưới đây là bảng tổng hợp các chức năng cốt lõi cùng các Prompt (Câu lệnh) mẫu giúp bạn tối đa hóa sức mạnh của hệ thống.

> [!TIP]
> **Nguyên tắc Prompting tối ưu cho Agent:**
> - **Rõ ràng mục tiêu:** Hãy nói rõ bạn muốn gì ở cuối câu.
> - **Cung cấp ngữ cảnh:** Càng nhiều thông tin nền, Agent càng hành động chính xác.
> - **Cho phép suy nghĩ:** Khi tác vụ khó, hãy thêm câu "Hãy phân tích từng bước trước khi làm" để kích hoạt chuỗi suy luận sâu của mô hình.

---

## Bảng Tra Cứu Chức Năng & Prompt Mẫu

| Tính Năng / Capability | Mô Tả Cách Hệ Thống Hoạt Động | Prompt Mẫu (Tham Khảo) |
| :--- | :--- | :--- |
| **🤖 1. Agentic AI & Suy Luận Đa Bước** | AI có khả năng lập kế hoạch nhiều bước (ReAct framework). Nó sẽ chia nhỏ một yêu cầu phức tạp thành nhiều hành động phụ, thực thi và tự động sửa sai nếu một bước thất bại. | *"Tôi muốn tổ chức một sự kiện ra mắt sản phẩm vào tháng tới. Hãy lập cho tôi một kế hoạch chi tiết, sau đó tìm kiếm các địa điểm tổ chức phù hợp sức chứa 200 người ở Hà Nội, và cuối cùng tạo một bức thư mời mẫu cho khách mời VIP."* |
| **🔍 2. Duyệt Web Thời Gian Thực (Web Search)** | Gọi công cụ Search Engine để tìm kiếm tin tức, dữ liệu mới nhất mà mô hình gốc không có sẵn trong weights. Thích hợp để check fact hoặc lấy số liệu. | *"Cập nhật cho tôi tình hình thị trường chứng khoán VN-Index ngày hôm nay thế nào? Trích xuất các mã cổ phiếu ngân hàng có sự tăng trưởng nổi bật nhất kèm lý do."* |
| **📚 3. RAG & Phân Tích Tài Liệu Nâng Cao** | Tự động trích xuất nội dung từ kho dữ liệu cá nhân (Vector DB - Qdrant) hoặc file tải lên (PDF, Word, Excel) để trả lời câu hỏi dựa trên ngữ cảnh chính xác tuyệt đối. | *"Dựa trên báo cáo tài chính quý 3/2026 trong kho dữ liệu của hệ thống, hãy phân tích sự thay đổi trong dòng tiền hoạt động kinh doanh và lập bảng so sánh với cùng kỳ năm ngoái."* |
| **👁️ 4. Vision & Nhận Diện Hình Ảnh** | Gửi hình ảnh cho AI phân tích. Hệ thống có thể nhận dạng vật thể, trích xuất văn bản (OCR) hoặc giải quyết vấn đề logic từ sơ đồ, biểu đồ. | *"Hãy xem biểu đồ doanh số tôi vừa tải lên. Giải thích lý do tại sao khu vực miền Bắc lại sụt giảm trong tháng 5 và đề xuất 3 chiến lược khắc phục."* |
| **🎙️ 5. Phân Tích Âm Thanh (Speech/Audio)** | Khả năng xử lý file âm thanh, bóc băng (transcript), tóm tắt cuộc họp dựa trên công nghệ Whisper/Speech-to-Text. | *"Hãy nghe file ghi âm cuộc họp hội đồng quản trị này. Liệt kê lại các quyết định quan trọng đã được thông qua và tạo một to-do list cho phòng Marketing."* |
| **🔌 6. Tích hợp Công Cụ Bên Thứ 3 (MCP)** | Hệ thống tự động kết nối (Model Context Protocol) với các Server vệ tinh. Agent có thể thay bạn tạo Ticket Jira, check Code GitHub, hoặc tương tác với CRM. | *"Kiểm tra trên kho dữ liệu của công ty xem có khách hàng nào tên là 'Nguyễn Văn A' không. Nếu có, hãy tạo một lịch hẹn (Calendar) với họ vào 9h sáng mai để thảo luận về hợp đồng mới."* |
| **✍️ 7. Tạo/Chỉnh Sửa Tập Tin (Office Automation)** | Yêu cầu AI trực tiếp tạo ra hoặc chỉnh sửa các file mã nguồn, file Word (Docx), Excel, hoặc PDF từ nội dung đàm thoại. | *"Từ những gì chúng ta vừa thống nhất ở trên, hãy tự động gen cho tôi một file Word chứa toàn bộ kế hoạch dự án. Thêm format tiêu đề rõ ràng và tải xuống máy giúp tôi."* |
| **💻 8. Tự Động Viết & Chạy Code (Code Execution)** | Viết kịch bản tự động hóa, phân tích data bằng Python/C# an toàn trong Sandbox và trả về kết quả hoặc biểu đồ. | *"Tôi có một tập dữ liệu CSV chứa 10.000 dòng log truy cập. Hãy viết script Python để lọc ra top 5 IP truy cập nhiều nhất, vẽ biểu đồ bar chart và lưu lại file hình."* |

---

## 🛠️ Một Số Mẹo (Best Practices) Cho Môi Trường Doanh Nghiệp (Multi-Tenant)

Nếu bạn đang sử dụng hệ thống này trong bối cảnh phân quyền doanh nghiệp (Mỗi khách hàng/Tenant một kho dữ liệu riêng), hãy lưu ý:
1. **Dữ liệu được cách ly:** Khi bạn yêu cầu *"Tìm kiếm trong hệ thống"*, AI chỉ có quyền truy cập vào không gian Vector/MCP của riêng công ty bạn. 
2. **Kích hoạt External Tools:** Nếu một lệnh yêu cầu tích hợp bên ngoài (ví dụ: *"Gửi email cho khách"*), hãy đảm bảo Tenant của bạn đã cấu hình Server MCP có quyền SendEmail. Nếu không, Agent sẽ báo lỗi *"Không tìm thấy công cụ phù hợp"*.

> [!CAUTION]
> Mặc dù Agentic AI rất thông minh trong việc sửa sai, nhưng với các hành động **tạo, sửa, xóa (Write/Delete)** trên hệ thống thật (qua MCP hoặc Code Execution), hệ thống sẽ luôn trả về một hộp thoại xin quyền (Human-in-the-loop) để bạn xác nhận trước khi thực hiện hành động.
