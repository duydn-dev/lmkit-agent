import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

// Xuất báo cáo CSV của dashboard RA MỘT TỆP trong bộ nhớ máy.
//
// Trước đây báo cáo chỉ đi qua clipboard: đủ để dán vào Excel một lần, nhưng không phải một
// tệp — không mở lại được, không gửi được, và mất ngay khi có thứ khác ghi đè clipboard.
//
// Phần quyết định tên tệp và nội dung tệp được tách khỏi `path_provider` để test được bằng
// thư mục tạm THẬT (`Directory.systemTemp`) mà không cần kênh platform.

/// Tên tệp: mang kỳ báo cáo và mốc thời gian, nên xuất hai lần trong cùng một ngày không ghi
/// đè lên nhau và người dùng vẫn nhìn ra tệp nào là bản mới.
String dashboardCsvFileName(int days, DateTime now) {
  final stamp = now.toIso8601String().substring(0, 19).replaceAll(':', '-');
  return 'bao-cao-dashboard-$days-ngay-$stamp.csv';
}

/// Nội dung tệp: UTF-8, giữ NGUYÊN VĂN chuỗi server trả về — chuỗi đó đã bắt đầu bằng BOM
/// (U+FEFF) để Excel nhận đúng tiếng Việt, nên encode lại từ Dart vẫn ra đúng BOM đó.
List<int> dashboardCsvBytes(String csv) => utf8.encode(csv);

/// Ghi báo cáo thành tệp trong thư mục do [resolveDirectory] trả về, và trả về tệp đã ghi để
/// màn hình hiển thị đường dẫn cho người dùng.
Future<File> saveDashboardCsv({
  required String csv,
  required int days,
  required DateTime now,
  required Future<Directory> Function() resolveDirectory,
}) async {
  final directory = await resolveDirectory();
  if (!await directory.exists()) {
    await directory.create(recursive: true);
  }

  final file = File(
    '${directory.path}${Platform.pathSeparator}${dashboardCsvFileName(days, now)}',
  );
  return file.writeAsBytes(dashboardCsvBytes(csv), flush: true);
}

/// Thư mục để ghi báo cáo TRÊN THIẾT BỊ THẬT.
///
/// Ưu tiên thư mục Downloads chung (desktop/iOS trả về được) để người dùng thấy tệp ngay bằng
/// trình quản lý tệp. Android không có thư mục đó cho ứng dụng nên rơi về thư mục tài liệu của
/// ứng dụng — vẫn là tệp thật, sao chép hoặc gửi đi được.
Future<Directory> resolveDashboardExportDirectory() async {
  try {
    final downloads = await getDownloadsDirectory();
    if (downloads != null) return downloads;
  } catch (_) {
    // Nuốt lỗi ở đây là CHỦ ĐÍCH: đây chỉ là bước CHỌN thư mục, và luôn còn phương án hai chắc
    // chắn hoạt động. Ném ra ở đây sẽ làm cả lần xuất thất bại dù tệp vẫn ghi được.
  }

  return getApplicationDocumentsDirectory();
}
