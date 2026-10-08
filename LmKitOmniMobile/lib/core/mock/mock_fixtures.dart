/// Dữ liệu mẫu cho chế độ demo (`USE_MOCK_DATA=true`).
///
/// Mục đích: xem và duyệt giao diện khi chưa có backend (hoặc backend chưa có
/// dữ liệu). Dữ liệu ở đây phải khớp **đúng** JSON mà `LmKitOmniApi` trả về —
/// mọi tên field đều đối chiếu với model/repository trong app, nên đổi tên
/// field ở đây là test `mock_adapter_test.dart` đỏ ngay.
///
/// Không file nào trong `lib/features/**` biết tới lớp này: nó chỉ được cài vào
/// Dio như một `HttpClientAdapter` thay thế.
library;

/// Mốc thời gian tương đối để danh sách luôn trông "mới" (2 giờ trước, hôm qua…).
String _ago(Duration duration) =>
    DateTime.now().toUtc().subtract(duration).toIso8601String();

/// Ngày `yyyy-MM-dd` cách hôm nay [days] ngày — chuỗi theo ngày của dashboard dùng
/// định dạng này (backend cũng trả đúng dạng đó).
String _dateDaysAgo(int days) {
  final date = DateTime.now().toUtc().subtract(Duration(days: days));
  return '${date.year}-${date.month.toString().padLeft(2, '0')}'
      '-${date.day.toString().padLeft(2, '0')}';
}

class MockFixtures {
  const MockFixtures._();

  // ------------------------------------------------------------------ tenant

  static const tenantId = '8f4c1d20-0000-4000-8000-000000000001';
  static const tenantName =
      'Trung tâm Thông tin lưu trữ và Thư viện tài nguyên môi trường quốc gia';
  static const agentName = 'Trợ lý CILA';

  /// Đường dẫn tương đối, đúng như backend trả về (app tự ghép `apiBaseUrl`).
  static const logoPath = '/api/tenant-branding/logo?v=demo';

  // -------------------------------------------------------------------- auth

  static Map<String, dynamic> user({
    String email = 'admin@cila.gov.vn',
    String fullName = 'Nguyễn Văn Duy',
    String role = 'Admin',
  }) => {
    'id': 'u-0001',
    'email': email,
    'fullName': fullName,
    'role': role,
    'tenantId': tenantId,
    'tenant': {'name': tenantName, 'agentName': agentName, 'logoUrl': logoPath},
  };

  static Map<String, dynamic> loginResponse({String email = ''}) => {
    'accessToken': 'mock-access-token',
    'refreshToken': 'mock-refresh-token',
    'accessTokenExpiresAtUtc': _ago(const Duration(minutes: -55)),
    'refreshTokenExpiresAtUtc': _ago(const Duration(days: -14)),
    'user': user(email: email.isEmpty ? 'admin@cila.gov.vn' : email),
  };

  static Map<String, dynamic> tokenPair() => {
    'accessToken': 'mock-access-token',
    'refreshToken': 'mock-refresh-token',
    'accessTokenExpiresAtUtc': _ago(const Duration(minutes: -55)),
    'refreshTokenExpiresAtUtc': _ago(const Duration(days: -14)),
  };

  // -------------------------------------------------------------------- chat

  static const sessionReportId = 's-demo-0001';
  static const sessionProcedureId = 's-demo-0002';
  static const sessionAgentId = 's-demo-0003';
  static const sessionProjectId = 's-demo-0004';

  static List<Map<String, dynamic>> chatSessions() => [
    {
      'id': sessionReportId,
      'title': 'Số liệu quan trắc không khí tháng 8/2026',
      'createdAt': _ago(const Duration(hours: 2, minutes: 12)),
      'customAgentId': null,
      'agentName': null,
      'agentIcon': null,
      'projectId': null,
      'isEphemeral': false,
    },
    {
      'id': sessionProcedureId,
      'title': 'Thủ tục cấp phép xả thải — trình tự hồ sơ',
      'createdAt': _ago(const Duration(days: 1, hours: 3)),
      'customAgentId': null,
      'agentName': null,
      'agentIcon': null,
      'projectId': null,
      'isEphemeral': false,
    },
    {
      'id': sessionAgentId,
      'title': 'Rà soát hợp đồng quan trắc quý III',
      'createdAt': _ago(const Duration(days: 2, hours: 5)),
      'customAgentId': 'a-demo-0001',
      'agentName': 'Chuyên gia pháp chế',
      'agentIcon': '⚖️',
      'projectId': null,
      'isEphemeral': false,
    },
    {
      'id': sessionProjectId,
      'title': 'Dàn ý báo cáo tổng kết dự án quan trắc 2026',
      'createdAt': _ago(const Duration(days: 5)),
      'customAgentId': null,
      'agentName': null,
      'agentIcon': null,
      'projectId': 'p-demo-0001',
      'isEphemeral': false,
    },
  ];

  /// Biểu đồ do trợ lý sinh ra, đúng định dạng `<chart>{json}</chart>` mà
  /// `parseGenerativeContent` đọc (Chart.js).
  static const _chartBlock = r'''
<chart>{"type":"bar","title":"PM2.5 trung bình tháng 8 (µg/m³)","data":{"labels":["Hoàn Kiếm","Ba Đình","Long Biên","Hà Đông","Tây Hồ"],"datasets":[{"label":"2026","data":[38.2,35.4,41.1,33.8,29.6],"backgroundColor":"#1E3A8A"},{"label":"2025","data":[45.6,42.1,47.3,39.2,34.1],"backgroundColor":"#FFCD00"}]}}</chart>''';

  static const reportAnswer =
      '''
Đã tổng hợp xong số liệu quan trắc không khí tháng 8/2026 từ 5 trạm nền.

**Kết luận chính**

1. PM2.5 trung bình toàn thành phố là 35,6 µg/m³, giảm 18,4% so với cùng kỳ 2025.
2. Trạm Long Biên vẫn cao nhất (41,1 µg/m³) do ảnh hưởng của hoạt động vận tải.
3. Số ngày vượt ngưỡng QCVN 05:2023 là 6 ngày, ít hơn 11 ngày so với tháng 8/2025.

$_chartBlock

Chi tiết từng trạm nằm trong tệp đính kèm bên dưới.''';

  static List<Map<String, dynamic>> chatMessages(String sessionId) {
    if (sessionId == sessionReportId) return _reportMessages;
    if (sessionId == sessionProcedureId) return _procedureMessages;
    if (sessionId == sessionAgentId) return _agentMessages;
    return _projectMessages;
  }

  static final List<Map<String, dynamic>> _reportMessages = [
    {
      'id': 'm-1',
      'role': 'user',
      'content':
          'Tổng hợp số liệu quan trắc chất lượng không khí tháng 8/2026 ở Hà Nội, '
          'so sánh với cùng kỳ 2025 và vẽ biểu đồ giúp tôi.',
      'createdAt': _ago(const Duration(hours: 2, minutes: 12)),
      'reasoning': '',
    },
    {
      'id': 'm-2',
      'role': 'assistant',
      'content':
          '[THINKING]:Đang tìm tài liệu quan trắc trong kho tri thức\n'
          '[THINKING]:Đang tính giá trị trung bình theo từng trạm\n'
          '[THINKING]:Đang dựng biểu đồ so sánh hai năm\n'
          '[REASONING]:Câu hỏi cần số liệu theo trạm nên tôi ưu tiên tài liệu nội '
          'bộ trước, sau đó đối chiếu với báo cáo công khai của Sở Tài nguyên.\n'
          '[REASONING]:Chênh lệch giữa hai năm lớn ở Long Biên nên cần nêu rõ '
          'nguyên nhân để tránh hiểu sai.\n'
          '[WEB_SEARCH]:https://monre.gov.vn/bao-cao-moi-truong-2026|'
          'https://hanoi.gov.vn/quan-trac-khong-khi-thang-8\n'
          '[FILE:{"id":"f-demo-0001","name":"bao-cao-khong-khi-08-2026.md",'
          '"contentType":"text/markdown","size":18432}]\n'
          '$reportAnswer',
      'createdAt': _ago(const Duration(hours: 2, minutes: 10)),
      'reasoning': '',
    },
    {
      'id': 'm-3',
      'role': 'user',
      'content': 'Vì sao Long Biên tăng? Trích dẫn nguồn giúp tôi.',
      'createdAt': _ago(const Duration(hours: 2, minutes: 4)),
      'reasoning': '',
    },
    {
      'id': 'm-4',
      'role': 'assistant',
      'content':
          '[WEB_SEARCH]:https://hanoi.gov.vn/quan-trac-khong-khi-thang-8\n'
          'Nguyên nhân chính là mật độ xe tải nặng qua cầu Chương Dương tăng '
          '12% và hai điểm thi công hạ tầng chưa che chắn.\n\n'
          'Nguồn: báo cáo quan trắc tháng 8/2026 của Sở Tài nguyên và Môi '
          'trường Hà Nội, mục 3.2.',
      'createdAt': _ago(const Duration(hours: 2, minutes: 2)),
      'reasoning': '',
    },
  ];

  static final List<Map<String, dynamic>> _procedureMessages = [
    {
      'id': 'm-11',
      'role': 'user',
      'content': 'Trình tự hồ sơ cấp phép xả thải gồm những bước nào?',
      'createdAt': _ago(const Duration(days: 1, hours: 3)),
      'reasoning': '',
    },
    {
      'id': 'm-12',
      'role': 'assistant',
      'content':
          '[THINKING]:Đang đối chiếu Luật Bảo vệ môi trường 2020\n'
          '[REASONING]:Người hỏi là đơn vị nội bộ nên cần nêu rõ cả thời hạn '
          'xử lý theo Nghị định 08/2022/NĐ-CP.\n'
          'Hồ sơ gồm 4 nhóm: đơn đề nghị, báo cáo đánh giá tác động môi '
          'trường, phương án xử lý chất thải và kế hoạch quan trắc.\n\n'
          '1. Nộp hồ sơ tại Sở Tài nguyên và Môi trường.\n'
          '2. Thẩm định hồ sơ trong 30 ngày làm việc.\n'
          '3. Kiểm tra thực tế tại cơ sở.\n'
          '4. Cấp phép, thời hạn tối đa 10 năm.',
      'createdAt': _ago(const Duration(days: 1, hours: 3, minutes: -1)),
      'reasoning': '',
    },
  ];

  static final List<Map<String, dynamic>> _agentMessages = [
    {
      'id': 'm-21',
      'role': 'user',
      'content': 'Rà soát điều khoản phạt trong hợp đồng quan trắc quý III.',
      'createdAt': _ago(const Duration(days: 2, hours: 5)),
      'reasoning': '',
    },
    {
      'id': 'm-22',
      'role': 'assistant',
      'content':
          'Điều 7.3 quy định mức phạt 0,05% giá trị hợp đồng cho mỗi ngày '
          'chậm, nhưng không có mức trần — đề nghị bổ sung trần 8% để cân đối '
          'với Điều 301 Luật Thương mại.',
      'createdAt': _ago(const Duration(days: 2, hours: 5, minutes: -2)),
      'reasoning': '',
    },
  ];

  static final List<Map<String, dynamic>> _projectMessages = [
    {
      'id': 'm-31',
      'role': 'user',
      'content': 'Lập dàn ý báo cáo tổng kết dự án quan trắc 2026.',
      'createdAt': _ago(const Duration(days: 5)),
      'reasoning': '',
    },
    {
      'id': 'm-32',
      'role': 'assistant',
      'content':
          'Dàn ý đề xuất: mở đầu, kết quả quan trắc theo quý, đánh giá thiết '
          'bị, tồn tại và kiến nghị. Tôi có thể viết chi tiết từng mục nếu bạn '
          'chọn phần cần làm trước.',
      'createdAt': _ago(const Duration(days: 5, minutes: -1)),
      'reasoning': '',
    },
  ];

  /// Các sự kiện SSE cho `POST /api/chat/stream`.
  ///
  /// Trả về nguyên văn phần sau `data:` — giống hệt định dạng backend, kèm cả
  /// `[DONE]` để app đóng stream.
  static List<String> chatStreamEvents(String question) {
    final topic = question.trim().isEmpty
        ? 'nội dung bạn vừa hỏi'
        : question.trim();
    return [
      '[Agent invoked: tool=search_knowledge_base]',
      '[THINKING]:Đang phân tích yêu cầu: $topic',
      '[THINKING]:Đang tìm trong kho tri thức nội bộ',
      '[WEB_SEARCH]:https://monre.gov.vn/van-ban-moi-truong|'
          'https://chinhphu.vn/chi-dao-dieu-hanh',
      '[REASONING]:Câu hỏi thuộc nhóm nghiệp vụ nên tôi trả lời theo trình tự '
          'pháp lý trước, sau đó mới tới số liệu.',
      'Tôi đã tiếp nhận yêu cầu: $topic\n\n',
      'Dưới đây là các bước xử lý theo quy định hiện hành:\n',
      '1. Kiểm tra tính đầy đủ của hồ sơ đầu vào.\n',
      '2. Đối chiếu với văn bản pháp luật đang có hiệu lực.\n',
      '3. Tổng hợp kết quả và ghi rõ nguồn tham chiếu.\n\n',
      'Nếu bạn cần bản đầy đủ, tôi có thể xuất thành tệp để lưu trữ.\n',
      '[FILE:{"id":"f-demo-0002","name":"ket-qua-tra-cuu.md",'
          '"contentType":"text/markdown","size":4096}]',
      '[DONE]',
    ];
  }

  /// Các sự kiện SSE cho `POST /api/agent-runs` (agent tự hành).
  static List<String> agentRunEvents(String goal) => [
    '[THINKING]:Bắt đầu lập kế hoạch cho mục tiêu: $goal',
    '[Agent invoked: tool=web_search]',
    '[THINKING]:Đã thu thập 4 nguồn tham chiếu',
    '[Agent invoked: tool=knowledge_base_query]',
    'Đã hoàn tất rà soát. Kết quả: cần bổ sung hồ sơ nghiệm thu thiết bị quý '
        'III trước ngày 30/9.\n',
    '[DONE]',
  ];

  /// Các sự kiện SSE cho `POST /api/research`.
  static List<String> researchEvents(String query) => [
    '[THINKING]:Đang tìm nguồn cho: $query',
    '[WEB_SEARCH]:https://monre.gov.vn/nghien-cuu|'
        'https://tapchimoitruong.vn/bai-viet',
    'Tổng hợp 4 nguồn: xu hướng giảm phát thải, lộ trình kiểm kê khí nhà '
        'kính, kinh nghiệm địa phương và khuyến nghị chính sách.\n',
    '[RESEARCH_SAVED:rr-demo-0001]',
    '[DONE]',
  ];

  /// Phiên chat mới tạo (`POST /api/chat/sessions`) — giữ nguyên các tham số
  /// người dùng đã chọn để màn chat hiển thị đúng ngữ cảnh.
  static Map<String, dynamic> createdSession({
    Map<String, dynamic> body = const {},
  }) {
    final projectId = body['projectId']?.toString();
    final agentId = body['customAgentId']?.toString();
    return {
      'id': 's-demo-new',
      'title': null,
      'createdAt': _ago(Duration.zero),
      'customAgentId': agentId,
      'agentName': agentId == null ? null : 'Chuyên gia pháp chế',
      'agentIcon': agentId == null ? null : '⚖️',
      'projectId': projectId,
      'isEphemeral': body['ephemeral'] == true,
    };
  }

  static const shareToken = 'demo-share-token';

  static Map<String, dynamic> shareLink() => {
    'token': shareToken,
    'expiresAtUtc': _ago(const Duration(days: -30)),
  };

  static Map<String, dynamic> sharedChat() => {
    'title': 'Số liệu quan trắc không khí tháng 8/2026',
    'createdAt': _ago(const Duration(hours: 2, minutes: 12)),
    'messages': [
      for (final message in _reportMessages)
        {
          'role': message['role'],
          'content': message['content'],
          'createdAt': message['createdAt'],
        },
    ],
  };

  static const producedFilePreview =
      '# Báo cáo quan trắc không khí tháng 8/2026\n\n'
      'Tệp mẫu sinh ra trong chế độ dữ liệu mẫu.\n';

  // ----------------------------------------------------------------- studio

  static Map<String, dynamic> customAgentsPage() => {
    'items': _customAgents,
    'page': 1,
    'pageSize': 50,
    'totalCount': _customAgents.length,
    'totalPages': 1,
  };

  static final List<Map<String, dynamic>> _customAgents = [
    {
      'id': 'a-demo-0001',
      'name': 'Chuyên gia pháp chế',
      'description':
          'Đọc hợp đồng, đối chiếu Luật Bảo vệ môi trường và Luật Thương mại.',
      'icon': '⚖️',
      'personaPrompt':
          'Bạn là chuyên gia pháp chế về môi trường. Luôn trích dẫn điều khoản '
          'cụ thể và nêu rủi ro pháp lý.',
      'allowedTools': ['knowledge_base_query', 'web_search'],
      'knowledgeDocumentIds': ['doc-demo-0002'],
      'loraAdapterId': null,
      'isSharedWithTenant': true,
      'isOwner': true,
    },
    {
      'id': 'a-demo-0002',
      'name': 'Trợ lý tổng hợp số liệu',
      'description': 'Tổng hợp số liệu quan trắc, sinh biểu đồ so sánh.',
      'icon': '📊',
      'personaPrompt':
          'Bạn phụ trách tổng hợp số liệu quan trắc và luôn kèm biểu đồ khi '
          'so sánh nhiều kỳ.',
      'allowedTools': null,
      'knowledgeDocumentIds': [],
      'loraAdapterId': 'lora-demo-0001',
      'isSharedWithTenant': false,
      'isOwner': true,
    },
  ];

  static Map<String, dynamic> customAgent({
    String id = 'a-demo-0003',
    String name = 'Agent dữ liệu mẫu',
    String personaPrompt = 'Bạn là trợ lý mẫu.',
  }) => {
    'id': id,
    'name': name,
    'description': null,
    'icon': null,
    'personaPrompt': personaPrompt,
    'allowedTools': null,
    'knowledgeDocumentIds': const <String>[],
    'loraAdapterId': null,
    'isSharedWithTenant': false,
    'isOwner': true,
  };

  static const List<Map<String, dynamic>> agentTools = [
    {
      'name': 'web_search',
      'label': 'Tìm kiếm web',
      'description': 'Tra cứu thông tin công khai trên internet.',
    },
    {
      'name': 'knowledge_base_query',
      'label': 'Kho tri thức nội bộ',
      'description': 'Truy vấn tài liệu đã nạp vào kho tri thức.',
    },
    {
      'name': 'database_query',
      'label': 'Truy vấn CSDL',
      'description': 'Đọc dữ liệu từ kết nối CSDL đã cấu hình.',
    },
  ];

  static const List<Map<String, dynamic>> knowledgeDocs = [
    {'id': 'doc-demo-0001', 'fileName': 'Luật Bảo vệ môi trường 2020.pdf'},
    {'id': 'doc-demo-0002', 'fileName': 'Nghị định 08/2022-NĐ-CP.pdf'},
  ];

  static const List<Map<String, dynamic>> documents = [
    {
      'id': 'doc-demo-0001',
      'fileName': 'Luật Bảo vệ môi trường 2020.pdf',
      'isVectorized': true,
      'vectorizationStatus': 'Completed',
      'uploadedAt': '2026-08-02T03:20:00Z',
      'hasError': false,
    },
    {
      'id': 'doc-demo-0002',
      'fileName': 'Nghị định 08/2022-NĐ-CP.pdf',
      'isVectorized': true,
      'vectorizationStatus': 'Completed',
      'uploadedAt': '2026-08-02T03:24:00Z',
      'hasError': false,
    },
    {
      'id': 'doc-demo-0003',
      'fileName': 'Bao-cao-quan-trac-thang-8.xlsx',
      'isVectorized': false,
      'vectorizationStatus': 'Indexing',
      'uploadedAt': '2026-09-14T01:05:00Z',
      'hasError': false,
    },
    {
      'id': 'doc-demo-0004',
      'fileName': 'Bien-ban-hop-quy-III.docx',
      'isVectorized': false,
      'vectorizationStatus': 'Failed',
      'uploadedAt': '2026-09-10T08:41:00Z',
      'hasError': true,
    },
  ];

  static Map<String, dynamic> schedulesPage() => {
    'items': _schedules,
    'page': 1,
    'pageSize': 50,
    'totalCount': _schedules.length,
    'totalPages': 1,
  };

  static final List<Map<String, dynamic>> _schedules = [
    {
      'id': 'sch-demo-0001',
      'name': 'Báo cáo không khí hằng ngày',
      'prompt':
          'Tổng hợp số liệu PM2.5 trong ngày và gửi bản tóm tắt cho lãnh đạo.',
      'scheduleKind': 'interval',
      'enabled': true,
      'nextRunUtc': _ago(const Duration(hours: -5)),
      'runMode': 'agent',
      'customAgentId': 'a-demo-legal',
      'approveFutureRuns': true,
      'lastAgentRunId': 'run-demo-0001',
      'intervalMinutes': 1440,
      'timeOfDayMinutes': null,
      'dayOfWeek': null,
      'lastStatus': 'Succeeded',
      'lastError': null,
    },
    {
      'id': 'sch-demo-0002',
      'name': 'Kiểm tra hồ sơ tồn đọng',
      'prompt': 'Liệt kê hồ sơ quá 30 ngày chưa xử lý.',
      'scheduleKind': 'weekly',
      'enabled': false,
      'nextRunUtc': _ago(const Duration(days: -3)),
      'runMode': 'completion',
      'intervalMinutes': null,
      'timeOfDayMinutes': 480,
      'dayOfWeek': 1,
      'lastStatus': 'Failed',
      'lastError': 'Không kết nối được CSDL nghiệp vụ.',
    },
  ];

  /// Lịch tự động mới tạo (`POST /api/schedules`).
  static Map<String, dynamic> createdSchedule({
    Map<String, dynamic> body = const {},
  }) => {
    'id': 'sch-demo-new',
    'name': (body['name'] ?? 'Lịch mới').toString(),
    'prompt': (body['prompt'] ?? '').toString(),
    'scheduleKind': (body['scheduleKind'] ?? 'interval').toString(),
    'enabled': true,
    'nextRunUtc': body['runAtUtc'] ?? _ago(const Duration(minutes: -60)),
    'runMode': body['runMode'] ?? 'completion',
    'customAgentId': body['customAgentId'],
    'approveFutureRuns': body['approveFutureRuns'] ?? false,
    'lastAgentRunId': null,
    'deliveryWebhookUrl': body['deliveryWebhookUrl'],
    'intervalMinutes': body['intervalMinutes'],
    'timeOfDayMinutes': body['timeOfDayMinutes'],
    'dayOfWeek': body['dayOfWeek'],
    'lastStatus': null,
    'lastError': null,
  };

  static Map<String, dynamic> agentRunsPage() => {
    'items': _agentRuns,
    'page': 1,
    'pageSize': 50,
    'totalCount': _agentRuns.length,
    'totalPages': 1,
  };

  static final List<Map<String, dynamic>> _agentRuns = [
    {
      'id': 'run-demo-0001',
      'goal': 'Rà soát toàn bộ hợp đồng quan trắc quý III và nêu rủi ro.',
      'status': 'completed',
      'stepCount': 7,
      'createdAtUtc': _ago(const Duration(hours: 6)),
      'completedAtUtc': _ago(const Duration(hours: 5, minutes: 40)),
    },
    {
      'id': 'run-demo-0002',
      'goal': 'Tổng hợp phản hồi của 12 đơn vị quan trắc.',
      'status': 'running',
      'stepCount': 3,
      'createdAtUtc': _ago(const Duration(minutes: 4)),
      'completedAtUtc': null,
    },
    {
      'id': 'run-demo-0003',
      'goal': 'Đối chiếu số liệu vận hành trạm với nhật ký thiết bị.',
      'status': 'failed',
      'stepCount': 2,
      'createdAtUtc': _ago(const Duration(days: 1, hours: 2)),
      'completedAtUtc': _ago(const Duration(days: 1, hours: 1, minutes: 50)),
    },
  ];

  static Map<String, dynamic> agentRunDetail(String id) {
    final run = _agentRuns.firstWhere(
      (item) => item['id'] == id,
      orElse: () => _agentRuns.first,
    );
    return {
      ...run,
      'result': run['status'] == 'failed'
          ? null
          : 'Đã rà soát 14 hợp đồng, phát hiện 3 điểm cần bổ sung phụ lục.',
      'error': run['status'] == 'failed'
          ? 'Không tìm thấy tệp nhật ký thiết bị đã tải lên.'
          : null,
      'steps': _runSteps(run['status']?.toString() ?? ''),
    };
  }

  static List<Map<String, dynamic>> _runSteps(String status) {
    final steps = <Map<String, dynamic>>[
      {
        'ordinal': 1,
        'action': 'Đọc yêu cầu',
        'input': 'Rà soát hợp đồng quan trắc quý III',
        'observation': 'Có 14 hợp đồng cần kiểm tra.',
        'createdAtUtc': _ago(const Duration(hours: 6)),
      },
      {
        'ordinal': 2,
        'action': 'knowledge_base_query',
        'input': 'điều khoản phạt hợp đồng quan trắc',
        'observation': 'Trả về 6 đoạn văn bản liên quan.',
        'createdAtUtc': _ago(const Duration(hours: 5, minutes: 55)),
      },
      {
        'ordinal': 3,
        'action': 'Soạn kết luận',
        'input': '3 hợp đồng thiếu phụ lục',
        'observation': 'Đã tạo danh sách việc cần làm.',
        'createdAtUtc': _ago(const Duration(hours: 5, minutes: 45)),
      },
    ];
    if (status == 'running') {
      steps.add({
        'ordinal': 4,
        'action': 'web_search',
        'input': 'mức phạt trần hợp đồng dịch vụ môi trường',
        'observation': 'Đang tìm kiếm…',
        'createdAtUtc': _ago(const Duration(minutes: 3)),
      });
    }
    return steps;
  }

  static const List<Map<String, dynamic>> pendingApprovals = [
    {
      'id': 'ap-demo-0001',
      'actionName': 'Gửi email nhắc hạn nộp hồ sơ',
      'details':
          'Agent muốn gửi email tới 12 đơn vị quan trắc về hạn nộp báo cáo '
          'quý III (30/9/2026).',
    },
    {
      'id': 'ap-demo-0002',
      'actionName': 'Cập nhật CSDL nghiệp vụ',
      'details': 'Ghi 3 bản ghi đã hiệu chỉnh vào bảng ket_qua_quan_trac.',
    },
  ];

  static const List<Map<String, dynamic>> notifications = [
    {
      'id': 'n-demo-0001',
      'title': 'Tài liệu đã xử lý xong',
      'body': 'Bao-cao-quan-trac-thang-8.xlsx đã vector hóa thành công.',
      'isRead': false,
      'createdAt': '2026-09-16T02:10:00Z',
    },
    {
      'id': 'n-demo-0002',
      'title': 'Agent chờ phê duyệt',
      'body': 'Có 2 hành động đang chờ bạn xác nhận trước khi chạy.',
      'isRead': false,
      'createdAt': '2026-09-15T09:30:00Z',
    },
    {
      'id': 'n-demo-0003',
      'title': 'Tác vụ tự động hoàn tất',
      'body': 'Báo cáo không khí hằng ngày đã chạy lúc 05:00.',
      'isRead': true,
      'createdAt': '2026-09-15T05:00:00Z',
      // Completion-mode: không có run để mở — deep-link về tile lịch của task.
      'scheduledTaskId': 'sch-demo-0001',
    },
    {
      'id': 'n-demo-0004',
      'title': 'Agent chờ phê duyệt',
      'body': 'Có 2 hành động đang chờ bạn xác nhận trước khi chạy.',
      'isRead': false,
      'createdAt': '2026-09-15T09:30:00Z',
      // Agent-mode: deep-link thẳng vào chi tiết lần chạy.
      'agentRunId': 'run-demo-0001',
    },
  ];

  static Map<String, dynamic> apiKeysPage() => {
    'items': _apiKeys,
    'page': 1,
    'pageSize': 50,
    'totalCount': _apiKeys.length,
    'totalPages': 1,
  };

  static final List<Map<String, dynamic>> _apiKeys = [
    {
      'id': 'key-demo-0001',
      'name': 'Tích hợp cổng dịch vụ công',
      'maxRequests': 50000,
      'usedRequests': 12480,
      'expiresAtUtc': _ago(const Duration(days: -60)),
      'createdAtUtc': _ago(const Duration(days: 30)),
      'isActive': true,
    },
    {
      'id': 'key-demo-0002',
      'name': 'Bảng điều khiển nội bộ',
      'maxRequests': 0,
      'usedRequests': 214,
      'expiresAtUtc': null,
      'createdAtUtc': _ago(const Duration(days: 12)),
      'isActive': true,
    },
    {
      'id': 'key-demo-0003',
      'name': 'Khóa thử nghiệm',
      'maxRequests': 1000,
      'usedRequests': 1000,
      'expiresAtUtc': _ago(const Duration(days: 2)),
      'createdAtUtc': _ago(const Duration(days: 90)),
      'isActive': false,
    },
  ];

  static Map<String, dynamic> createdApiKey(String name) => {
    'id': 'key-demo-new',
    'name': name,
    'maxRequests': 0,
    'usedRequests': 0,
    'expiresAtUtc': _ago(const Duration(days: -90)),
    'createdAtUtc': _ago(Duration.zero),
    'isActive': true,
    'rawKey': 'cila_live_9f13c0d4b7a24e61a8f0demo',
  };

  static const Map<String, dynamic> textAnalyzeResult = {
    'sentiment': 'neutral',
    'score': 0.12,
    'tokens': 148,
    'entities': 'môi trường, quan trắc, Hà Nội',
  };

  static const Map<String, dynamic> textLanguageResult = {
    'language': 'vi',
    'languageName': 'Tiếng Việt',
    'confidence': 0.99,
  };

  static const Map<String, dynamic> textKeywordsResult = {
    'keywords': 'quan trắc, không khí, PM2.5, Hà Nội, QCVN 05:2023',
    'count': 8,
  };

  static const Map<String, dynamic> textEmbeddingsResult = {
    'dimensions': 768,
    'model': 'text-embedding-demo',
    'preview': '0.021, -0.004, 0.118, -0.093, …',
  };

  static const Map<String, dynamic> classifyResult = {
    'category': 'Kinh tế',
    'confidence': 0.92,
    'scores': 'Kinh tế 0.92 · Thể thao 0.05 · Giải trí 0.03',
  };

  static const Map<String, dynamic> visionAnalyzeResult = {
    'description':
        'Ảnh chụp một trạm quan trắc không khí ngoài trời, có cột cảm biến '
        'và hộp thiết bị.',
    'objects': 'trạm quan trắc, cảm biến, cột thép, cây xanh',
    'confidence': 0.94,
  };

  static const Map<String, dynamic> visionOcrResult = {
    'text':
        'TRẠM QUAN TRẮC KHÔNG KHÍ LB-02\nVị trí: Long Biên, Hà Nội\n'
        'PM2.5: 41.1 µg/m³',
    'lines': 3,
    'confidence': 0.91,
  };

  static const Map<String, dynamic> visionUploadResult = {
    'imagePath': '/api/files/f-demo-image',
  };

  static const Map<String, dynamic> visionBackgroundResult = {
    'imagePath': '/api/files/f-demo-image-nobg',
  };

  static const Map<String, dynamic> visionClassifyResult = {
    'category': 'tài liệu',
    'confidence': 0.88,
  };

  static const Map<String, dynamic> contentPipelineResult = {
    'finalContent':
        'Dự thảo bài viết về kiểm kê khí nhà kính gồm 4 phần: bối cảnh, lộ '
        'trình, số liệu minh họa và khuyến nghị cho đơn vị sự nghiệp.',
    'stages': [
      {
        'stageName': 'Nghiên cứu',
        'isSuccess': true,
        'content': 'Thu thập 6 nguồn về kiểm kê khí nhà kính.',
        'errorMessage': null,
      },
      {
        'stageName': 'Dàn ý',
        'isSuccess': true,
        'content': '4 phần, 9 mục nhỏ.',
        'errorMessage': null,
      },
      {
        'stageName': 'Bản nháp',
        'isSuccess': true,
        'content': 'Dự thảo 1.200 từ.',
        'errorMessage': null,
      },
      {
        'stageName': 'Kiểm chứng',
        'isSuccess': false,
        'content': null,
        'errorMessage': 'Chưa đối chiếu được số liệu với CSDL quốc gia.',
      },
    ],
  };

  // -------------------------------------------------------------- workspace

  static Map<String, dynamic> projectsPage() => {
    'items': _projects,
    'page': 1,
    'pageSize': 50,
    'totalCount': _projects.length,
    'totalPages': 1,
  };

  static final List<Map<String, dynamic>> _projects = [
    {
      'id': 'p-demo-0001',
      'name': 'Báo cáo tổng kết quan trắc 2026',
      'description': 'Tập hợp số liệu, biểu đồ và dự thảo báo cáo năm.',
      'icon': '📁',
      'instructions': 'Mọi số liệu phải ghi rõ nguồn và thời điểm đo.',
      'sessionCount': 3,
      'createdAt': _ago(const Duration(days: 20)),
      'updatedAt': _ago(const Duration(days: 5)),
    },
    {
      'id': 'p-demo-0002',
      'name': 'Hồ sơ cấp phép xả thải',
      'description': null,
      'icon': null,
      'instructions': null,
      'sessionCount': 1,
      'createdAt': _ago(const Duration(days: 9)),
      'updatedAt': _ago(const Duration(days: 1)),
    },
  ];

  /// Dự án mới tạo (`POST /api/projects`).
  static Map<String, dynamic> createdProject({
    Map<String, dynamic> body = const {},
  }) => {
    'id': 'p-demo-new',
    'name': (body['name'] ?? 'Dự án mới').toString(),
    'description': body['description'],
    'icon': body['icon'],
    'instructions': body['instructions'],
    'sessionCount': 0,
    'createdAt': _ago(Duration.zero),
    'updatedAt': _ago(Duration.zero),
  };

  static const List<Map<String, dynamic>> memories = [
    {
      'id': 'mem-demo-0001',
      'memoryType': 'Preference',
      'memoryKey': 'Đơn vị công tác',
      'memoryValue': 'Trung tâm Thông tin lưu trữ và Thư viện tài nguyên',
      'confidence': 0.96,
      'isConfirmed': true,
      'updatedAtUtc': '2026-09-12T02:00:00Z',
    },
    {
      'id': 'mem-demo-0002',
      'memoryType': 'Fact',
      'memoryKey': 'Định dạng báo cáo ưa thích',
      'memoryValue': 'Markdown, có bảng số liệu và biểu đồ',
      'confidence': 0.81,
      'isConfirmed': false,
      'updatedAtUtc': '2026-09-15T08:30:00Z',
    },
    {
      'id': 'mem-demo-0003',
      'memoryType': 'Context',
      'memoryKey': 'Dự án đang làm',
      'memoryValue': 'Báo cáo tổng kết quan trắc 2026',
      'confidence': 0.73,
      'isConfirmed': false,
      'updatedAtUtc': '2026-09-16T01:10:00Z',
    },
  ];

  static const Map<String, dynamic> customInstructions = {
    'aboutUser':
        'Chuyên viên tổng hợp số liệu quan trắc môi trường, thường làm báo cáo '
        'gửi lãnh đạo.',
    'responseStyle':
        'Trả lời ngắn gọn, nêu số liệu trước kết luận, luôn ghi nguồn.',
    'updatedAtUtc': '2026-09-14T07:20:00Z',
  };

  // ----------------------------------------------------------------- canvas

  static const List<Map<String, dynamic>> canvasArtifacts = [
    {
      'id': 'cv-demo-0001',
      'rootId': 'cv-demo-0001',
      'title': 'Báo cáo không khí tháng 8/2026',
      'kind': 'markdown',
      'language': 'markdown',
      'version': 3,
      'chatSessionId': sessionReportId,
      'updatedAt': '2026-09-16T02:05:00Z',
    },
    {
      'id': 'cv-demo-0002',
      'rootId': 'cv-demo-0002',
      'title': 'Bảng tổng hợp PM2.5 theo trạm',
      'kind': 'table',
      'language': null,
      'version': 1,
      'chatSessionId': sessionReportId,
      'updatedAt': '2026-09-15T10:12:00Z',
    },
    {
      'id': 'cv-demo-0003',
      'rootId': 'cv-demo-0003',
      'title': 'Biểu đồ so sánh 2025 – 2026',
      'kind': 'chart',
      'language': null,
      'version': 2,
      'chatSessionId': null,
      'updatedAt': '2026-09-14T04:00:00Z',
    },
  ];

  static const Map<String, dynamic> canvasArtifactDetail = {
    'id': 'cv-demo-0001',
    'rootId': 'cv-demo-0001',
    'title': 'Báo cáo không khí tháng 8/2026',
    'kind': 'markdown',
    'language': 'markdown',
    'version': 3,
    'createdAt': '2026-09-16T02:05:00Z',
    'content':
        '# Báo cáo quan trắc không khí tháng 8/2026\n\n'
        '## 1. Kết quả chính\n\n'
        '- PM2.5 trung bình: 35,6 µg/m³ (giảm 18,4% so với cùng kỳ).\n'
        '- Số ngày vượt ngưỡng QCVN 05:2023: 6 ngày.\n\n'
        '## 2. Khuyến nghị\n\n'
        'Tăng cường kiểm soát xe tải nặng qua cầu Chương Dương.\n',
  };

  static const List<Map<String, dynamic>> canvasVersions = [
    {'version': 3, 'createdAt': '2026-09-16T02:05:00Z'},
    {'version': 2, 'createdAt': '2026-09-15T09:40:00Z'},
    {'version': 1, 'createdAt': '2026-09-14T05:20:00Z'},
  ];

  // ------------------------------------------------------------------ admin

  static Map<String, dynamic> tenantsPage() => {
    'items': _tenants,
    'page': 1,
    'pageSize': 50,
    'totalCount': _tenants.length,
    'totalPages': 1,
  };

  static final List<Map<String, dynamic>> _tenants = [
    {
      'id': tenantId,
      'name': tenantName,
      'agentDisplayName': agentName,
      'hasLogo': true,
      'logoUpdatedAt': _ago(const Duration(days: 12)),
      'createdAt': _ago(const Duration(days: 120)),
      'userCount': 18,
      'databaseConnectionCount': 2,
    },
    {
      'id': 'tenant-demo-0002',
      'name': 'Sở Tài nguyên và Môi trường tỉnh Bắc Ninh',
      'agentDisplayName': 'Trợ lý TNMT Bắc Ninh',
      'hasLogo': false,
      'logoUpdatedAt': null,
      'createdAt': _ago(const Duration(days: 64)),
      'userCount': 7,
      'databaseConnectionCount': 1,
    },
  ];

  static const List<Map<String, dynamic>> tenantOptions = [
    {'id': tenantId, 'name': 'Trung tâm Thông tin lưu trữ (mặc định)'},
    {'id': 'tenant-demo-0002', 'name': 'Sở TNMT Bắc Ninh'},
  ];

  static Map<String, dynamic> mcpServersPage() => {
    'items': _mcpServers,
    'page': 1,
    'pageSize': 50,
    'totalCount': _mcpServers.length,
    'totalPages': 1,
  };

  static final List<Map<String, dynamic>> _mcpServers = [
    {
      'id': 'mcp-demo-0001',
      'name': 'Kho dữ liệu quốc gia',
      'url': 'https://mcp.monre.gov.vn/sse',
      'isActive': true,
      'trustReadOnlyAnnotations': true,
      'hasHeaders': true,
      'authMode': 'OAuth',
      'oauthClientId': 'cila-mobile',
      'oauthTokenUrl': 'https://oauth.monre.gov.vn/token',
      'oauthAuthorizeUrl': 'https://oauth.monre.gov.vn/authorize',
      'oauthScopes': 'read metrics',
    },
    {
      'id': 'mcp-demo-0002',
      'name': 'Máy chủ nội bộ (filesystem)',
      'url': 'http://10.0.2.2:9100/sse',
      'isActive': false,
      'trustReadOnlyAnnotations': false,
      'hasHeaders': true,
      'authMode': 'Static',
      'oauthClientId': null,
      'oauthTokenUrl': null,
      'oauthAuthorizeUrl': null,
      'oauthScopes': null,
    },
  ];

  static const List<Map<String, dynamic>> mcpCatalog = [
    {
      'id': 'filesystem',
      'url':
          'https://github.com/modelcontextprotocol/servers/tree/main/src/filesystem',
      'description': 'Đọc/ghi tệp trong thư mục được cấp phép.',
    },
    {
      'id': 'postgres',
      'url':
          'https://github.com/modelcontextprotocol/servers/tree/main/src/postgres',
      'description': 'Truy vấn PostgreSQL chỉ đọc.',
    },
  ];

  /// Sơ đồ schema của một kết nối — dữ liệu cho màn `DatabaseDiagramScreen`.
  ///
  /// Có đủ ba tình huống mà giao diện phải xử lý: bảng nhiều cột, một cạnh vẽ
  /// được, và một cạnh trỏ ra ngoài sơ đồ (`targetIncluded: false`).
  static Map<String, dynamic> databaseSchema({
    String connectionId = 'db-demo-0001',
  }) => {
    'connectionId': connectionId,
    'name': 'CSDL nghiệp vụ quan trắc',
    'provider': 'Postgres',
    'isActive': true,
    'isIndexed': true,
    'indexStatus': 'Completed',
    'lastIndexedAtUtc': _ago(const Duration(hours: 8)),
    'tableCount': 3,
    'totalTableCount': 3,
    'truncated': false,
    'tables': [
      {
        'schema': 'public',
        'name': 'stations',
        'qualifiedName': 'public.stations',
        'columns': [
          {
            'name': 'id',
            'dataType': 'integer',
            'isNullable': false,
            'isPrimaryKey': true,
            'isForeignKey': false,
          },
          {
            'name': 'code',
            'dataType': 'character varying',
            'isNullable': false,
            'isPrimaryKey': false,
            'isForeignKey': false,
          },
          {
            'name': 'name',
            'dataType': 'character varying',
            'isNullable': true,
            'isPrimaryKey': false,
            'isForeignKey': false,
          },
        ],
        'foreignKeys': const <String>[],
      },
      {
        'schema': 'public',
        'name': 'measurements',
        'qualifiedName': 'public.measurements',
        'columns': [
          {
            'name': 'id',
            'dataType': 'bigint',
            'isNullable': false,
            'isPrimaryKey': true,
            'isForeignKey': false,
          },
          {
            'name': 'station_id',
            'dataType': 'integer',
            'isNullable': false,
            'isPrimaryKey': false,
            'isForeignKey': true,
          },
          {
            'name': 'measured_at',
            'dataType': 'timestamp without time zone',
            'isNullable': false,
            'isPrimaryKey': false,
            'isForeignKey': false,
          },
          {
            'name': 'value',
            'dataType': 'numeric',
            'isNullable': true,
            'isPrimaryKey': false,
            'isForeignKey': false,
          },
        ],
        'foreignKeys': [
          {
            'column': 'station_id',
            'referencedTable': 'public.stations',
            'referencedColumn': 'id',
            'raw': 'station_id → public.stations.id',
            'isResolved': true,
          },
        ],
      },
      {
        'schema': 'public',
        'name': 'thresholds',
        'qualifiedName': 'public.thresholds',
        'columns': [
          {
            'name': 'id',
            'dataType': 'integer',
            'isNullable': false,
            'isPrimaryKey': true,
            'isForeignKey': false,
          },
          {
            'name': 'parameter_id',
            'dataType': 'integer',
            'isNullable': false,
            'isPrimaryKey': false,
            'isForeignKey': true,
          },
        ],
        'foreignKeys': [
          {
            'column': 'parameter_id',
            'referencedTable': 'public.parameters',
            'referencedColumn': 'id',
            'raw': 'parameter_id → public.parameters.id',
            'isResolved': true,
          },
        ],
      },
    ],
    'relations': [
      {
        'fromTable': 'public.measurements',
        'fromColumn': 'station_id',
        'toTable': 'public.stations',
        'toColumn': 'id',
        'targetIncluded': true,
      },
      {
        'fromTable': 'public.thresholds',
        'fromColumn': 'parameter_id',
        'toTable': 'public.parameters',
        'toColumn': 'id',
        'targetIncluded': false,
      },
    ],
  };

  static Map<String, dynamic> databaseConnectionsPage() => {
    'items': _databaseConnections,
    'page': 1,
    'pageSize': 50,
    'totalCount': _databaseConnections.length,
    'totalPages': 1,
  };

  static final List<Map<String, dynamic>> _databaseConnections = [
    {
      'id': 'db-demo-0001',
      'tenantId': null,
      'tenantName': null,
      'name': 'CSDL nghiệp vụ quan trắc',
      'provider': 'PostgreSQL',
      'isActive': true,
      'allowWrites': false,
      'isIndexed': true,
      'indexStatus': 'Completed',
      'lastIndexError': null,
      'lastIndexedAtUtc': _ago(const Duration(hours: 8)),
    },
    {
      'id': 'db-demo-0002',
      'tenantId': 'tenant-demo-0002',
      'tenantName': 'Sở TNMT Bắc Ninh',
      'name': 'CSDL địa phương',
      'provider': 'SQLServer',
      'isActive': true,
      'allowWrites': true,
      'isIndexed': false,
      'indexStatus': 'Failed',
      'lastIndexError': 'Sai thông tin đăng nhập.',
      'lastIndexedAtUtc': null,
    },
  ];

  static Map<String, dynamic> loraAdaptersPage() => {
    'items': _loraAdapters,
    'page': 1,
    'pageSize': 50,
    'totalCount': _loraAdapters.length,
    'totalPages': 1,
  };

  static final List<Map<String, dynamic>> _loraAdapters = [
    {
      'id': 'lora-demo-0001',
      'name': 'Phong cách văn bản hành chính',
      'description': 'Tinh chỉnh cho văn phong công văn, báo cáo.',
      'scale': 0.8,
      'targetModelId': 'llama-3.1-8b',
      'fileSizeBytes': 8942080,
      'isActive': true,
    },
    {
      'id': 'lora-demo-0002',
      'name': 'Trích xuất số liệu bảng',
      'description': null,
      'scale': 1.0,
      'targetModelId': 'qwen2.5-14b',
      'fileSizeBytes': 17301504,
      'isActive': false,
    },
  ];

  static const Map<String, dynamic> widgetSettings = {
    'isActive': true,
    'allowedOrigins': ['https://cila.gov.vn', 'https://dichvucong.cila.gov.vn'],
    'requestsPerMinute': 30,
    'requestsPerDay': 5000,
    'widgetTitle': 'Trợ lý CILA',
    'welcomeMessage': 'Xin chào, tôi có thể giúp gì cho bạn hôm nay?',
    'brandColor': '#1E3A8A',
    'logoUrl': logoPath,
    'position': 'bottom-right',
    'rotatedAtUtc': '2026-08-30T02:00:00Z',
  };

  static const Map<String, dynamic> rotatedWidgetKey = {
    'rawKey': 'widget_live_4d21b8e7c0a94f13demo',
  };

  static Map<String, dynamic> usersPage({
    int page = 1,
    int pageSize = 20,
    String search = '',
  }) {
    final query = search.trim().toLowerCase();
    final filtered = query.isEmpty
        ? _users
        : _users
              .where(
                (user) => '${user['email']} ${user['fullName']}'
                    .toLowerCase()
                    .contains(query),
              )
              .toList();
    final start = (page - 1) * pageSize;
    final rows = filtered.skip(start).take(pageSize).toList();
    return {
      'items': rows,
      'page': page,
      'pageSize': pageSize,
      'totalCount': filtered.length,
      'totalPages': (filtered.length / pageSize).ceil(),
    };
  }

  static final List<Map<String, dynamic>> _users = [
    {
      'id': 'u-0001',
      'email': 'admin@cila.gov.vn',
      'fullName': 'Nguyễn Văn Duy',
      'role': 'Admin',
      'isActive': true,
      'createdAt': _ago(const Duration(days: 120)),
      'updatedAt': _ago(const Duration(days: 3)),
      'failedLoginAttempts': 0,
      'lockoutEnd': null,
      'tenantId': tenantId,
    },
    {
      'id': 'u-0002',
      'email': 'lan.pham@cila.gov.vn',
      'fullName': 'Phạm Thị Lan',
      'role': 'Member',
      'isActive': true,
      'createdAt': _ago(const Duration(days: 90)),
      'updatedAt': _ago(const Duration(days: 10)),
      'failedLoginAttempts': 0,
      'lockoutEnd': null,
      'tenantId': tenantId,
    },
    {
      'id': 'u-0003',
      'email': 'hung.tran@cila.gov.vn',
      'fullName': 'Trần Quốc Hùng',
      'role': 'Member',
      'isActive': false,
      'createdAt': _ago(const Duration(days: 60)),
      'updatedAt': _ago(const Duration(days: 2)),
      'failedLoginAttempts': 5,
      'lockoutEnd': _ago(const Duration(minutes: -12)),
      'tenantId': tenantId,
    },
    {
      'id': 'u-0004',
      'email': 'thu.le@bacninh.gov.vn',
      'fullName': 'Lê Minh Thu',
      'role': 'Admin',
      'isActive': true,
      'createdAt': _ago(const Duration(days: 30)),
      'updatedAt': _ago(const Duration(days: 4)),
      'failedLoginAttempts': 0,
      'lockoutEnd': null,
      'tenantId': 'tenant-demo-0002',
    },
  ];

  static Map<String, dynamic> createdUser({
    required String email,
    required String fullName,
    String role = 'Member',
  }) => {
    'id': 'u-new',
    'email': email,
    'fullName': fullName,
    'role': role,
    'isActive': true,
    'createdAt': _ago(Duration.zero),
    'updatedAt': _ago(Duration.zero),
    'failedLoginAttempts': 0,
    'lockoutEnd': null,
    'tenantId': tenantId,
  };

  static Map<String, dynamic> auditPage({int page = 1, int pageSize = 25}) {
    final start = (page - 1) * pageSize;
    final rows = _audit.skip(start).take(pageSize).toList();
    return {
      'items': rows,
      'page': page,
      'pageSize': pageSize,
      'totalCount': _audit.length,
      'totalPages': (_audit.length / pageSize).ceil(),
    };
  }

  static final List<Map<String, dynamic>> _audit = [
    {
      'id': 'log-demo-0001',
      'actorUserId': 'u-0001',
      'actorType': 'User',
      'action': 'Login',
      'entityType': 'Session',
      'entityId': 'sess-1',
      'correlationId': 'c-1',
      'detailsJson': '{"ip":"10.0.0.24","agent":"Chrome/152"}',
      'createdAtUtc': _ago(const Duration(minutes: 42)),
    },
    {
      'id': 'log-demo-0002',
      'actorUserId': 'u-0001',
      'actorType': 'User',
      'action': 'UpdateRole',
      'entityType': 'User',
      'entityId': 'u-0002',
      'correlationId': 'c-2',
      'detailsJson': '{"from":"Member","to":"Admin"}',
      'createdAtUtc': _ago(const Duration(hours: 5)),
    },
    {
      'id': 'log-demo-0003',
      'actorUserId': null,
      'actorType': 'System',
      'action': 'DocumentVectorized',
      'entityType': 'Document',
      'entityId': 'doc-demo-0003',
      'correlationId': 'c-3',
      'detailsJson': '{"chunks":84,"durationMs":15320}',
      'createdAtUtc': _ago(const Duration(hours: 9)),
    },
    {
      'id': 'log-demo-0004',
      'actorUserId': 'u-0002',
      'actorType': 'User',
      'action': 'CreateApiKey',
      'entityType': 'ApiKey',
      'entityId': 'key-demo-0002',
      'correlationId': 'c-4',
      'detailsJson': '{"maxRequests":0}',
      'createdAtUtc': _ago(const Duration(days: 1, hours: 3)),
    },
    {
      'id': 'log-demo-0005',
      'actorUserId': null,
      'actorType': 'Agent',
      'action': 'RunAgentTask',
      'entityType': 'AgentRun',
      'entityId': 'run-demo-0001',
      'correlationId': 'c-5',
      'detailsJson': '{"steps":7,"status":"completed"}',
      'createdAtUtc': _ago(const Duration(days: 1, hours: 8)),
    },
  ];

  static const Map<String, dynamic> auditFacets = {
    'actorTypes': ['User', 'System', 'Agent'],
    'actions': [
      'Login',
      'UpdateRole',
      'DocumentVectorized',
      'CreateApiKey',
      'RunAgentTask',
    ],
    'entityTypes': ['Session', 'User', 'Document', 'ApiKey', 'AgentRun'],
  };

  // --------------------------------------------------------------- dashboard

  /// `GET /api/dashboard/stats` — đúng hợp đồng `DashboardStats` mà web cũng đọc.
  ///
  /// Dữ liệu cố tình chứa các ca khó thay vì toàn số đẹp: một đơn vị **vượt** hạn mức,
  /// một đơn vị **sắp chạm** ngưỡng cảnh báo 80%, một đơn vị gói **không giới hạn** và
  /// một đơn vị **chưa gán gói**. Nhờ vậy mọi nhánh màu của thanh tiến độ và mọi nhánh
  /// chữ của phần hạn mức đều có dữ liệu để chạy qua khi render.
  static Map<String, dynamic> dashboardStats({int days = 30}) => {
    'periodDays': days,
    'totalUsers': 18,
    'totalTenants': 4,
    'totalSessions': 342,
    'totalDocuments': 57,
    'myUsage': const {
      'questions': 128,
      'answers': 126,
      'promptTokens': 486320,
      'completionTokens': 271480,
      'sessions': 24,
    },
    'cockpit': {
      'tokens': {
        'promptTokens': 4820640,
        'completionTokens': 2310980,
        'messages': 3412,
        // Token agent-run để RIÊNG: một lần chạy gồm nhiều lượt suy luận nối tiếp nên
        // là nguồn chi phí lớn nhất, gộp chung vào lượt chat là giấu mất nó.
        'agentRunPromptTokens': 1240500,
        'agentRunCompletionTokens': 512300,
        'agentRuns': 63,
        'daily': [
          for (var i = days - 1; i >= 0; i--)
            {
              'date': _dateDaysAgo(i),
              'promptTokens': 118000 + (days - 1 - i) * 1450,
              'completionTokens': 61000 + (days - 1 - i) * 830,
              'messages': 88 + (days - 1 - i),
            },
        ],
        'byModel': const [
          {
            'modelName': 'qwen2.5:14b-instruct-q4_K_M',
            'promptTokens': 2980000,
            'completionTokens': 1420000,
            'messages': 1980,
          },
          {
            'modelName': 'llama3.1:8b-instruct-q8_0',
            'promptTokens': 1340640,
            'completionTokens': 620980,
            'messages': 1120,
          },
          {
            'modelName': 'bge-m3',
            'promptTokens': 500000,
            'completionTokens': 270000,
            'messages': 312,
          },
        ],
      },
      'users': {
        'dailyActive': 7,
        'weeklyActive': 12,
        'monthlyActive': 18,
        'totalUsers': 18,
        'newUsers': 3,
        'adoptionPct': 100,
        // Chuỗi theo ngày ở khối người dùng CỐ ĐỊNH 7 ngày — DAU/WAU/MAU luôn tính
        // trên 1/7/30 ngày gần nhất, không theo kỳ đang chọn.
        'daily': [
          for (var i = 6; i >= 0; i--)
            {'date': _dateDaysAgo(i), 'count': 4 + i % 5},
        ],
        'topUsers': const [
          {
            'userId': 'u-0001',
            'name': 'Nguyễn Văn Duy',
            'email': 'admin@cila.gov.vn',
            'questions': 128,
            'answers': 126,
            'promptTokens': 486320,
            'completionTokens': 271480,
          },
          {
            'userId': 'u-0002',
            'name': 'Phạm Thị Lan',
            'email': 'lan.pham@cila.gov.vn',
            'questions': 96,
            'answers': 94,
            'promptTokens': 351200,
            'completionTokens': 198640,
          },
        ],
      },
      'spend': const {
        'totalTokens': 7131620,
        'totalAgentRunTokens': 1752800,
        'top3SharePct': 96,
        'topTenantSharePct': 74,
        'topTenantName': tenantName,
        'byTenant': [
          {
            'tenantId': tenantId,
            'tenantName': tenantName,
            'promptTokens': 3520000,
            'completionTokens': 1680000,
            'messages': 2410,
            'agentRunPromptTokens': 1240500,
            'agentRunCompletionTokens': 512300,
            'agentRuns': 63,
          },
          {
            'tenantId': 'tenant-demo-0002',
            'tenantName': 'Sở Tài nguyên và Môi trường tỉnh Bắc Ninh',
            'promptTokens': 1300640,
            'completionTokens': 630980,
            'messages': 1002,
            'agentRunPromptTokens': 0,
            'agentRunCompletionTokens': 0,
            'agentRuns': 0,
          },
        ],
      },
      'quota': const {
        'tenantsOnPlan': 3,
        'tenantsOverThreshold': 1,
        'tenantsOverLimit': 1,
        'tenantsWithoutPlan': 1,
        'totalMonthlyLimit': 10000000,
        'totalMonthlyUsed': 11186600,
        'totalCreditBalance': 1250000,
        'byTenant': [
          {
            'tenantId': tenantId,
            'tenantName': tenantName,
            'planName': 'Gói cơ quan — 5 triệu token',
            'monthlyLimit': 5000000,
            'usedTokens': 5240000,
            'utilizationPct': 104,
            'creditBalance': 250000,
            'isUnlimited': false,
          },
          {
            'tenantId': 'tenant-demo-0002',
            'tenantName': 'Sở Tài nguyên và Môi trường tỉnh Bắc Ninh',
            'planName': 'Gói địa phương — 5 triệu token',
            'monthlyLimit': 5000000,
            'usedTokens': 4120400,
            'utilizationPct': 82,
            'creditBalance': 1000000,
            'isUnlimited': false,
          },
          {
            'tenantId': 'tenant-demo-0003',
            'tenantName': 'Trung tâm Dữ liệu và Hạ tầng số',
            'planName': 'Gói nội bộ không giới hạn',
            'monthlyLimit': 0,
            'usedTokens': 1820400,
            'utilizationPct': 0,
            'creditBalance': 0,
            'isUnlimited': true,
          },
          {
            'tenantId': 'tenant-demo-0004',
            'tenantName': 'Chi cục Bảo vệ môi trường tỉnh Hải Dương',
            'planName': null,
            // Backend đặt `isUnlimited = !hasPlan || limit <= 0`, nên đơn vị CHƯA GÁN GÓI
            // cũng là "không giới hạn" (không có trần nào để tính %). Khác với đơn vị có
            // gói không giới hạn chỉ ở `planName` — màn phải đọc đúng cả hai.
            'monthlyLimit': 0,
            'usedTokens': 46200,
            'utilizationPct': 0,
            'creditBalance': 0,
            'isUnlimited': true,
          },
        ],
      },
      'documents': const {
        'total': 57,
        'indexed': 51,
        'pending': 4,
        'failed': 2,
        'totalChunks': 12840,
        'byStatus': [
          {'key': 'Indexed', 'count': 51},
          {'key': 'Pending', 'count': 4},
          {'key': 'Failed', 'count': 2},
        ],
      },
      'activity': const {
        'total': 1842,
        'topActions': [
          {'key': 'Login', 'count': 640},
          {'key': 'ChatCompletion', 'count': 512},
          {'key': 'RunAgentTask', 'count': 63},
        ],
      },
      'performance': const {
        'samples': 3412,
        'avgLatencyMs': 1840,
        'p95LatencyMs': 6120,
        // Lần chạy agent được đo riêng: một lần chạy là nhiều lượt suy luận cộng lại.
        'agentRunSamples': 63,
        'agentRunAvgLatencyMs': 42800,
        'agentRunP95LatencyMs': 96500,
      },
      'alerts': const {
        'nearLimitTenants': 1,
        'overLimitTenants': 1,
        'expiringGrantCount': 2,
        'expiringGrants': [
          {
            'tenantId': tenantId,
            'tenantName': tenantName,
            'remainingTokens': 380000,
            'daysLeft': 9,
          },
          {
            'tenantId': 'tenant-demo-0002',
            'tenantName': 'Sở Tài nguyên và Môi trường tỉnh Bắc Ninh',
            'remainingTokens': 120000,
            'daysLeft': 3,
          },
        ],
      },
    },
  };

  /// `GET /api/dashboard/export.csv` — nội dung tệp báo cáo (đơn vị × model).
  ///
  /// Bám đúng dạng backend phát ra: BOM UTF-8 (Excel cần để đọc đúng tiếng Việt),
  /// phân cách bằng dấu `,`, xuống dòng `\r\n`, và **ba cột agent-run nằm cuối** để
  /// người đang đọc theo 7 cột đầu không bị lệch. Màn dashboard ghi thẳng chuỗi này
  /// xuống tệp, nên dữ liệu mẫu phải khớp — nếu không thì đường ghi tệp được kiểm
  /// trên một chuỗi khác với thứ backend thật trả về.
  static final String dashboardCsv =
      '\uFEFF'
      'Mã đơn vị,Tên đơn vị,Model,Số câu trả lời,Prompt tokens,Completion tokens,'
      'Tổng token,Số lần chạy agent,Prompt tokens agent,Completion tokens agent\r\n'
      '$tenantId,$tenantName,qwen2.5:14b-instruct-q4_K_M,1980,2980000,1420000,'
      '4400000,41,824500,341200\r\n'
      '$tenantId,$tenantName,llama3.1:8b-instruct-q8_0,430,360640,190980,'
      '551620,12,148000,84200\r\n'
      'tenant-demo-0002,Sở Tài nguyên và Môi trường tỉnh Bắc Ninh,bge-m3,'
      '312,500000,270000,770000,10,268000,87000\r\n';

  // ---------------------------------------------------- hạn mức (quota admin)

  /// `GET /api/admin/quota/plans` — endpoint trả list TRẮNG (không envelope).
  ///
  /// Có đủ ba trạng thái mà màn quản trị phân biệt: gói đang dùng, gói KHÔNG GIỚI HẠN
  /// (`monthlyTokenLimit == 0`, khác hẳn gói 0 token) và gói đã ngừng (không gán được).
  static const List<Map<String, dynamic>> quotaPlans = [
    {
      'id': 'plan-demo-0001',
      'name': 'Gói cơ quan — 5 triệu token',
      'monthlyTokenLimit': 5000000,
      'isActive': true,
      'tenantCount': 1,
    },
    {
      'id': 'plan-demo-0002',
      'name': 'Gói nội bộ không giới hạn',
      'monthlyTokenLimit': 0,
      'isActive': true,
      'tenantCount': 1,
    },
    {
      'id': 'plan-demo-0003',
      'name': 'Gói dùng thử 2025',
      'monthlyTokenLimit': 500000,
      'isActive': false,
      'tenantCount': 0,
    },
  ];

  /// `GET /api/admin/quota/tenants` — trạng thái hạn mức của mọi đơn vị.
  ///
  /// Bốn ca để mọi nhánh chữ của màn chạy qua: vượt trần (104%), sắp chạm ngưỡng
  /// (82%), gói không giới hạn, và chưa gán gói (`planId == null`).
  static final List<Map<String, dynamic>> tenantQuotas = [
    {
      'tenantId': tenantId,
      'tenantName': tenantName,
      'planId': 'plan-demo-0001',
      'planName': 'Gói cơ quan — 5 triệu token',
      'monthlyTokenLimit': 5000000,
      'renewalAtUtc': _ago(const Duration(days: -21)),
      'usedTokens': 5240000,
      'utilizationPct': 104,
      'isUnlimited': false,
      'creditBalance': 250000,
      'activeGrantCount': 2,
      'grantRemainingTokens': 380000,
    },
    {
      'tenantId': 'tenant-demo-0002',
      'tenantName': 'Sở Tài nguyên và Môi trường tỉnh Bắc Ninh',
      'planId': 'plan-demo-0002',
      'planName': 'Gói nội bộ không giới hạn',
      'monthlyTokenLimit': 0,
      'renewalAtUtc': null,
      'usedTokens': 4120400,
      'utilizationPct': 0,
      'isUnlimited': true,
      'creditBalance': 1000000,
      'activeGrantCount': 0,
      'grantRemainingTokens': 0,
    },
    {
      'tenantId': 'tenant-demo-0003',
      'tenantName': 'Trung tâm Dữ liệu và Hạ tầng số',
      'planId': null,
      'planName': null,
      // `isUnlimited` CŨNG đúng cho đơn vị chưa gán gói (server đặt `!hasPlan || limit <= 0`):
      // không có trần thì không có gì để tính %. Màn phân biệt "chưa gán gói" bằng
      // `planId == null`, không bằng cờ này.
      'monthlyTokenLimit': 0,
      'renewalAtUtc': null,
      'usedTokens': 0,
      'utilizationPct': 0,
      'isUnlimited': true,
      'creditBalance': 0,
      'activeGrantCount': 0,
      'grantRemainingTokens': 0,
    },
  ];

  /// `GET /api/admin/quota/tenants/{id}/grants` — token cấp thêm của một đơn vị.
  ///
  /// Ba ca quyết định giao diện: còn nguyên (gỡ được), đã tiêu một phần (server chặn
  /// gỡ nên màn không hiện nút) và đã hết hạn (tô cảnh báo).
  static final List<Map<String, dynamic>> tenantGrants = [
    {
      'id': 'grant-demo-0001',
      'tokens': 500000,
      'usedTokens': 120000,
      'remainingTokens': 380000,
      'expiresAtUtc': _ago(const Duration(days: -9)),
      'reason': 'Bù hạn mức quý IV',
      'isExpired': false,
    },
    {
      'id': 'grant-demo-0002',
      'tokens': 250000,
      'usedTokens': 0,
      'remainingTokens': 250000,
      'expiresAtUtc': null,
      'reason': 'Thử nghiệm trợ lý pháp chế',
      'isExpired': false,
    },
    {
      'id': 'grant-demo-0003',
      'tokens': 100000,
      'usedTokens': 100000,
      'remainingTokens': 0,
      'expiresAtUtc': _ago(const Duration(days: 30)),
      'reason': 'Đợt tập huấn tháng 6',
      'isExpired': true,
    },
  ];

  // ----------------------------------------------------------------- speech

  static const Map<String, dynamic> voiceToken = {
    'token': 'mock-livekit-token',
    'room': 'omni-room',
    'agent': true,
    'agentUnavailableReason': null,
  };

  static const Map<String, dynamic> health = {'status': 'Healthy'};
}
