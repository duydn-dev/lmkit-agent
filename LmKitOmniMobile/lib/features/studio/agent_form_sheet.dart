import 'package:flutter/material.dart';

import '../../app/ui/app_controls.dart';
import '../admin/admin_models.dart';
import 'studio_models.dart';

/// Dữ liệu form tạo/sửa custom agent.
class AgentFormResult {
  const AgentFormResult({
    required this.name,
    required this.personaPrompt,
    this.description,
    this.icon,
    this.shared = false,
    this.allowedTools,
    this.knowledgeDocumentIds,
    this.loraAdapterId,
  });

  final String name;
  final String personaPrompt;
  final String? description;
  final String? icon;
  final bool shared;

  /// `null` = dùng bộ công cụ mặc định theo vai trò.
  final List<String>? allowedTools;
  final List<String>? knowledgeDocumentIds;

  /// LoRA adapter chọn trong form; `null` = không dùng adapter.
  ///
  /// Binding này **không** nằm trong payload agent — màn gọi phải tự gọi
  /// `assign`/`unassign` sau khi lưu, giống bản desktop.
  final String? loraAdapterId;
}

/// Mở form tạo/sửa custom agent dưới dạng bottom sheet.
///
/// [tools], [documents] và [loraAdapters] được nạp trước khi mở để form không
/// phải tự gọi API.
Future<AgentFormResult?> showAgentForm(
  BuildContext context, {
  required List<AgentToolModel> tools,
  required List<KnowledgeDocModel> documents,
  List<LoraAdapterModel> loraAdapters = const [],
  CustomAgentModel? existing,
}) => showAppSheet<AgentFormResult>(
  context,
  isScrollControlled: true,
  // Không tự cộng `viewInsets` ở đây: `showFSheet` đã tự chừa chỗ cho bàn phím,
  // cộng thêm lần nữa thì phần đầu form (tiêu đề) bị đẩy ra ngoài khung sheet —
  // đúng hiện tượng tiêu đề "Tạo Custom Agent" bị cắt cụt.
  builder: (sheetContext) => _AgentFormSheet(
    tools: tools,
    documents: documents,
    loraAdapters: loraAdapters,
    existing: existing,
  ),
);

class _AgentFormSheet extends StatefulWidget {
  const _AgentFormSheet({
    required this.tools,
    required this.documents,
    this.loraAdapters = const [],
    this.existing,
  });

  final List<AgentToolModel> tools;
  final List<KnowledgeDocModel> documents;

  /// Adapter khả dụng; rỗng khi tính năng LoRA bị tắt hoặc người dùng không có quyền.
  final List<LoraAdapterModel> loraAdapters;
  final CustomAgentModel? existing;

  @override
  State<_AgentFormSheet> createState() => _AgentFormSheetState();
}

class _AgentFormSheetState extends State<_AgentFormSheet> {
  late final TextEditingController _name;
  late final TextEditingController _description;
  late final TextEditingController _persona;
  late final TextEditingController _icon;

  /// `true` khi agent dùng bộ công cụ mặc định theo vai trò.
  late bool _defaultTools;
  late bool _shared;
  late final Set<String> _tools;
  late final Set<String> _documents;
  late String? _loraAdapterId;

  @override
  void initState() {
    super.initState();
    final existing = widget.existing;
    _name = TextEditingController(text: existing?.name ?? '');
    _description = TextEditingController(text: existing?.description ?? '');
    _persona = TextEditingController(text: existing?.personaPrompt ?? '');
    _icon = TextEditingController(text: existing?.icon ?? '');
    _defaultTools = existing?.allowedTools == null;
    _shared = existing?.isSharedWithTenant ?? false;
    _tools = {...?existing?.allowedTools};
    _documents = {...?existing?.knowledgeDocumentIds};
    _loraAdapterId = existing?.loraAdapterId;
  }

  @override
  void dispose() {
    _name.dispose();
    _description.dispose();
    _persona.dispose();
    _icon.dispose();
    super.dispose();
  }

  /// Nhãn của adapter đang chọn; `''` (và id không còn tồn tại) là "không dùng".
  String _adapterLabel(String id) {
    if (id.isEmpty) return 'Không dùng adapter';
    final adapter = widget.loraAdapters.firstWhere(
      (adapter) => adapter.id == id,
      orElse: () => widget.loraAdapters.first,
    );
    return adapter.isActive ? adapter.name : '${adapter.name} (tạm tắt)';
  }

  void _submit() {
    final name = _name.text.trim();
    if (name.isEmpty) {
      showAppSnack(context, 'Tên agent không được trống.');
      return;
    }
    if (_persona.text.trim().isEmpty) {
      showAppSnack(context, 'Persona prompt không được trống.');
      return;
    }
    Navigator.pop(
      context,
      AgentFormResult(
        name: name,
        personaPrompt: _persona.text.trim(),
        description: _description.text.trim(),
        icon: _icon.text.trim(),
        shared: _shared,
        allowedTools: _defaultTools ? null : _tools.toList(),
        knowledgeDocumentIds: _documents.toList(),
        loraAdapterId: _loraAdapterId,
      ),
    );
  }

  @override
  Widget build(BuildContext context) => ConstrainedBox(
    // Cao tối đa 85% màn (không phải cố định 85%): form ngắn vừa khít nội dung,
    // form dài — như khi bật chọn công cụ thủ công — mới chạm trần và cuộn.
    constraints: BoxConstraints(
      maxHeight: MediaQuery.of(context).size.height * 0.85,
    ),
    child: Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // Tiêu đề có khoảng thở phía trên: để sát mép sheet thì chữ bị cắt cụt
        // trông như màn lỗi.
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 20, 16, 12),
          child: Text(
            widget.existing == null ? 'Tạo Custom Agent' : 'Sửa Custom Agent',
            style: Theme.of(context).textTheme.titleLarge,
          ),
        ),
        Flexible(
          child: ListView(
            // Không cộng `viewInsets` cho đáy danh sách: `showFSheet` đã tự co
            // sheet khi bàn phím mở (`resizeToAvoidBottomInset`), cộng thêm lần
            // nữa thì phần đầu form bị đẩy ra ngoài khung — tiêu đề bị cắt cụt.
            padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
            children: [
                AppTextField(controller: _name, label: 'Tên'),
                AppTextField(controller: _description, label: 'Mô tả'),
                AppTextField(
                  controller: _persona,
                  label: 'Persona prompt',
                  hint: 'Mô tả vai trò, giọng điệu và giới hạn của agent',
                  maxLines: 5,
                ),
                AppTextField(
                  controller: _icon,
                  label: 'Icon',
                  hint: 'Một emoji, ví dụ 🤖',
                ),
                AppSwitchTile(
                  label: 'Chia sẻ cho cả tenant',
                  subtitle:
                      'Người dùng khác trong tenant có thể dùng agent này.',
                  value: _shared,
                  onChanged: (value) => setState(() => _shared = value),
                ),
                const Divider(),
                AppSwitchTile(
                  label: 'Dùng bộ công cụ mặc định',
                  subtitle:
                      'Tắt để tự chọn đúng những công cụ agent được phép gọi.',
                  value: _defaultTools,
                  onChanged: (value) => setState(() => _defaultTools = value),
                ),
                if (!_defaultTools) ...[
                  const SizedBox(height: 8),
                  if (widget.tools.isEmpty)
                    const Text('Server không trả về danh sách công cụ.')
                  else
                    for (final tool in widget.tools)
                      AppCheckTile(
                        label: tool.label,
                        subtitle: tool.description,
                        value: _tools.contains(tool.name),
                        onChanged: (value) => setState(() {
                          if (value) {
                            _tools.add(tool.name);
                          } else {
                            _tools.remove(tool.name);
                          }
                        }),
                      ),
                ],
                const Divider(),
                const AppSectionTitle(
                  title: 'Tài liệu ghim',
                  subtitle:
                      'Chỉ tài liệu do bạn tải lên mới ghim được vào agent.',
                ),
                if (widget.documents.isEmpty)
                  const Text('Bạn chưa có tài liệu nào trong kho.')
                else
                  for (final document in widget.documents)
                    AppCheckTile(
                      label: document.fileName,
                      subtitle: document.isVectorized
                          ? 'Đã vector hóa'
                          : 'Đang xử lý',
                      value: _documents.contains(document.id),
                      onChanged: (value) => setState(() {
                        if (value) {
                          _documents.add(document.id);
                        } else {
                          _documents.remove(document.id);
                        }
                      }),
                    ),
                const Divider(),
                const AppSectionTitle(
                  title: 'LoRA adapter',
                  subtitle:
                      'Adapter tinh chỉnh áp lên model chat riêng cho agent này.',
                ),
                if (widget.loraAdapters.isEmpty)
                  const Text(
                    'Không có adapter khả dụng (tính năng có thể đang tắt).',
                  )
                else
                  // Ô chọn của hệ thiết kế: sheet Forui không tra được tổ tiên
                  // `Material` nên `DropdownButtonFormField` ở đây sẽ làm cả
                  // sheet trắng xoá — xem `AppSelectTile`.
                  //
                  // `''` là "không dùng adapter" chứ không phải `null`: giá trị
                  // `null` từ sheet chọn trùng với "người dùng bấm ra ngoài", nên
                  // không phân biệt được hai trường hợp.
                  AppSelectTile<String>(
                    label: 'Adapter',
                    icon: Icons.tune,
                    value: _loraAdapterId ?? '',
                    items: [
                      '',
                      for (final adapter in widget.loraAdapters) adapter.id,
                    ],
                    labelOf: _adapterLabel,
                    helper: 'Để trống nếu không dùng adapter riêng.',
                    onChanged: (value) => setState(
                      () => _loraAdapterId = value.isEmpty ? null : value,
                    ),
                  ),
                const SizedBox(height: 8),
              ],
            ),
          ),
        // Đường kẻ tách vùng cuộn khỏi nút lưu: danh sách bị cắt ở mép cuộn là
        // chuyện bình thường, nhưng phải đọc ra "còn cuộn được" chứ không phải
        // "form bị cắt cụt".
        const Divider(height: 1),
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 12, 16, 16),
          child: AppPrimaryButton(
            label: widget.existing == null ? 'Tạo agent' : 'Lưu thay đổi',
            onPressed: _submit,
          ),
        ),
      ],
    ),
  );
}
