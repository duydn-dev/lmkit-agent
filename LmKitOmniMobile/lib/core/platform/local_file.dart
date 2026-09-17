/// Thao tác tệp cục bộ, có bản riêng cho nền tảng không có `dart:io` (web).
library;

///
/// `dart:io` không tồn tại trên web, nên chỉ cần **một** import `dart:io` ở bất
/// kỳ file nào là cả bản build web không biên dịch được — kể cả khi chỗ dùng
/// được bọc trong `if (kIsWeb)`. Tách ra sau một export có điều kiện thì phần
/// còn lại của app dùng được một API duy nhất cho mọi nền tảng.
export 'local_file_web.dart' if (dart.library.io) 'local_file_io.dart';
