# Kế hoạch: Giao diện dịch video — lmkit-agent

**Trạng thái:** Kế hoạch; chưa triển khai.  
**Phạm vi:** Vue web và Flutter mobile thuộc repository `lmkit-agent`. `OllamaAgent` có kế hoạch riêng tại `docs/plans/video-translation-ui.md` trong repository đó.

## Mục tiêu

Tạo cùng một trải nghiệm UI-first để nhập nguồn video, chọn ngôn ngữ đích, preview video có lớp phụ đề minh họa ở giữa cạnh dưới, và cho biết tính năng dịch AI/xuất video thật chưa được nối backend.

## Không nằm trong kế hoạch này

- Không hiện thực dịch AI/realtime, speech-to-text, lấy audio, hoặc tạo file video hoàn chỉnh.
- Không tải/proxy video Facebook/YouTube, vượt DRM/đăng nhập, hay giả định URL trang xem là URL media.
- Không thay đổi API .NET, cơ sở dữ liệu, upload, hay lưu dữ liệu phía server.
- Không mở rộng sang app mobile OllamaAgent; repository kia là Angular web.

## Thiết kế dùng chung

- Route web được bảo vệ bởi auth hiện có: `/tools/video-translation`.
- Mobile đã đăng nhập mở màn “Dịch video” từ nhóm “AI Studio” trong `FunctionMenuSheet`, không đổi màn mặc định Chat.
- Mỗi client có catalogue ngôn ngữ ISO 639-1 (184 mã), mã locale cùng tên bản địa và tên tiếng Anh; tìm kiếm theo mã hoặc hai tên. Dữ liệu được đóng gói, không gọi dịch vụ bên ngoài.
- Nguồn có thể là file local được người dùng chọn hoặc URL media công khai mà họ có quyền xem. URL chỉ được bind vào player sau khi bấm “Preview”; paste URL không phát request. Dùng player gốc, không dựng iframe HTML tùy ý. Link trang Facebook/YouTube có thể không phát được; giải thích rõ rằng UI chưa trích xuất âm thanh.
- Overlay hiển thị câu mẫu có nhãn “Phụ đề minh họa — AI chưa kết nối”; không gọi là bản dịch. Nút “Xuất video” bị disable và giải thích backend cần được tích hợp.
- Mọi URL/file và trạng thái preview chỉ giữ cục bộ. Không upload hoặc gọi API.

## Công việc 1 — Vue web

### File dự kiến

- Tạo `LmKitOmniClient/src/views/tools/videoLanguages.ts`: catalogue và hàm pure tìm kiếm/kiểm tra URL.
- Tạo `LmKitOmniClient/src/views/tools/videoLanguages.test.ts`: kiểm catalogue, uniqueness, tìm theo ISO code/tên Anh/tên bản địa, normalize dấu/hoa thường, URL HTTP(S) hợp lệ và scheme bị từ chối.
- Sửa `LmKitOmniClient/src/router/index.ts`: thêm route con authenticated `/tools/video-translation`, lazy-load, page title.
- Tạo `LmKitOmniClient/src/views/tools/VideoTranslationView.vue`: form URL, file picker native, chọn ngôn ngữ có tìm kiếm, nút Preview, player HTML5 local/media, overlay bottom-center, trạng thái lỗi/giới hạn social và export disabled.
- Tạo test component (Vitest): trạng thái trống, invalid URL, URL chỉ phát sau Preview, file replacement/clear, caption và export disabled; không gọi API.
- Cập nhật `LmKitOmniClient/src/layout/AppLayout.vue` (hoặc nguồn nav được `AppLayout` dùng): thêm link “Dịch video” trong nhóm công cụ, giữ theo conventions thực tế.

### Trình tự và kiểm tra

1. Đọc `LmKitOmniClient/AGENTS.md`/hướng dẫn lồng nếu có; kiểm tra auth/router/nav hiện hành và package scripts trước khi sửa.
2. Viết test helper trước, chạy `npm run test:unit -- src/views/tools/videoLanguages.test.ts` để xác nhận fail vì implementation chưa có.
3. Thêm helper/catalogue thuần và chạy lại test.
4. Viết test component cho URL/file/preview/caption/export; chạy fail-first.
5. Thêm route và UI Vue. Dùng Vue binding, không `innerHTML`; tạo `URL.createObjectURL(file)` khi cần preview và `URL.revokeObjectURL` khi thay file/unmount. Chỉ set `video.src` sau click Preview. Player error phải có hướng dẫn đổi nguồn/định dạng.
6. Chạy `npm run test:unit`, `npm run build`, `npm run lint` trong `LmKitOmniClient`; sửa lỗi thực tế, không tắt test/lint.
7. Chạy app bằng dev server hiện có hoặc khởi động đúng theo `package.json`; đăng nhập và mở route, thử file mẫu MP4/WebM, kiểm tra overlay và disabled state. Ghi rõ nếu auth hoặc codec chặn kiểm thử.

## Công việc 2 — Flutter mobile

### File dự kiến

- Tạo `LmKitOmniMobile/lib/features/video_translation/video_languages.dart` và `test/video_languages_test.dart`: cùng mã/ngôn ngữ và quy tắc tìm kiếm như Vue.
- Tạo `LmKitOmniMobile/lib/features/video_translation/video_translation_screen.dart`.
- Sửa `LmKitOmniMobile/lib/features/more/function_menu.dart`: thêm mục AI Studio và điều hướng tới screen.
- Sửa `LmKitOmniMobile/pubspec.yaml` và lockfile bằng Flutter package manager: thêm package `video_player` tương thích với Dart SDK hiện tại; `file_picker` đã có, không thêm picker khác.
- Tạo `LmKitOmniMobile/test/video_translation_screen_test.dart` cho trống/URL/locale/overlay/export/lỗi player/controller lifecycle.

### Trình tự và kiểm tra

1. Đọc hướng dẫn lồng tại `LmKitOmniMobile/AGENTS.md` nếu có, Flutter SDK constraint, lockfile, menu pattern và API package hiện hành.
2. Viết `video_languages_test.dart`, chạy test mục tiêu fail-first; thêm helper/catalogue, chạy lại.
3. Thêm tests cho picker/player bằng seam có thể giả lập để unit/widget tests không cần phát network; không giả lập kết quả sản xuất thành công.
4. Thêm `video_player` bằng `flutter pub add video_player:<phiên-bản-tương-thích>` sau khi xác nhận ràng buộc; xem diff để chắc không nâng cấp dependency unrelated.
5. Tạo screen responsive: `file_picker` chọn video, `VideoPlayerController.file` khởi tạo khi user chọn file, `AspectRatio`/`Stack` overlay subtitle dưới giữa, trạng thái initialize/decode error, ngôn ngữ searchable, URL preview chỉ khi nhấn rõ ràng, nút xuất luôn disabled. Dispose controller khi thay nguồn và khi rời màn.
6. Nối menu mobile; không đổi `HomeScreen`/Chat mặc định hoặc auth flow.
7. Chạy `flutter pub get --enforce-lockfile` nếu phù hợp tool version; `flutter analyze`, tests mục tiêu, rồi toàn bộ `flutter test` tại `LmKitOmniMobile`.
8. Nếu Android emulator/device có sẵn, build và chạy app; chọn một MP4 local, xác minh video thực phát, phụ đề đúng vị trí và back/dispose không báo lỗi. Không tuyên bố đã kiểm tra device nếu chưa chạy được.

## Acceptance criteria

- Vue route cần đăng nhập; Flutter screen chỉ reachable sau đăng nhập.
- Hai client có cùng danh mục ngôn ngữ (mã/tên bản địa/tên Anh), tìm kiếm được.
- Tệp video local được preview bởi player thật trên mỗi nền tảng; URL media chỉ thử phát sau click Preview.
- URL sai, URL mạng xã hội không nhúng được, hoặc codec không hỗ trợ đều có thông báo phân biệt/hướng dẫn.
- Caption ở giữa đáy player, có nhãn demo rõ ràng; không có lời dịch AI giả.
- Export tồn tại nhưng disable; không sinh file giả hoặc gọi backend.
- Đổi/xóa nguồn và rời màn giải phóng object URL/controller.
- Helper/UI tests, Vue build/lint/test và Flutter analyze/test đạt; nếu tool/device chưa có phải báo limitation chính xác.

## Tương quan với OllamaAgent

Đây chỉ là kế hoạch `lmkit-agent`. Kế hoạch Angular tương ứng nằm trong repository `OllamaAgent`, `docs/plans/video-translation-ui.md`. Hai implementation độc lập nhưng phải giữ route, locale catalogue, demo status và trạng thái xuất tương đương; không chia sẻ backend trong milestone UI-only.
