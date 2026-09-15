import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/ui/app_controls.dart';
import 'admin_repository.dart';

/// Khung danh sách dùng chung cho mọi màn admin: tải dữ liệu, tìm kiếm phía
/// server, trạng thái lỗi/rỗng, kéo để làm mới và nút hành động.
class AdminListView<T> extends ConsumerStatefulWidget {
  const AdminListView({
    super.key,
    required this.title,
    required this.fetch,
    required this.itemBuilder,
    this.searchHint,
    this.emptyText = 'Chưa có dữ liệu.',
    this.emptyIcon = Icons.inbox_outlined,
    this.description,
    this.extraActions,
    this.fabBuilder,
  });

  final String title;
  final String? description;
  final Future<List<T>> Function(String? search) fetch;
  final Widget Function(
    BuildContext context,
    T item,
    Future<void> Function() reload,
  )
  itemBuilder;
  final String? searchHint;
  final String emptyText;

  /// Icon minh hoạ cho trạng thái rỗng — mỗi nhóm dữ liệu một hình cho dễ nhận ra.
  final IconData emptyIcon;
  final List<Widget> Function(
    BuildContext context,
    Future<void> Function() reload,
  )?
  extraActions;
  final Widget Function(BuildContext context, Future<void> Function() reload)?
  fabBuilder;

  @override
  ConsumerState<AdminListView<T>> createState() => _AdminListViewState<T>();
}

class _AdminListViewState<T> extends ConsumerState<AdminListView<T>> {
  final _searchController = TextEditingController();
  Timer? _debounce;
  List<T> _items = const [];
  bool _loading = true;
  String? _error;
  String? _search;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _debounce?.cancel();
    _searchController.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    if (!mounted) return;
    setState(() {
      _loading = _items.isEmpty;
      _error = null;
    });
    try {
      final items = await widget.fetch(_search);
      if (mounted) setState(() => _items = items);
    } catch (error) {
      if (mounted) setState(() => _error = adminErrorMessage(error));
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  void _onSearchChanged(String value) {
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 350), () {
      if (!mounted) return;
      _search = value.trim().isEmpty ? null : value.trim();
      _load();
    });
  }

  @override
  Widget build(BuildContext context) {
    final reload = _load;
    return Scaffold(
      appBar: AppBar(
        title: Text(widget.title),
        actions: [
          IconButton(
            tooltip: 'Làm mới',
            onPressed: reload,
            icon: const Icon(Icons.refresh),
          ),
          ...?widget.extraActions?.call(context, reload),
        ],
      ),
      floatingActionButton: widget.fabBuilder?.call(context, reload),
      body: RefreshIndicator(
        onRefresh: reload,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            if (widget.description != null)
              Padding(
                padding: const EdgeInsets.only(bottom: 12),
                child: Text(
                  widget.description!,
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ),
            if (widget.searchHint != null)
              Padding(
                padding: const EdgeInsets.only(bottom: 4),
                child: AppTextField(
                  controller: _searchController,
                  label: widget.searchHint!,
                  onChanged: _onSearchChanged,
                ),
              ),
            if (_error != null)
              AppAlert(message: _error!, isError: true, onRetry: reload),
            if (_loading)
              const Padding(
                padding: EdgeInsets.all(40),
                child: Center(child: CircularProgressIndicator()),
              )
            else if (_items.isEmpty)
              AppEmptyState(message: widget.emptyText, icon: widget.emptyIcon)
            else
              for (final item in _items)
                widget.itemBuilder(context, item, reload),
          ],
        ),
      ),
    );
  }
}

/// Thông báo lỗi/trạng thái kèm nút thử lại (Forui `FAlert`).
class AdminBanner extends StatelessWidget {
  const AdminBanner({
    super.key,
    required this.message,
    this.isError = false,
    this.onRetry,
  });

  final String message;
  final bool isError;
  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) =>
      AppAlert(message: message, isError: isError, onRetry: onRetry);
}

/// Ô nhập văn bản có nhãn, dùng trong các dialog form admin (Forui `FTextField`).
class AdminField extends StatelessWidget {
  const AdminField({
    super.key,
    required this.controller,
    required this.label,
    this.hint,
    this.obscure = false,
    this.maxLines = 1,
    this.keyboardType,
  });

  final TextEditingController controller;
  final String label;
  final String? hint;
  final bool obscure;
  final int maxLines;
  final TextInputType? keyboardType;

  @override
  Widget build(BuildContext context) => AppTextField(
    controller: controller,
    label: label,
    hint: hint,
    obscure: obscure,
    maxLines: maxLines,
    keyboardType: keyboardType,
  );
}

/// Hỏi xác nhận một hành động không hoàn tác được.
/// Xác nhận hành động không hoàn tác được trong màn quản trị.
///
/// Chỉ là bí danh của [confirmAppAction] để mã admin đọc rõ nghĩa; cả app chỉ có
/// MỘT hộp thoại xác nhận nên không thể lệch nhau về chữ hay thứ tự nút.
Future<bool> confirmAdminAction(
  BuildContext context, {
  required String title,
  required String message,
  String confirmLabel = 'Xoá',
}) => confirmAppAction(
  context,
  title: title,
  message: message,
  confirmLabel: confirmLabel,
);

/// Hiển thị kết quả tác vụ dưới dạng SnackBar.
void showAdminSnack(BuildContext context, String message) =>
    showAppSnack(context, message);
