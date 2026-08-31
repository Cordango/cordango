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
 *
 * `{{state.<key>}}` reads the screen's own state, which is what makes a page filter bar drive a
 * list: the facet writes the state var, the list's filter reads it, and neither knows about the
 * other. `{{today+7}}` / `{{today-2w}}` shift the clock token by days or weeks.
 */
export function resolveValue(value, context) {
  // A range is a pair, and both ends can be tokens: `between` takes ['{{today}}', '{{today+7}}'].
  if (Array.isArray(value)) return value.map((one) => resolveValue(one, context))
  if (typeof value !== 'string') return value

  return value.replace(/\{\{\s*([^}]+?)\s*\}\}/g, (whole, token) => {
    const clock = /^(today|now)(?:\s*([+-])\s*(\d+)\s*([dwh])?)?$/.exec(token)
    if (clock) return clockToken(clock)

    const [scope, ...rest] = token.split('.')
    const key = rest.join('.')
    if (scope === 'actor') return String((key === 'id' ? context?.personId : context?.[key]) ?? '')
    if (scope === 'state') return String(context?.state?.[key] ?? '')
    if (scope === 'record') return String(context?.record?.[key] ?? '')
    return whole
  })
}

function clockToken([, base, sign, amount, unit]) {
  const at = new Date()
  if (sign) {
    const by = Number(amount) * (sign === '-' ? -1 : 1)
    if (unit === 'h') at.setHours(at.getHours() + by)
    else at.setDate(at.getDate() + by * (unit === 'w' ? 7 : 1))
  }
  return base === 'now' ? at.toISOString() : at.toISOString().slice(0, 10)
}

/**
 * Is this filter leaf one to send at all?
 *
 * An `optional` leaf is SKIPPED while its resolved value is empty. That is what lets a facet mean
 * "all owners" rather than "owner is blank": an unset dropdown drops the leaf out of the query
 * instead of narrowing it to the rows nobody owns.
 */
export function filterApplies(filter, context) {
  if (!filter?.optional) return true
  const resolved = resolveValue(filter.value ?? '', context)
  if (Array.isArray(resolved)) return resolved.every((one) => one !== '' && one !== null && one !== undefined)
  return resolved !== '' && resolved !== null && resolved !== undefined
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

/**
 * Turn a definition filter into the query-string term the API expects.
 *
 * A list value is joined with `|`, the separator the server already splits `in` on, so `between`
 * and `in` share one encoding rather than each inventing its own. Joining with a comma would work
 * until the first value that contains one.
 */
export function filterTerm(filter, context) {
  const resolved = resolveValue(filter.value ?? '', context)
  const value = Array.isArray(resolved) ? resolved.join('|') : (resolved ?? '')
  return `${filter.field}:${filter.operator}:${value}`
}

/** Turn a definition sort list into the API's `sort` parameter. */
export const sortTerm = (sort) =>
  (sort || []).map((s) => (s.direction === 'desc' ? `-${s.field}` : s.field)).join(',')

/** A page of records for a view. */
export async function loadView(
  view, context, { skip = 0, take = 100, extraFilters = [], sort: order = null } = {}) {
  const params = new URLSearchParams()
  for (const filter of [...(view.filters || []), ...extraFilters]) {
    if (filterApplies(filter, context)) params.append('filter', filterTerm(filter, context))
  }
  // The view's own order, unless the caller needs a different one over the SAME rows — a timeline
  // asking "where is the nearest record" wants the earliest by date and still wants this view's
  // filters, so that it never offers to jump to something the view excludes.
  const sort = order ?? sortTerm(view.sort)
  if (sort) params.set('sort', sort)
  params.set('skip', String(skip))
  params.set('take', String(view.limit ?? take))

  return api.get(`/api/${view.entity}?${params}`)
}

/** A page of records for an entity, with filters given directly. */
export async function loadRecords(entityKey, { filters = [], sort = '', skip = 0, take = 100, context } = {}) {
  const params = new URLSearchParams()
  for (const filter of filters) {
    if (filterApplies(filter, context)) params.append('filter', filterTerm(filter, context))
  }
  if (sort) params.set('sort', sort)
  params.set('skip', String(skip))
  params.set('take', String(take))
  return api.get(`/api/${entityKey}?${params}`)
}

/**
 * Does this row match a free-text query?
 *
 * Matched in the browser, over the page already loaded, because the query has to match what a
 * person SEES: a reference column reads as somebody's name and the column holding it stores a uuid,
 * so a server-side `contains` over that column would match nothing anybody typed. `labelFor` is how
 * the caller lends what it has already resolved; without one a reference matches on its raw id.
 *
 * The cost is that it narrows the loaded page rather than the table, which is the same bargain the
 * language's own `filterBar` describes ("narrows them CLIENT-SIDE").
 */
export function matchesSearch(row, query, entityKey, fields, labelFor) {
  const needle = String(query ?? '').trim().toLowerCase()
  if (!needle) return true

  const entity = entityOf(entityKey)
  const all = entity?.fields ?? []
  const searched = fields?.length ? all.filter((f) => fields.includes(f.key)) : all.filter((f) => !f.system)

  return searched.some((field) => {
    const value = row?.[field.key]
    if (value === null || value === undefined || value === '') return false
    const shown = labelFor?.(field, value) ?? formatValue(value, field)
    return String(shown).toLowerCase().includes(needle)
  })
}

/**
 * The rows of a referenced entity, as {value,label} choices — a facet dropdown's options, and what
 * turns a reference column's uuid back into the name somebody typed.
 *
 * Cached per entity for the life of the page: a page with three facets over the same entity asks
 * once. The cache holds the promise rather than the answer, so three simultaneous askers still make
 * one request.
 */
const referenceCache = new Map()

export function referenceOptions(entityKey, route) {
  if (!entityKey) return Promise.resolve([])
  const path = route ?? `/api/${entityKey}`
  if (!referenceCache.has(path)) {
    referenceCache.set(
      path,
      api.get(`${path}?take=200`)
        .then((page) => (page?.items ?? []).map((r) => ({
          value: r.id,
          label: r.full_name ?? r.name ?? displayOf(entityKey, r),
        })))
        .catch(() => []))
  }
  return referenceCache.get(path)
}

/**
 * Where a reference field's records are READ from, as an api path without the `/api/` prefix.
 *
 * Two homes, and `targetApp` is the whole of the discriminator. A reference with no `targetApp`
 * points at one of this application's own entities and is served by that entity's controller. A
 * reference WITH one points into the directory — the People, Departments, Groups, Organizations and
 * Contacts every application carries, whichever `targetApp` names them, because a standalone build
 * has one directory rather than one per application it was linked against.
 *
 * This used to test for `platform` and for the literal entity `person`, which sent everything else —
 * a customer, a contact — to `/api/organization`, a route that does not exist in a generated
 * application. The picker then showed nothing at all, with no error a person could see: the field
 * looked optional and was unfillable.
 */
export function referenceRoute(field) {
  const target = field?.targetEntity ?? 'person'
  return field?.targetApp ? `directory/${target}` : target
}

/**
 * The values a facet dropdown offers for one field, and what to call each.
 *
 * A select carries its own options. A process-governed status field's values are the process's
 * states, not the field's — asking the field would offer states the lifecycle has since dropped. A
 * reference's values are the referenced records, which means a request; the platform directory is a
 * reference whose records live outside this application's tables.
 */
export function fieldChoices(entityKey, fieldKey) {
  const field = fieldOf(entityKey, fieldKey)
  if (!field) return Promise.resolve([])

  const process = processOf(entityKey)
  if (process?.stateField === fieldKey && (process.states || []).length) {
    return Promise.resolve(process.states.map((s) => ({ value: s.key, label: s.label ?? s.key })))
  }

  if (field.options?.length) {
    return Promise.resolve(field.options.map((o) => ({ value: o.value, label: o.label ?? o.value })))
  }

  if (field.type === 'reference') {
    const route = referenceRoute(field)
    return referenceOptions(field.targetEntity ?? 'person', `/api/${route}`)
  }

  if (field.type === 'boolean') {
    return Promise.resolve([{ value: 'true', label: 'Yes' }, { value: 'false', label: 'No' }])
  }

  return Promise.resolve([])
}

/**
 * Where a record opens.
 *
 * The same four lists — table, board, calendar, timeline — each spelled this out for themselves, so
 * changing where a record lives meant finding all four and the routes they were built from. It is
 * also the shape `@cordango/web-controls` asks a host for: its `route(target, id)` seam takes the
 * RESOLVED `{ handle, entity, manifest }` a reference points at rather than a bare key, so the
 * adapter reads `target.entity` and comes through here.
 */
export const recordRoute = (entityKey, id) =>
  `/record/${entityKey}/${encodeURIComponent(id)}`

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

/**
 * A BLOCK's `format`, which is not a field's `type`.
 *
 * A field is formatted by what it IS — a money column is money wherever it appears. A stat is
 * formatted by what the author says it MEANS at this spot on this screen, and the two vocabularies
 * do not overlap: there is no `share` or `multiple` field type, and `formatValue` fell through to
 * `String(value)` for every one of them. A count of 12400 rendered as "12400".
 *
 * `share` and `percent` take a FRACTION — 0.42 prints as 42%.
 */
export function formatStat(value, format, options = {}) {
  if (value === null || value === undefined || value === '') return '—'

  const locale = currentLocale()
  const number = Number(value)

  switch (format) {
    case 'money':
      return new Intl.NumberFormat(locale, {
        style: 'currency',
        currency: options.currency || 'EUR',
      }).format(number)

    case 'percent':
    case 'share':
      return new Intl.NumberFormat(locale, { style: 'percent', maximumFractionDigits: 1 }).format(number)

    case 'multiple':
      return `${new Intl.NumberFormat(locale, { maximumFractionDigits: 2 }).format(number)}×`

    case 'date':
      return new Intl.DateTimeFormat(locale, { dateStyle: 'medium' }).format(new Date(value))

    case 'datetime':
      return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' })
        .format(new Date(value))

    default:
      // Including 'number', and including no format at all. A figure that is not a number falls
      // back to its own text rather than to NaN.
      return Number.isFinite(number)
        ? new Intl.NumberFormat(locale, { maximumFractionDigits: 2 }).format(number)
        : String(value)
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

/**
 * The event the shell's snackbar listens for.
 *
 * A CONSTANT rather than a string at each end, because it was a string at each end and the two
 * ends did not match: the shell listened on one name and the command button dispatched another, so
 * every message a command returned was thrown into an empty room. Nothing errored, nothing logged,
 * and the only symptom was a confirmation that never appeared.
 */
export const TOAST_EVENT = 'cordango:toast'

/** Say something to the person using the application. `tone` is info | success | warning | error. */
export const toast = (message, tone = 'info') =>
  window.dispatchEvent(new CustomEvent(TOAST_EVENT, { detail: { message, tone } }))
