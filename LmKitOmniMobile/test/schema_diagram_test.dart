import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:lmkit_omni_mobile/app/theme.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_models.dart';
import 'package:lmkit_omni_mobile/core/auth/auth_provider.dart';
import 'package:lmkit_omni_mobile/core/config/app_config.dart';
import 'package:lmkit_omni_mobile/core/config/app_config_provider.dart';
import 'package:lmkit_omni_mobile/core/mock/mock_fixtures.dart';
import 'package:lmkit_omni_mobile/features/admin/admin_models.dart';
import 'package:lmkit_omni_mobile/features/admin/database_diagram.dart';
import 'package:lmkit_omni_mobile/app/ui/app_controls.dart';
import 'package:lmkit_omni_mobile/features/admin/database_diagram_screen.dart';

/// Sơ đồ schema: model hoá dữ liệu `/schema`, bố cục ER, ghi chú và màn hình.
///
/// Bố cục là hàm thuần nên kiểm được bằng số học (thẻ không chồng nhau, điểm neo
/// nằm đúng mép thẻ, khung bao đủ mọi thẻ) — đọc mã nguồn không thấy được những
/// lỗi kiểu "đường quan hệ nối vào giữa khoảng không".
/// Cạnh phải nối hai mép **đối diện nhau**: thẻ nào bên trái thì dùng mép phải
/// của nó (và ngược lại), nếu không đường nối sẽ chạy xuyên qua thân thẻ.
void expectFacingAnchors(
  DiagramEdgeLayout edge,
  DiagramCardLayout from,
  DiagramCardLayout to,
) {
  if (edge.vertical) {
    expect(edge.from.dx, from.rect.center.dx);
    expect(edge.to.dx, to.rect.center.dx);
    expect(edge.from.dx, edge.to.dx);
    return;
  }
  final fromOnRight = from.rect.center.dx > to.rect.center.dx;
  expect(edge.from.dx, fromOnRight ? from.rect.left : from.rect.right);
  expect(edge.to.dx, fromOnRight ? to.rect.right : to.rect.left);
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  // Dio đọc token từ secure storage trước mỗi request; thiếu mock này thì request
  // treo ở interceptor và màn hình mãi ở trạng thái "đang tải".
  setUp(() => FlutterSecureStorage.setMockInitialValues({}));

  SchemaTableModel table({
    required String name,
    String? qualified,
    List<SchemaColumnModel> columns = const [],
    List<SchemaForeignKeyModel> foreignKeys = const [],
  }) => SchemaTableModel(
    name: name,
    qualifiedName: qualified ?? name,
    columns: columns,
    foreignKeys: foreignKeys,
  );

  SchemaColumnModel column(
    String name, {
    String type = 'integer',
    bool pk = false,
    bool fk = false,
    bool nullable = true,
  }) => SchemaColumnModel(
    name: name,
    dataType: type,
    isNullable: nullable,
    isPrimaryKey: pk,
    isForeignKey: fk,
  );

  DatabaseSchemaModel schema({
    List<SchemaTableModel> tables = const [],
    List<SchemaRelationModel> relations = const [],
    String provider = 'Postgres',
    bool isIndexed = true,
    bool truncated = false,
    int tableCount = 0,
    int totalTableCount = 0,
  }) => DatabaseSchemaModel(
    connectionId: 'c1',
    name: 'kho-bao-cao',
    provider: provider,
    isIndexed: isIndexed,
    indexStatus: isIndexed ? 'Completed' : 'Pending',
    tables: tables,
    relations: relations,
    truncated: truncated,
    tableCount: tableCount,
    totalTableCount: totalTableCount,
  );

  group('model', () {
    test('đọc đúng JSON của API', () {
      final parsed = DatabaseSchemaModel.fromJson(
        MockFixtures.databaseSchema(),
      );

      expect(parsed.name, 'CSDL nghiệp vụ quan trắc');
      expect(parsed.provider, 'Postgres');
      expect(parsed.tableCount, 3);
      expect(parsed.tables, hasLength(3));

      final measurements = parsed.tables[1];
      expect(measurements.qualifiedName, 'public.measurements');
      expect(measurements.columns[1].name, 'station_id');
      expect(measurements.columns[1].isForeignKey, isTrue);
      expect(measurements.columns[1].keyLabel, 'FK');
      expect(measurements.columns[0].keyLabel, 'PK');
      expect(measurements.foreignKeys.single.isResolved, isTrue);
    });

    test('thiếu field thì rơi về giá trị an toàn, không ném lỗi', () {
      final parsed = DatabaseSchemaModel.fromJson(const {});

      expect(parsed.tables, isEmpty);
      expect(parsed.relations, isEmpty);
      expect(parsed.truncated, isFalse);
      expect(parsed.isIndexed, isFalse);
    });

    test('tách cạnh vẽ được khỏi cạnh trỏ ra ngoài sơ đồ', () {
      final parsed = DatabaseSchemaModel.fromJson(
        MockFixtures.databaseSchema(),
      );

      // `public.parameters` không có trong sơ đồ → cạnh đó KHÔNG được vẽ nhưng
      // phải xuất hiện trong danh sách nhắc nhở.
      expect(parsed.drawableRelations, hasLength(1));
      expect(parsed.drawableRelations.single.fromTable, 'public.measurements');
      expect(parsed.externalReferences, hasLength(1));
      expect(parsed.externalReferences.single.toTable, 'public.parameters');
    });

    test('gom khoá ngoại không tách được', () {
      final parsed = schema(
        tables: [
          table(
            name: 'orders',
            foreignKeys: const [
              SchemaForeignKeyModel(
                column: 'a,b',
                raw: 'a,b → t.x,y',
                isResolved: false,
              ),
              SchemaForeignKeyModel(
                column: 'user_id',
                referencedTable: 'users',
                referencedColumn: 'id',
                raw: 'user_id → users.id',
                isResolved: true,
              ),
            ],
          ),
        ],
      );

      expect(parsed.unresolvedForeignKeys, hasLength(1));
      expect(parsed.unresolvedForeignKeys.single.raw, 'a,b → t.x,y');
    });
  });

  group('layout', () {
    test('chọn số cột theo số bảng', () {
      expect(SchemaDiagramLayout.columnsFor(0), 1);
      expect(SchemaDiagramLayout.columnsFor(1), 1);
      expect(SchemaDiagramLayout.columnsFor(2), 2);
      expect(SchemaDiagramLayout.columnsFor(4), 2);
      expect(SchemaDiagramLayout.columnsFor(5), 3);
      expect(SchemaDiagramLayout.columnsFor(40), 3);
    });

    test('thẻ không chồng nhau và khung bao đủ mọi thẻ', () {
      final layout = SchemaDiagramLayout.compute(
        DatabaseSchemaModel.fromJson(MockFixtures.databaseSchema()),
      );

      expect(layout.cards, hasLength(3));
      for (var i = 0; i < layout.cards.length; i++) {
        for (var j = i + 1; j < layout.cards.length; j++) {
          expect(
            layout.cards[i].rect.overlaps(layout.cards[j].rect),
            isFalse,
            reason: 'thẻ ${layout.cards[i].table.name} chồng lên thẻ khác',
          );
        }
        expect(
          layout.size.width,
          greaterThanOrEqualTo(layout.cards[i].rect.right),
        );
        expect(
          layout.size.height,
          greaterThanOrEqualTo(layout.cards[i].rect.bottom),
        );
      }
    });

    test('chiều cao thẻ theo số cột của bảng', () {
      final small = table(name: 'a', columns: [column('id', pk: true)]);
      final big = table(
        name: 'b',
        columns: [column('id'), column('x'), column('y')],
      );

      expect(
        SchemaDiagramLayout.cardHeight(big) -
            SchemaDiagramLayout.cardHeight(small),
        2 * SchemaDiagramLayout.baseRowHeight,
      );
    });

    test('tăng cỡ chữ thì thẻ cao lên theo', () {
      final tall = table(name: 'a', columns: [column('id'), column('x')]);

      expect(
        SchemaDiagramLayout.cardHeight(tall, textScale: 1.5),
        greaterThan(SchemaDiagramLayout.cardHeight(tall)),
      );
    });

    test('điểm neo nằm đúng mép thẻ và đúng dòng của cột', () {
      final layout = SchemaDiagramLayout.compute(
        DatabaseSchemaModel.fromJson(MockFixtures.databaseSchema()),
      );
      final card = layout.cards.firstWhere(
        (card) => card.table.name == 'measurements',
      );

      final anchor = card.rightAnchor('station_id');
      expect(anchor.dx, card.rect.right);
      // `station_id` là cột thứ 2 → tâm dòng thứ 2 trong thẻ.
      expect(
        anchor.dy,
        closeTo(
          card.rect.top + layout.headerHeight + layout.rowHeight * 1.5,
          0.001,
        ),
      );

      // Cột không tồn tại thì neo vào giữa thẻ, không ném lỗi.
      expect(card.leftAnchor('khong-co').dy, card.rect.center.dy);
    });

    test('cạnh vẽ từ bảng con sang bảng cha, bỏ cạnh thiếu đầu', () {
      final layout = SchemaDiagramLayout.compute(
        DatabaseSchemaModel.fromJson(MockFixtures.databaseSchema()),
      );

      final edge = layout.edges.single;
      expect(edge.relation.fromTable, 'public.measurements');
      expect(edge.relation.toTable, 'public.stations');
      expect(edge.vertical, isFalse);

      // Hai thẻ khác cột → neo vào **hai mép đối diện nhau** (bên nào ở trái thì
      // lấy mép phải của nó), nên đường nối không vòng qua thân thẻ.
      final from = layout.cards.firstWhere(
        (card) => card.table.qualifiedName == 'public.measurements',
      );
      final to = layout.cards.firstWhere(
        (card) => card.table.qualifiedName == 'public.stations',
      );
      expectFacingAnchors(edge, from, to);
    });

    test('hai bảng cùng một cột thì nối trên-dưới', () {
      // 3 bảng → 2 cột, nên bảng thứ 3 rơi lại cột của bảng thứ nhất: cạnh giữa
      // chúng phải đi dọc, không phải vẽ vòng ngang qua thẻ ở giữa.
      final layout = SchemaDiagramLayout.compute(
        schema(
          tables: [
            table(name: 'a', columns: [column('id', pk: true)]),
            table(name: 'b', columns: [column('id', pk: true)]),
            table(name: 'c', columns: [column('a_id', fk: true)]),
          ],
          relations: const [
            SchemaRelationModel(
              fromTable: 'c',
              fromColumn: 'a_id',
              toTable: 'a',
              toColumn: 'id',
              targetIncluded: true,
            ),
          ],
        ),
      );

      final edge = layout.edges.single;
      expect(edge.vertical, isTrue);
      // Cùng cột → cùng trục x; bảng 'c' nằm dưới nên đi từ mép trên của nó
      // lên mép dưới của 'a'.
      expect(edge.from.dx, edge.to.dx);
      expect(edge.from.dy, greaterThan(edge.to.dy));
      expect(edge.controlPoints.first.dy, lessThan(edge.from.dy));
    });

    test('hai bảng khác cột thì nối ngang', () {
      final layout = SchemaDiagramLayout.compute(
        schema(
          tables: [
            table(name: 'a', columns: [column('id', pk: true)]),
            table(name: 'b', columns: [column('a_id', fk: true)]),
          ],
          relations: const [
            SchemaRelationModel(
              fromTable: 'b',
              fromColumn: 'a_id',
              toTable: 'a',
              toColumn: 'id',
              targetIncluded: true,
            ),
          ],
        ),
      );

      final edge = layout.edges.single;
      expect(edge.vertical, isFalse);
      expectFacingAnchors(
        edge,
        layout.cards.firstWhere((card) => card.table.name == 'b'),
        layout.cards.firstWhere((card) => card.table.name == 'a'),
      );
    });

    test('sơ đồ rỗng thì không có thẻ và kích thước bằng 0', () {
      final layout = SchemaDiagramLayout.compute(schema());

      expect(layout.cards, isEmpty);
      expect(layout.edges, isEmpty);
      expect(layout.size, Size.zero);
    });
  });

  test('điểm neo của cạnh nằm trên hai mép đối diện nhau', () {
    // Khẳng định dùng chung cho cả cạnh ngang lẫn cạnh dọc.
    final layout = SchemaDiagramLayout.compute(
      schema(
        tables: [
          table(name: 'left', columns: [column('id', pk: true)]),
          table(name: 'right', columns: [column('left_id', fk: true)]),
        ],
        relations: const [
          SchemaRelationModel(
            fromTable: 'right',
            fromColumn: 'left_id',
            toTable: 'left',
            toColumn: 'id',
            targetIncluded: true,
          ),
        ],
      ),
    );

    expectFacingAnchors(
      layout.edges.single,
      layout.cards.firstWhere((card) => card.table.name == 'right'),
      layout.cards.firstWhere((card) => card.table.name == 'left'),
    );
  });

  group('ghi chú', () {
    test('rỗng khi không có gì phải cảnh báo', () {
      expect(schemaDiagramNotes(schema(tables: [table(name: 'a')])), isEmpty);
    });

    test('nói rõ phần bị cắt kèm cả hai con số', () {
      final notes = schemaDiagramNotes(
        schema(
          tables: [table(name: 'a')],
          truncated: true,
          tableCount: 40,
          totalTableCount: 137,
        ),
      );

      expect(notes.single, contains('40/137'));
    });

    test('giải thích cạnh bị bỏ, khoá không tách được và nhánh MongoDB', () {
      final notes = schemaDiagramNotes(
        schema(
          provider: 'Mongo',
          isIndexed: false,
          tables: [
            table(
              name: 'events',
              foreignKeys: const [SchemaForeignKeyModel(raw: 'a,b → t.x,y')],
            ),
          ],
          relations: const [
            SchemaRelationModel(
              fromTable: 'events',
              fromColumn: 'a',
              toTable: 'ngoai',
              targetIncluded: false,
            ),
          ],
        ),
      );

      expect(notes, hasLength(4));
      expect(notes.join(' '), contains('không được vẽ'));
      expect(notes.join(' '), contains('không tách được'));
      expect(notes.join(' '), contains('MongoDB'));
      expect(notes.join(' '), contains('chưa đánh chỉ mục'));
    });

    test('CSDL không có bảng nào thì nói thẳng', () {
      expect(schemaDiagramNotes(schema()).single, contains('không có bảng'));
    });
  });

  testWidgets('màn sơ đồ vẽ được bảng, quan hệ và ghi chú (dữ liệu mẫu)', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(390, 844);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          bootstrapAppConfigProvider.overrideWithValue(_mockConfig),
          authControllerProvider.overrideWith(() => _FakeAuth()),
        ],
        child: MaterialApp(
          theme: AppTheme.material(AppTheme.forui()),
          builder: (context, inner) =>
              FTheme(data: AppTheme.forui(), child: inner ?? const SizedBox()),
          home: const DatabaseDiagramScreen(
            connectionId: 'db-demo-0001',
            connectionName: 'CSDL nghiệp vụ quan trắc',
          ),
        ),
      ),
    );

    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 120));
    }

    expect(tester.takeException(), isNull);
    expect(find.text('CSDL nghiệp vụ quan trắc'), findsOneWidget);
    // Chip là `Text.rich` (icon + chữ trên cùng dòng chữ) nên phải tìm trong
    // rich text, không phải `Text.data`.
    expect(find.textContaining('3 bảng', findRichText: true), findsOneWidget);
    expect(
      find.textContaining('2 quan hệ khoá ngoại', findRichText: true),
      findsOneWidget,
    );
    // Thẻ bảng thật (chọn/đọc được), không phải ảnh.
    expect(find.text('public.measurements'), findsOneWidget);
    expect(find.text('public.stations'), findsOneWidget);
    // Cột khoá ngoại có nhãn FK, khoá chính có nhãn PK.
    expect(find.text('FK'), findsNWidgets(2));
    expect(find.text('public.thresholds'), findsOneWidget);
    // Cạnh trỏ ra ngoài sơ đồ phải được nhắc tới.
    expect(
      find.textContaining('trỏ tới bảng không nằm trong sơ đồ'),
      findsOneWidget,
    );
    expect(find.textContaining('Trỏ tới bảng ngoài sơ đồ'), findsOneWidget);
  });

  testWidgets('màn sơ đồ hiện lỗi kèm nút thử lại khi không gọi được API', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(390, 844);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    // Không bật dữ liệu mẫu và trỏ vào cổng không có gì → nhánh lỗi.
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          bootstrapAppConfigProvider.overrideWithValue(
            _mockConfig.copyWith(useMockData: false),
          ),
          authControllerProvider.overrideWith(() => _FakeAuth()),
        ],
        child: MaterialApp(
          theme: AppTheme.material(AppTheme.forui()),
          builder: (context, inner) =>
              FTheme(data: AppTheme.forui(), child: inner ?? const SizedBox()),
          home: const DatabaseDiagramScreen(connectionId: 'db-demo-0001'),
        ),
      ),
    );

    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 120));
    }

    expect(tester.takeException(), isNull);
    expect(find.byType(AppAlert), findsOneWidget);
    expect(find.byIcon(Icons.refresh), findsWidgets);
    // Tiêu đề rơi về mặc định khi không có tên kết nối.
    expect(find.text('Sơ đồ CSDL'), findsOneWidget);
  });
}

const _mockConfig = AppConfig(
  apiBaseUrl: 'http://localhost:5032',
  webBaseUrl: 'http://localhost:5173',
  flavor: 'test-mock',
  connectTimeout: Duration(seconds: 2),
  receiveTimeout: Duration(seconds: 2),
  sendTimeout: Duration(seconds: 2),
  logHttp: false,
  useMockData: true,
);

class _FakeAuth extends AuthController {
  @override
  Future<AuthSession?> build() async => AuthSession(
    accessToken: 'access',
    refreshToken: 'refresh',
    accessTokenExpiresAt: DateTime(2030),
    refreshTokenExpiresAt: DateTime(2030),
    user: UserModel(
      id: 'u1',
      email: 'admin@cila.gov.vn',
      fullName: 'Nguyễn Văn Duy',
      role: 'Admin',
      tenantId: 't1',
    ),
  );
}
