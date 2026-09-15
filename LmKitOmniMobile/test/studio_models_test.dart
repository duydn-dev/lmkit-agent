import 'package:flutter_test/flutter_test.dart';
import 'package:lmkit_omni_mobile/features/studio/studio_models.dart';

void main() {
  test('custom agent đọc công cụ và tài liệu ghim', () {
    final agent = CustomAgentModel.fromJson({
      'id': 'AG1',
      'name': 'Trợ lý pháp lý',
      'personaPrompt': 'Trả lời ngắn gọn, trích dẫn điều luật.',
      'allowedTools': ['QueryKnowledgeBase', 'AnalyzeText'],
      'knowledgeDocumentIds': ['D1', 'D2'],
      'isSharedWithTenant': true,
      'isOwner': true,
    });

    expect(agent.allowedTools, ['QueryKnowledgeBase', 'AnalyzeText']);
    expect(agent.knowledgeDocumentIds, ['D1', 'D2']);
    expect(agent.isSharedWithTenant, isTrue);
  });

  test('allowedTools null nghĩa là dùng bộ công cụ mặc định', () {
    final agent = CustomAgentModel.fromJson({
      'id': 'AG2',
      'name': 'Mặc định',
      'personaPrompt': 'x',
    });

    expect(agent.allowedTools, isNull);
    expect(agent.knowledgeDocumentIds, isEmpty);
  });

  test('tool catalog đọc name/label/description', () {
    final tool = AgentToolModel.fromJson({
      'name': 'RunCode',
      'label': 'Chạy mã JavaScript',
      'description': 'Thực thi đoạn mã ngắn trong sandbox.',
    });

    expect(tool.name, 'RunCode');
    expect(tool.label, 'Chạy mã JavaScript');
    expect(tool.description, isNotEmpty);
  });

  test('tài liệu trong picker đọc trạng thái vector hóa', () {
    final document = KnowledgeDocModel.fromJson({
      'id': 'D1',
      'fileName': 'hop-dong.pdf',
      'isVectorized': true,
    });

    expect(document.fileName, 'hop-dong.pdf');
    expect(document.isVectorized, isTrue);
  });

  test('agent run detail đọc các bước theo thứ tự', () {
    final run = AgentRunDetailModel.fromJson({
      'id': 'R1',
      'goal': 'Tổng hợp báo cáo quý',
      'status': 'Running',
      'result': null,
      'steps': [
        {
          'ordinal': 1,
          'action': 'QueryKnowledgeBase',
          'input': 'doanh thu quý',
          'observation': '3 đoạn liên quan',
          'createdAtUtc': '2026-09-12T01:00:00Z',
        },
        {
          'ordinal': 2,
          'action': 'AnalyzeText',
          'input': 'so sánh quý trước',
          'observation': 'đã tóm tắt',
        },
      ],
    });

    expect(run.isRunning, isTrue);
    expect(run.steps.length, 2);
    expect(run.steps.first.action, 'QueryKnowledgeBase');
    expect(run.steps.first.createdAtUtc, isNotNull);
    expect(run.steps.last.observation, 'đã tóm tắt');
  });

  test('agent run đã xong không còn isRunning', () {
    final run = AgentRunDetailModel.fromJson({
      'id': 'R2',
      'goal': 'x',
      'status': 'Completed',
      'result': 'Xong',
    });

    expect(run.isRunning, isFalse);
    expect(run.result, 'Xong');
    expect(run.steps, isEmpty);
  });
}
