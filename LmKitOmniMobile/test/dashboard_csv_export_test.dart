import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:lmkit_omni_mobile/features/admin/dashboard_csv_export.dart';

/// Báo cáo CSV phải ra một TỆP THẬT, không chỉ là chuỗi trong clipboard. Test ghi vào thư mục
/// tạm của hệ điều hành rồi ĐỌC LẠI TỪ ĐĨA — chính là thứ người dùng sẽ mở bằng Excel.
void main() {
  group('dashboardCsvFileName', () {
    test('mang ky bao cao va moc thoi gian nen hai lan xuat khong trung ten', () {
      final first = dashboardCsvFileName(30, DateTime(2026, 10, 8, 9, 5, 3));
      final second = dashboardCsvFileName(30, DateTime(2026, 10, 8, 9, 5, 4));

      expect(first, 'bao-cao-dashboard-30-ngay-2026-10-08T09-05-03.csv');
      expect(first, isNot(second));
      expect(
        dashboardCsvFileName(7, DateTime(2026, 10, 8, 9, 5, 3)),
        startsWith('bao-cao-dashboard-7-ngay-'),
      );
    });
  });

  group('dashboardCsvBytes', () {
    test('giu nguyen van, khong tu chen them BOM', () {
      // Server đã chèn BOM vào chính chuỗi CSV. Thêm một BOM nữa ở đây sẽ làm Excel hiện
      // "﻿Mã đơn vị" ở cột đầu.
      expect(dashboardCsvBytes('a,b'), [0x61, 0x2C, 0x62]);

      final withBom = dashboardCsvBytes('\uFEFFMã');
      expect(withBom.sublist(0, 3), [0xEF, 0xBB, 0xBF]);
      expect(withBom.length, 6); // 3 byte BOM + "M"(1) + "ã"(2)
    });
  });

  group('saveDashboardCsv', () {
    late Directory tempDir;

    setUp(() async {
      tempDir = await Directory.systemTemp.createTemp('lmkit-csv-export-');
    });

    tearDown(() async {
      if (await tempDir.exists()) await tempDir.delete(recursive: true);
    });

    test('ghi mot tep that trong bo nho may va giu nguyen BOM cho Excel', () async {
      const csv = '\uFEFFMã đơn vị,Tên đơn vị\r\n11111111,Cục Trồng trọt\r\n';

      final file = await saveDashboardCsv(
        csv: csv,
        days: 30,
        now: DateTime(2026, 10, 8, 10, 30),
        resolveDirectory: () async => tempDir,
      );

      expect(await file.exists(), isTrue);
      expect(file.absolute.path, startsWith(tempDir.absolute.path));
      expect(file.path, endsWith('bao-cao-dashboard-30-ngay-2026-10-08T10-30-00.csv'));

      // Đọc lại TỪ ĐĨA: giá trị trả về của writeAsBytes không chứng minh được tệp có nội dung gì.
      final bytes = await File(file.path).readAsBytes();
      expect(bytes.sublist(0, 3), [0xEF, 0xBB, 0xBF]);

      // Bộ giải mã UTF-8 của Dart TIÊU THỤ ký tự BOM ở đầu chuỗi, nên đọc thành String sẽ thiếu
      // U+FEFF. Bytes ở trên mới là bằng chứng tệp giữ đúng BOM cho Excel — đây là lý do test
      // phải kiểm cả hai: bytes (BOM còn) và chuỗi (nội dung không bị đổi).
      expect(bytes, dashboardCsvBytes(csv));
      expect(await File(file.path).readAsString(), csv.substring(1));
    });

    test('tao thu muc neu chua co, va hai lan xuat khong ghi de nhau', () async {
      final nested = Directory('${tempDir.path}${Platform.pathSeparator}bao-cao');
      expect(await nested.exists(), isFalse);

      final first = await saveDashboardCsv(
        csv: 'lan-1',
        days: 7,
        now: DateTime(2026, 10, 8, 10, 0, 0),
        resolveDirectory: () async => nested,
      );
      final second = await saveDashboardCsv(
        csv: 'lan-2',
        days: 7,
        now: DateTime(2026, 10, 8, 10, 0, 1),
        resolveDirectory: () async => nested,
      );

      expect(first.path, isNot(second.path));
      expect(await first.readAsString(), 'lan-1');
      expect(await second.readAsString(), 'lan-2');
      expect((await nested.list().toList()).length, 2);
    });
  });
}
