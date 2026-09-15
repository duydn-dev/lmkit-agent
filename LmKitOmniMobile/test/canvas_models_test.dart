import 'package:flutter_test/flutter_test.dart';
import 'package:lmkit_omni_mobile/features/canvas/canvas_models.dart';

void main() {
  test('artifact list đọc đúng root, version và phiên chat', () {
    final artifact = CanvasArtifactModel.fromJson({
      'id': 'A1',
      'rootId': 'R1',
      'title': 'Bản nháp',
      'kind': 'text',
      'version': 3,
      'chatSessionId': 'S1',
      'updatedAt': '2026-09-10T08:00:00Z',
    });

    expect(artifact.rootId, 'R1');
    expect(artifact.version, 3);
    expect(artifact.chatSessionId, 'S1');
    expect(artifact.updatedAt, isNotNull);
  });

  test('artifact detail giữ nguyên nội dung nhiều dòng', () {
    const content = 'Dòng 1\nDòng 2\nDòng 3';
    final detail = CanvasArtifactDetailModel.fromJson({
      'id': 'A1',
      'rootId': 'R1',
      'title': 'Bản nháp',
      'kind': 'markdown',
      'content': content,
      'version': 2,
    });

    expect(detail.content, content);
    expect(detail.kind, 'markdown');
  });

  test('version list đọc số phiên bản', () {
    final versions = [
      {'version': 2, 'createdAt': '2026-09-11T00:00:00Z'},
      {'version': 1},
    ].map(CanvasVersionModel.fromJson).toList();

    expect(versions.first.version, 2);
    expect(versions.last.createdAt, isNull);
  });
}
