import 'package:flutter_test/flutter_test.dart';
import 'package:lmkit_omni_mobile/features/studio/studio_models.dart';

void main() {
  group('Scheduler time fields', () {
    test('parses UTC clock minutes and rejects invalid values', () {
      expect(ScheduledTaskInput.parseTime('01:00'), 60);
      expect(ScheduledTaskInput.parseTime('8:05'), 485);
      expect(ScheduledTaskInput.parseTime('24:00'), isNull);
      expect(ScheduledTaskInput.parseTime('12:60'), isNull);
      expect(ScheduledTaskInput.parseTime('1:5'), isNull);
    });

    test('parses strict local one-time date and rejects rollover dates', () {
      expect(
        ScheduledTaskInput.parseLocalDateTime('2026-09-27 08:05'),
        isNotNull,
      );
      expect(ScheduledTaskInput.parseLocalDateTime('2026-02-29 08:05'), isNull);
      expect(ScheduledTaskInput.parseLocalDateTime('2026-13-01 08:05'), isNull);
      expect(
        ScheduledTaskInput.parseLocalDateTime('2026-09-27T08:05'),
        isNotNull,
      );
    });
  });
}
