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
}) => showModalBottomSheet<AgentFormResult>(
  context: context,
  isScrollControlled: true,
  showDragHandle: true,
  builder: (context) => _AgentFormSheet(
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
  Widget build(BuildContext context) => Padding(
    padding: EdgeInsets.only(
      left: 16,
      right: 16,
      bottom: MediaQuery.of(context).viewInsets.bottom + 16,
    ),
    child: SizedBox(
      height: MediaQuery.of(context).size.height * 0.85,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            widget.existing == null ? 'Tạo Custom Agent' : 'Sửa Custom Agent',
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 12),
          Expanded(
            child: ListView(
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
                SwitchListTile(
                  contentPadding: EdgeInsets.zero,
                  value: _shared,
                  onChanged: (value) => setState(() => _shared = value),
                  title: const Text('Chia sẻ cho cả tenant'),
                  subtitle: const Text(
                    'Người dùng khác trong tenant có thể dùng agent này.',
                  ),
                ),
                const Divider(),
                SwitchListTile(
                  contentPadding: EdgeInsets.zero,
                  value: _defaultTools,
                  onChanged: (value) => setState(() => _defaultTools = value),
                  title: const Text('Dùng bộ công cụ mặc định'),
                  subtitle: const Text(
                    'Tắt để tự chọn đúng những công cụ agent được phép gọi.',
                  ),
                ),
                if (!_defaultTools) ...[
                  const SizedBox(height: 8),
                  if (widget.tools.isEmpty)
                    const Text('Server không trả về danh sách công cụ.')
                  else
                    for (final tool in widget.tools)
                      CheckboxListTile(
                        contentPadding: EdgeInsets.zero,
                        value: _tools.contains(tool.name),
                        onChanged: (value) => setState(() {
                          if (value == true) {
                            _tools.add(tool.name);
                          } else {
                            _tools.remove(tool.name);
                          }
                        }),
                        title: Text(tool.label),
                        subtitle: Text(tool.description),
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
                    CheckboxListTile(
                      contentPadding: EdgeInsets.zero,
                      value: _documents.contains(document.id),
                      onChanged: (value) => setState(() {
                        if (value == true) {
                          _documents.add(document.id);
                        } else {
                          _documents.remove(document.id);
                        }
                      }),
                      title: Text(document.fileName),
                      subtitle: Text(
                        document.isVectorized ? 'Đã vector hóa' : 'Đang xử lý',
                      ),
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
                  DropdownButtonFormField<String?>(
                    initialValue: _loraAdapterId,
                    isExpanded: true,
                    decoration: const InputDecoration(
                      labelText: 'Adapter',
                      helperText: 'Để trống nếu không dùng adapter riêng.',
                    ),
                    items: [
                      const DropdownMenuItem<String?>(
                        value: null,
                        child: Text('Không dùng adapter'),
                      ),
                      for (final adapter in widget.loraAdapters)
                        DropdownMenuItem<String?>(
                          value: adapter.id,
                          child: Text(
                            adapter.isActive
                                ? adapter.name
                                : '${adapter.name} (tạm tắt)',
                          ),
                        ),
                    ],
                    onChanged: (value) =>
                        setState(() => _loraAdapterId = value),
                  ),
                const SizedBox(height: 8),
              ],
            ),
          ),
          const SizedBox(height: 12),
          AppPrimaryButton(
            label: widget.existing == null ? 'Tạo agent' : 'Lưu thay đổi',
            onPressed: _submit,
          ),
        ],
      ),
    ),
  );
}
