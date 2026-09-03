// Turning an authored `groupBy` into the sections a table draws — and, where the sections are
// records, into the identity needed to add, rename and delete them.
//
// TWO KINDS OF GROUP, and the difference is what you may do to them. A SELECT field's groups are its
// options: they come from the definition, there is nothing to fetch, and nobody can add one from a
// table because adding one means changing the application. A REFERENCE field's groups are ROWS in
// another entity — a project's task sections — and those are ordinary records somebody can create,
// rename and delete while they work, which is the whole point of sections in a task list.
//
// So the resolver answers the first question and `groupTarget` answers the second, and a table that
// gets null from `groupTarget` simply does not offer the section controls.

import { entityOf, displayOf, loadRecords, optionLabel } from './records.js'

/**
 * The CRUD identity behind a reference groupBy: which entity the sections live in, the field that
 * holds their name, the reference that scopes them to the record this table is inside, and the
 * number they sort by.
 *
 * <p>Inferred rather than authored, deliberately. A section entity is already fully described —
 * `task_section` has a name, a `project` reference and an `order` — and asking an author to repeat
 * all three in the groupBy would be three more things to get out of step with the entity.</p>
 *
 * <p>Null for a select groupBy, and null is the answer that matters: options are not records, so
 * there is nothing to create.</p>
 */
export function groupTarget(entity, spec, parentEntityKey) {
  const field = entity?.fields?.find((f) => f.key === spec?.field)
  if (!field || field.type !== 'reference' || field.targetApp) return null

  const target = entityOf(field.targetEntity)
  if (!target) return null

  const scope = (target.fields || []).find(
    (f) => f.type === 'reference' && f.targetEntity === parentEntityKey && !f.targetApp)
  const name = target.displayField || (target.fields || []).find((f) => f.type === 'text')?.key
  const order = (target.fields || []).find((f) => ['integer', 'decimal'].includes(f.type))?.key

  return name ? { entityKey: field.targetEntity, scopeKey: scope?.key ?? null, nameField: name, orderField: order } : null
}

/**
 * The ordered sections, as `[{ id, label, order }]`.
 *
 * <p>Null when the spec cannot be resolved — an unknown field, a target that is not there, a failed
 * request. The table then draws itself ungrouped, which is the right degradation: a flat list of the
 * right rows beats an empty screen, and the rows are all still present.</p>
 */
export async function resolveGroups(entity, spec, parentEntityKey, parentId) {
  const field = entity?.fields?.find((f) => f.key === spec?.field)
  if (!field) return null

  if (field.type === 'select') {
    return (field.options || []).map((o) => ({ id: o.value, label: o.label ?? optionLabel(field, o.value) }))
  }

  // Local references only. A platform reference points at a directory this application does not own,
  // so its rows are not sections somebody can reorganise from here.
  if (field.type !== 'reference' || field.targetApp) return null

  const target = entityOf(field.targetEntity)
  if (!target) return null

  let rows
  try {
    rows = (await loadRecords(field.targetEntity, { take: 200 }))?.items ?? []
  } catch {
    return null
  }

  // Scope to the record this table is inside. Without this, every project's task list would offer
  // every project's sections — which is not a longer list, it is the wrong one.
  if (parentEntityKey && parentId != null) {
    const scope = (target.fields || []).find(
      (f) => f.type === 'reference' && f.targetEntity === parentEntityKey && !f.targetApp)
    if (scope) rows = rows.filter((r) => r[scope.key] === parentId)
  }

  const by = spec.orderBy
  if (by) {
    rows = [...rows].sort((a, b) => {
      const an = Number(a[by])
      const bn = Number(b[by])
      if (!Number.isNaN(an) && !Number.isNaN(bn)) return an - bn
      return String(a[by] ?? '').localeCompare(String(b[by] ?? ''), undefined, { numeric: true })
    })
  }

  return rows.map((r) => ({ id: r.id, label: displayOf(field.targetEntity, r), order: by ? r[by] : undefined }))
}
