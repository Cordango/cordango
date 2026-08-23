// Reading and writing records, and turning a stored value into something a person can read.
//
// Everything the screens need from the server goes through here, so the wire format is decided once.
// The shapes it works in — entity keys, field keys, filters — are the App Definition's own, which is
// why a screen generated from a definition can be read next to the definition it came from.

import { api } from './api.js'
import { app } from './app.js'
import { session } from './session.js'

/** One entity's description, as the definition wrote it. */
export const entityOf = (key) => app.entities.find((e) => e.key === key) || null

/** One field's description. */
export const fieldOf = (entityKey, fieldKey) =>
  entityOf(entityKey)?.fields.find((f) => f.key === fieldKey) || null

/** One saved view. */
export const viewOf = (key) => app.views.find((v) => v.key === key) || null

/** The process governing an entity, if one does. */
export const processOf = (entityKey) => app.processes.find((p) => p.entity === entityKey) || null

/** The commands available on an entity. */
export const commandsOf = (entityKey) => app.commands.filter((c) => c.entity === entityKey)

/** The field whose value stands for the whole record in a link or a chip. */
export function displayOf(entityKey, record) {
  if (!record) return ''
  const entity = entityOf(entityKey)
  const key = entity?.displayField
  return String(record?.[key] ?? record?.id ?? '')
}

/**
 * Resolve the placeholders a definition may put in a filter value.
 *
 * `{{actor.id}}` is the signed-in PERSON, not their login — "expenses I submitted" compares against
 * a directory reference, and comparing it against an identity-table key would match nothing, on
 * every such view, without any error to notice.
 */
export function resolveValue(value, context) {
  if (typeof value !== 'string') return value

  return value
    .replace('{{actor.id}}', context?.personId ?? '')
    .replace('{{actor.userId}}', context?.userId ?? '')
    .replace('{{today}}', new Date().toISOString().slice(0, 10))
}

/**
 * Does a block's `visibleWhen` hold right now?
 *
 * Evaluated in the browser, against the scope the block is in: the record it is bound to, the
 * screen's own state, or the person looking at it. That is what lets a section appear only while a
 * request is pending, or a day/week toggle swap which grid renders, without a round trip.
 *
 * A block with no condition is always visible, and a condition naming none of eq/neq/in is too —
 * the schema allows it and "shown" is the safer reading of "the author wrote a condition and did not
 * say what it tests".
 */
export function visibleWhen(condition, record, state) {
  if (!condition?.value) return true

  const resolved = resolveScope(condition.value, record, state)

  if ('eq' in condition) return resolved === condition.eq
  if ('neq' in condition) return resolved !== condition.neq
  if ('in' in condition) return (condition.in || []).includes(resolved)
  return true
}

/** `{{record.status}}`, `{{state.cursor}}`, `{{actor.id}}` — or a literal, unchanged. */
function resolveScope(expression, record, state) {
  const match = /^\{\{\s*([\w.]+)\s*\}\}$/.exec(expression)
  if (!match) return expression

  const [scope, ...rest] = match[1].split('.')
  const key = rest.join('.')

  if (scope === 'record') return record?.[key]
  if (scope === 'state') return state?.[key]
  if (scope === 'actor') return key === 'id' ? session.personId : session[key]
  return undefined
}

/** Turn a definition filter into the query-string term the API expects. */
export const filterTerm = (filter, context) =>
  `${filter.field}:${filter.operator}:${resolveValue(filter.value ?? '', context) ?? ''}`

/** Turn a definition sort list into the API's `sort` parameter. */
export const sortTerm = (sort) =>
  (sort || []).map((s) => (s.direction === 'desc' ? `-${s.field}` : s.field)).join(',')

/** A page of records for a view. */
export async function loadView(view, context, { skip = 0, take = 100, extraFilters = [] } = {}) {
  const params = new URLSearchParams()
  for (const filter of [...(view.filters || []), ...extraFilters]) {
    params.append('filter', filterTerm(filter, context))
  }
  const sort = sortTerm(view.sort)
  if (sort) params.set('sort', sort)
  params.set('skip', String(skip))
  params.set('take', String(take))

  return api.get(`/api/${view.entity}?${params}`)
}

/** A page of records for an entity, with filters given directly. */
export async function loadRecords(entityKey, { filters = [], sort = '', skip = 0, take = 100, context } = {}) {
  const params = new URLSearchParams()
  for (const filter of filters) params.append('filter', filterTerm(filter, context))
  if (sort) params.set('sort', sort)
  params.set('skip', String(skip))
  params.set('take', String(take))
  return api.get(`/api/${entityKey}?${params}`)
}

export const loadRecord = (entityKey, id) => api.get(`/api/${entityKey}/${encodeURIComponent(id)}`)
export const createRecord = (entityKey, body) => api.post(`/api/${entityKey}`, body)
export const updateRecord = (entityKey, id, body) => api.patch(`/api/${entityKey}/${encodeURIComponent(id)}`, body)
export const deleteRecord = (entityKey, id) => api.delete(`/api/${entityKey}/${encodeURIComponent(id)}`)

/** Run a command against one record. */
export const runCommand = (entityKey, id, command, input) =>
  api.post(`/api/${entityKey}/${encodeURIComponent(id)}/commands/${command}`, input ?? {})

/**
 * An aggregate over an entity — what a stat and a chart both need.
 *
 * Computed on the server, because a figure over a hundred thousand rows must not require the
 * browser to fetch a hundred thousand rows to add them up.
 */
export async function loadAggregate(source, context) {
  const params = new URLSearchParams()
  for (const filter of source.filters || []) params.append('filter', filterTerm(filter, context))

  const aggregate = source.aggregate || { op: 'count' }
  params.set('op', aggregate.op)
  if (aggregate.field) params.set('field', aggregate.field)
  if (aggregate.groupBy) params.set('groupBy', aggregate.groupBy)

  return api.get(`/api/${source.entity}/aggregate?${params}`)
}

// ---- formatting ------------------------------------------------------------------------------
//
// The locale comes from the application, never from the browser. `toLocaleString(undefined, …)`
// formats in whatever language the reader's browser happens to be set to, so a German workspace
// renders English dates for anyone whose laptop is English — and both spellings produce a plausible
// date, so nothing looks wrong.

let currentLocale = () => 'en'

/** Called once by main.js, with the application's own locale. */
export function setLocaleSource(source) {
  if (typeof source === 'function') currentLocale = source
}

export function formatValue(value, field) {
  if (value === null || value === undefined || value === '') return ''
  const locale = currentLocale()

  switch (field?.type) {
    case 'money':
      return new Intl.NumberFormat(locale, {
        style: 'currency',
        currency: field.currency || 'EUR',
      }).format(Number(value))

    case 'decimal':
    case 'integer': {
      const number = new Intl.NumberFormat(locale).format(Number(value))
      return field.unit ? `${number} ${field.unit}` : number
    }

    case 'date':
      return new Intl.DateTimeFormat(locale, { dateStyle: 'medium' }).format(new Date(value))

    case 'datetime':
      return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value))

    case 'boolean':
      return value ? '✓' : '—'

    case 'select':
      return field.options?.find((o) => o.value === value)?.label ?? String(value)

    case 'multiselect':
      return (Array.isArray(value) ? value : [])
        .map((v) => field.options?.find((o) => o.value === v)?.label ?? v)
        .join(', ')

    default:
      return String(value)
  }
}

/** The colour a select option carries, for a chip or a board column. */
export const optionColor = (field, value) =>
  field?.options?.find((o) => o.value === value)?.color || undefined

/** The label a select option carries. */
export const optionLabel = (field, value) =>
  field?.options?.find((o) => o.value === value)?.label ?? String(value ?? '')

/**
 * A record of `entityKey` was created, changed or deleted somewhere on this page.
 *
 * Lists load themselves and have no way of knowing that a button three blocks away just added a
 * row. Rather than thread a refresh callback through every container between them, the two ends
 * meet on one window event: whoever writes says so, whoever is showing that entity reloads.
 */
export const recordsChanged = (entityKey) =>
  window.dispatchEvent(new CustomEvent('cordango:records-changed', { detail: entityKey }))

/** Listen for the above. Returns the unsubscribe, for onUnmounted. */
export function onRecordsChanged(matches, handler) {
  const listener = (event) => {
    if (!matches || event.detail === matches) handler(event.detail)
  }
  window.addEventListener('cordango:records-changed', listener)
  return () => window.removeEventListener('cordango:records-changed', listener)
}
