/**
 * Turns the backend schema diagram (tables + columns + FK edges) into a Mermaid
 * `erDiagram` source, plus the notes an operator needs to read it honestly.
 *
 * Kept as a pure function (no DOM, no mermaid import) so it is unit-testable and so the
 * generator can be validated against the real Mermaid parser in tests.
 */

export interface SchemaColumn {
  name: string
  dataType: string
  isNullable: boolean
  isPrimaryKey: boolean
  isForeignKey: boolean
}

export interface SchemaForeignKey {
  column: string
  referencedTable: string
  referencedColumn: string
  raw: string
  isResolved: boolean
}

export interface SchemaTable {
  schema: string
  name: string
  qualifiedName: string
  columns: SchemaColumn[]
  foreignKeys: SchemaForeignKey[]
}

export interface SchemaRelation {
  fromTable: string
  fromColumn: string
  toTable: string
  toColumn: string
  targetIncluded: boolean
}

export interface SchemaDiagram {
  connectionId: string
  name: string
  provider: string
  isActive: boolean
  isIndexed: boolean
  indexStatus: string
  lastIndexedAtUtc: string | null
  tableCount: number
  totalTableCount: number
  truncated: boolean
  tables: SchemaTable[]
  relations: SchemaRelation[]
}

// Mermaid ER grammar limits we validated against mermaid 11:
//  - entity names MAY be quoted, so a real "public.users" / "bảng đơn" survives as-is;
//  - an attribute line is `type name [PK[, FK]]` — the TYPE must be ONE token, and an
//    attribute name must not start with a digit;
//  - commas are the key separator, so nothing else in a line may contain a bare comma.
// Anything that would break the grammar is normalised here instead of producing a diagram
// that fails to render for a whole connection.
const STRUCTURAL = /["`:{}\[\]|<>#%\r\n\t]/g
const WHITESPACE = /\s+/g

/** A Mermaid-safe entity name; quoted in the source so dots/spaces/diacritics stay readable. */
export function entityId(qualifiedName: string): string {
  const name = (qualifiedName ?? '').trim().replace(STRUCTURAL, '_')
  return `"${name.length > 0 ? name : 'unknown'}"`
}

/** `character varying` → `character_varying`: the type must be a single token. */
export function columnType(dataType: string): string {
  const type = (dataType ?? '').trim().replace(WHITESPACE, '_').replace(STRUCTURAL, '_')
  const collapsed = type.replace(/_{2,}/g, '_').replace(/^_+|_+$/g, '')
  if (collapsed.length === 0) return 'unknown'
  return /^[0-9]/.test(collapsed) ? `type_${collapsed}` : collapsed
}

/** Attribute names keep their diacritics (Mermaid accepts them) but must not start with a digit. */
export function columnName(name: string): string {
  const cleaned = (name ?? '').trim().replace(WHITESPACE, '_').replace(STRUCTURAL, '_')
  if (cleaned.length === 0) return 'column'
  return /^[0-9]/.test(cleaned) ? `_${cleaned}` : cleaned
}

export function relationLabel(relation: SchemaRelation): string {
  const label = relation.toColumn ? `${relation.fromColumn} → ${relation.toColumn}` : relation.fromColumn
  return `"${label.replace(/"/g, "'")}"`
}

/**
 * Relation line: the child (FK holder) is "zero or more", the parent is exactly one when the
 * FK column is NOT NULL and zero-or-one when it is nullable — so the picture shows whether a
 * child can exist without a parent.
 */
export function relationLine(relation: SchemaRelation, fromTable?: SchemaTable): string {
  const fkColumn = fromTable?.columns.find(
    (c) => c.name.toLowerCase() === relation.fromColumn.toLowerCase()
  )
  const parentSide = fkColumn && !fkColumn.isNullable ? '||' : 'o|'
  return `${entityId(relation.fromTable)} }o--${parentSide} ${entityId(relation.toTable)} : ${relationLabel(relation)}`
}

export function buildErDiagram(diagram: SchemaDiagram): string {
  const lines: string[] = ['erDiagram']
  const tablesById = new Map(diagram.tables.map((t) => [t.qualifiedName, t]))

  for (const table of diagram.tables) {
    lines.push(`    ${entityId(table.qualifiedName)} {`)
    for (const column of table.columns) {
      // A column that is both PK and FK is legal ("PK, FK") and worth showing.
      const keys = [column.isPrimaryKey ? 'PK' : '', column.isForeignKey ? 'FK' : ''].filter(Boolean)
      const suffix = keys.length > 0 ? ` ${keys.join(', ')}` : ''
      lines.push(`        ${columnType(column.dataType)} ${columnName(column.name)}${suffix}`)
    }
    lines.push('    }')
  }

  for (const relation of diagram.relations) {
    // Only draw an edge when BOTH ends exist: Mermaid silently invents an empty entity for
    // an unknown name, which would render as a confusing blank box.
    if (!relation.targetIncluded) continue
    if (!tablesById.has(relation.fromTable) || !tablesById.has(relation.toTable)) continue
    lines.push(`    ${relationLine(relation, tablesById.get(relation.fromTable))}`)
  }

  return lines.join('\n')
}

/** Edges the picture had to leave out, so the UI can list them instead of losing them. */
export function externalReferences(diagram: SchemaDiagram): SchemaRelation[] {
  return diagram.relations.filter((r) => !r.targetIncluded)
}

/** Foreign keys the backend could not split into a drawable edge (raw text kept). */
export function unresolvedForeignKeys(diagram: SchemaDiagram): SchemaForeignKey[] {
  return diagram.tables.flatMap((t) => t.foreignKeys.filter((fk) => !fk.isResolved))
}

/** Short, honest caveats shown next to the diagram. Empty when there is nothing to warn about. */
export function diagramNotes(diagram: SchemaDiagram): string[] {
  const notes: string[] = []

  if (diagram.truncated) {
    notes.push(
      `Sơ đồ chỉ vẽ ${diagram.tableCount}/${diagram.totalTableCount} bảng đầu tiên (theo thứ tự tên) — phần còn lại bị cắt cho dễ đọc.`
    )
  }

  const dangling = externalReferences(diagram).length
  if (dangling > 0) {
    notes.push(`${dangling} khoá ngoại trỏ tới bảng không nằm trong sơ đồ nên không được vẽ.`)
  }

  const unresolved = unresolvedForeignKeys(diagram).length
  if (unresolved > 0) {
    notes.push(`${unresolved} khoá ngoại không tách được thành cạnh (khoá tổ hợp hoặc thiếu thông tin).`)
  }

  if (diagram.provider.toLowerCase() === 'mongo') {
    notes.push('MongoDB không có khoá ngoại: mỗi "bảng" là một collection, cột là các trường lấy mẫu từ tài liệu.')
  }

  if (!diagram.isIndexed) {
    notes.push('Kết nối chưa lập chỉ mục schema — sơ đồ đọc trực tiếp từ CSDL nên vẫn xem được.')
  }

  if (diagram.tables.length === 0) {
    notes.push('CSDL này không có bảng nào để vẽ.')
  }

  return notes
}
