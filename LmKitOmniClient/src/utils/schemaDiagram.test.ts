import { describe, expect, it } from 'vitest'
import mermaid from 'mermaid'
import {
  buildErDiagram,
  columnName,
  columnType,
  diagramNotes,
  entityId,
  externalReferences,
  unresolvedForeignKeys,
  type SchemaDiagram
} from './schemaDiagram'

const column = (name: string, dataType: string, extra: Partial<{ isNullable: boolean; isPrimaryKey: boolean; isForeignKey: boolean }> = {}) => ({
  name,
  dataType,
  isNullable: extra.isNullable ?? false,
  isPrimaryKey: extra.isPrimaryKey ?? false,
  isForeignKey: extra.isForeignKey ?? false
})

const diagram = (over: Partial<SchemaDiagram> = {}): SchemaDiagram => ({
  connectionId: 'c1',
  name: 'kho-bao-cao',
  provider: 'Postgres',
  isActive: true,
  isIndexed: true,
  indexStatus: 'Completed',
  lastIndexedAtUtc: '2026-09-22T00:00:00Z',
  tableCount: 2,
  totalTableCount: 2,
  truncated: false,
  tables: [
    {
      schema: 'public',
      name: 'customers',
      qualifiedName: 'public.customers',
      columns: [column('id', 'integer', { isPrimaryKey: true }), column('name', 'character varying')],
      foreignKeys: []
    },
    {
      schema: 'public',
      name: 'orders',
      qualifiedName: 'public.orders',
      columns: [
        column('id', 'integer', { isPrimaryKey: true }),
        column('customer_id', 'integer', { isForeignKey: true })
      ],
      foreignKeys: [
        {
          column: 'customer_id',
          referencedTable: 'public.customers',
          referencedColumn: 'id',
          raw: 'customer_id → public.customers.id',
          isResolved: true
        }
      ]
    }
  ],
  relations: [
    {
      fromTable: 'public.orders',
      fromColumn: 'customer_id',
      toTable: 'public.customers',
      toColumn: 'id',
      targetIncluded: true
    }
  ],
  ...over
})

describe('buildErDiagram', () => {
  it('emits one entity per table with column types and key markers', () => {
    const source = buildErDiagram(diagram())

    expect(source.startsWith('erDiagram')).toBe(true)
    expect(source).toContain('"public.customers" {')
    expect(source).toContain('integer id PK')
    expect(source).toContain('character_varying name')
    expect(source).toContain('integer customer_id FK')
  })

  it('draws the foreign key as an edge from the child to the parent', () => {
    const source = buildErDiagram(diagram())

    expect(source).toContain('"public.orders" }o--|| "public.customers" : "customer_id → id"')
  })

  it('shows a nullable foreign key as zero-or-one parent', () => {
    const nullable = diagram()
    nullable.tables[1].columns[1].isNullable = true

    expect(buildErDiagram(nullable)).toContain('}o--o|')
  })

  it('marks a column that is both a primary and a foreign key', () => {
    const shared = diagram()
    shared.tables[1].columns[1] = column('customer_id', 'integer', { isPrimaryKey: true, isForeignKey: true })

    expect(buildErDiagram(shared)).toContain('integer customer_id PK, FK')
  })

  it('leaves out an edge whose parent table is not in the diagram', () => {
    const cut = diagram({
      relations: [{ ...diagram().relations[0], toTable: 'public.audit_log', targetIncluded: false }]
    })

    const source = buildErDiagram(cut)
    expect(source).not.toContain('audit_log')
    expect(externalReferences(cut)).toHaveLength(1)
  })

  it('renders a table with no columns as a bare entity instead of invalid syntax', () => {
    const empty = diagram({
      tables: [{ schema: 'main', name: 'flag', qualifiedName: 'flag', columns: [], foreignKeys: [] }],
      relations: []
    })

    expect(buildErDiagram(empty)).toContain('"flag" {')
  })

  // The generator's contract is "the parser accepts it": asserting the string alone would
  // pass while the diagram fails to render for a whole connection. Mermaid rejects an
  // attribute type containing a space and a name starting with a digit — both of which real
  // engines produce — so this case is deliberately nasty.
  it('produces source the real Mermaid parser accepts', async () => {
    const messy = diagram({
      tables: [
        {
          schema: 'public',
          name: 'bảng đơn',
          qualifiedName: 'public.bảng đơn',
          columns: [
            column('mã đơn', 'character varying', { isPrimaryKey: true }),
            column('2024_total', 'numeric'),
            column('số tiền', 'numeric(10,2)'),
            column('thời điểm', 'timestamp without time zone')
          ],
          foreignKeys: []
        },
        {
          schema: 'public',
          name: 'chi tiết',
          qualifiedName: 'public.chi tiết',
          columns: [column('mã đơn', 'character varying', { isNullable: true, isForeignKey: true })],
          foreignKeys: []
        }
      ],
      relations: [
        {
          fromTable: 'public.chi tiết',
          fromColumn: 'mã đơn',
          toTable: 'public.bảng đơn',
          toColumn: 'mã đơn',
          targetIncluded: true
        }
      ]
    })

    mermaid.initialize({ startOnLoad: false })
    await expect(mermaid.parse(buildErDiagram(messy))).resolves.toBeTruthy()
  })
})

describe('sanitising', () => {
  it('collapses a multi-word type into one token', () => {
    expect(columnType('timestamp without time zone')).toBe('timestamp_without_time_zone')
    expect(columnType('character varying')).toBe('character_varying')
    expect(columnType('')).toBe('unknown')
  })

  it('keeps diacritics but never starts a column name with a digit', () => {
    expect(columnName('mã đơn')).toBe('mã_đơn')
    expect(columnName('2024_total')).toBe('_2024_total')
    expect(columnName('   ')).toBe('column')
  })

  it('quotes entity names so dots and spaces survive', () => {
    expect(entityId('public.users')).toBe('"public.users"')
    expect(entityId('bảng đơn')).toBe('"bảng đơn"')
    expect(entityId('')).toBe('"unknown"')
  })
})

describe('diagramNotes', () => {
  it('reports truncation with both numbers', () => {
    const notes = diagramNotes(diagram({ truncated: true, tableCount: 40, totalTableCount: 137 }))

    expect(notes.join(' ')).toContain('40/137')
  })

  it('explains the missing edges and the unrestrained Mongo case', () => {
    const notes = diagramNotes(
      diagram({
        provider: 'Mongo',
        isIndexed: false,
        relations: [{ ...diagram().relations[0], targetIncluded: false }],
        tables: [
          {
            schema: '',
            name: 'events',
            qualifiedName: 'events',
            columns: [column('id', 'ObjectId', { isPrimaryKey: true })],
            foreignKeys: [{ column: 'a,b', referencedTable: '', referencedColumn: '', raw: 'a,b → t.x,y', isResolved: false }]
          }
        ]
      })
    )

    expect(notes).toHaveLength(4)
    expect(notes.join(' ')).toContain('không được vẽ')
    expect(notes.join(' ')).toContain('không tách được')
    expect(notes.join(' ')).toContain('MongoDB')
    expect(unresolvedForeignKeys(diagram({
      tables: [{
        schema: '',
        name: 'events',
        qualifiedName: 'events',
        columns: [],
        foreignKeys: [{ column: 'a,b', referencedTable: '', referencedColumn: '', raw: 'a,b → t.x,y', isResolved: false }]
      }]
    }))).toHaveLength(1)
  })

  it('says nothing when there is nothing to warn about', () => {
    expect(diagramNotes(diagram())).toEqual([])
  })
})
