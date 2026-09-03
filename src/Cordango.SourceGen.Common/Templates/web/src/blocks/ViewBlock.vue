<script setup>
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { CordangoDataTable, statusCellMoves } from '@cordango/web-controls'
import {
  viewOf, entityOf, loadView, commandsOf, createRecord, updateRecord, deleteRecord,
  referenceOptions, referenceRoute, onRecordsChanged, recordsChanged, matchesSearch, formatValue, resolveValue,
  recordRoute, toast,
} from '../records.js'
import { app } from '../app.js'
import { groupTarget, resolveGroups } from '../tableGroups.js'
import { session } from '../session.js'
import RecordDialog from './RecordDialog.vue'
import CommandButton from './CommandButton.vue'
import EmptyState from './EmptyState.vue'
import RecordPeek from './RecordPeek.vue'
import { useSurface } from './surface.js'

// The LIST, and what this component is: the host around a shared control.
//
// The table itself is `CordangoDataTable` from `@cordango/web-controls` — the same one the platform
// renders — and this file is what it needs a host to be. That split is the package's own and it is
// deliberate: the control takes rows as PROPS and emits INTENTS, because a control that fetched its
// own data would need a data seam, and a default for that seam would render convincing rows nobody
// could tell were fictional.
//
// So: everything about WHICH rows belongs here — resolving a saved view, `{{actor.id}}` and
// `{{record.id}}` in a filter, the request, the reload — and everything about how a table BEHAVES
// belongs there. What arrived with it, none of which the hand-rolled table had: sortable columns,
// per-column filters, show/hide and reorder with per-person persistence, density, CSV, pagination,
// drag-and-drop ordering, inline section management, and a status cell that offers the lifecycle's
// legal moves rather than every option the field has.

const props = defineProps({
  // A SAVED view, by key.
  view: String,
  // Or the block's own query, inline. A `table` block carries its filters, sort and columns itself
  // rather than pointing at a saved view, and reading them off the block is the whole difference
  // between "Open tasks" and "every task there has ever been".
  definition: { type: Object, default: null },
  // Extra conditions the page adds on top of the view's own — a stat card's deep link.
  extraFilters: { type: Array, default: () => [] },
  // The screen's state, so `{{state.x}}` in a filter resolves and the list reloads when a facet
  // moves.
  state: { type: Object, default: null },
  // Bound to a PAGE-level filterbar's search box: `{ state, fields }`.
  search: { type: Object, default: null },
  // This list's OWN toolbar: `{ search: [...fields], facets: [...fields] }`.
  filterBar: { type: Object, default: null },
  create: { type: Boolean, default: true },
  // The page heading already says this. Set by the emitter when the two would be the same words,
  // never by the definition — an author naming a list is not making a mistake.
  hideTitle: { type: Boolean, default: false },
  allowDelete: { type: Boolean, default: false },
  // Edit cells where they sit. The first column is left alone: it is the one that opens the record,
  // and a column that both opens and edits does neither predictably.
  inlineEdit: { type: Boolean, default: false },
  // The record this list is inside, on a detail screen. A `{{record.id}}` in a filter resolves
  // against it, which is how a table narrowed with `via` finds its parent's children.
  record: { type: Object, default: null },
  // Split the rows into labelled sections by a discrete field.
  groupBy: { type: Object, default: null },
  // The numeric field rows sort by, and the one a drag writes.
  orderField: { type: String, default: null },
  // What a new record starts with, beyond what this list's own equality filters already imply.
  newDefaults: { type: Object, default: null },
  // A row click opens a quick-look panel rather than navigating. Opt-in: for a list whose rows ARE
  // the work, the full page is the right destination and a panel is one more thing to dismiss.
  openDetail: { type: Boolean, default: false },
})

const router = useRouter()

// A table inside a `card` block is already in a card. Two borders around one table is the same
// box-in-a-box the stats had.
const depth = useSurface()
const definition = computed(() => props.definition ?? viewOf(props.view))
const entityKey = computed(() => definition.value?.entity)
const entity = computed(() => entityOf(entityKey.value))

// A saved view carries its presentation in its own `config`, and the block that renders one passes
// nothing but the key: `<ViewBlock view="all_tasks_table" />` is the whole of it. So the view's
// settings are the FALLBACK for every switch a block can also set.
//
// Without this the same words in a definition mean two different screens — `inlineEdit: true` under
// `kind: table` gives you editable cells, and the identical line under a saved view's `settings`
// gives you nothing — which is a difference nobody can see by reading either one.
//
// The block still wins wherever it says something: a screen placing a list is more specific than
// the list's own defaults.
const settings = computed(() => {
  const config = definition.value?.config ?? {}
  return {
    filterBar: props.filterBar ?? config.filterBar ?? null,
    groupBy: props.groupBy ?? config.groupBy ?? null,
    orderField: props.orderField ?? config.orderField ?? null,
    inlineEdit: props.inlineEdit || config.inlineEdit === true,
    allowDelete: props.allowDelete || config.allowDelete === true,
    openDetail: props.openDetail || config.openDetail === true,
  }
})

const rows = ref([])
const total = ref(0)
const loading = ref(true)
const error = ref(null)
const editing = ref(null)
const confirming = ref(null)
const pending = ref(null)

const columns = computed(() => {
  const keys = definition.value?.config?.columns
    ?? definition.value?.columns
    // No columns named means every field somebody could have entered. Not every field: the audit
    // stamps would push the ones people care about off the right of the screen.
    ?? entity.value?.fields.filter((f) => !f.system).slice(0, 6).map((f) => f.key)
    ?? []
  return keys
    .map((key) => entity.value?.fields.find((f) => f.key === key))
    .filter(Boolean)
    .map((field) => ({ key: field.key, title: field.label, field }))
})

const rowCommands = computed(() =>
  commandsOf(entityKey.value).filter((c) => (c.placements || []).includes('tableRow')))

// The control asks for the count up front so it can reserve the column, and resolves per row so a
// guard can hide one. Nothing here is guarded on the client: this application has no access map, and
// its command button has always offered the command and let the server refuse. Pre-filtering here
// with a map we do not have would hide commands people are allowed to run.
const commandsOnRow = computed(() =>
  rowCommands.value.length
    ? { count: rowCommands.value.length, resolve: () => rowCommands.value }
    : null)

// Same reasoning, one layer down: `statusCellMoves` filters transitions by an access map, and an
// absent one reads as "may run nothing" — which would empty the status cell of every legal move.
// Permissive, and the server is still the authority.
const access = computed(() => ({ [entityKey.value]: { commands: ['*'] } }))

const transitionsFor = (record) =>
  statusCellMoves(app, entityKey.value, record, access.value, session.personId)

// Reference columns arrive as ids. Resolving them here rather than per cell means the table asks
// once for the whole page instead of once per row, and it is the same map the search reads so a
// query matches the name a person can actually see.
const labels = ref({})

// Where a reference cell LINKS to. The package's `route(target, id)` seam takes a resolved target
// rather than a bare key, so it is handed the shape it reads.
const refTargets = computed(() => Object.fromEntries(
  columns.value
    .filter((c) => c.field.type === 'reference' && !c.field.targetApp && c.field.targetEntity)
    .map((c) => [c.key, { entity: c.field.targetEntity }])))

async function resolveLabels() {
  const grouping = settings.value.groupBy?.field
    ? entity.value?.fields.filter((f) => f.key === settings.value.groupBy.field) ?? []
    : []
  const references = [...columns.value.map((c) => c.field), ...grouping].filter((f) => f.type === 'reference'
    && f.targetApp !== 'platform' && f.targetEntity && f.targetEntity !== 'person')
  const resolved = {}
  for (const field of references) {
    const options = await referenceOptions(field.targetEntity, `/api/${referenceRoute(field)}`)
    resolved[field.key] = Object.fromEntries(options.map((o) => [o.value, o.label]))
  }
  labels.value = resolved
}

const labelFor = (field, value) => labels.value[field.key]?.[value] ?? formatValue(value, field)

// --- sections -----------------------------------------------------------------------------------

const groups = ref(null)

// WHICH record this list sits inside, by entity.
//
// Read off the `via` filter rather than passed in: a child list is narrowed by `project eq
// {{record.id}}`, so the field that names the parent is already there, and its target IS the parent's
// entity. That is what scopes the sections — a project's task list must offer that project's
// sections and no other project's.
const parentEntityKey = computed(() => {
  if (!props.record) return null
  const via = (definition.value?.filters || [])
    .find((f) => String(f.value ?? '').includes('record.id'))
  return via
    ? entity.value?.fields.find((f) => f.key === via.field)?.targetEntity ?? null
    : null
})

// A section is a RECORD only when the grouping field points at one, and that is exactly what decides
// whether this table offers to add, rename or delete sections at all.
const target = computed(() =>
  settings.value.groupBy?.field
    ? groupTarget(entity.value, settings.value.groupBy, parentEntityKey.value)
    : null)

async function loadGroups() {
  if (!settings.value.groupBy?.field) { groups.value = null; return }
  groups.value = await resolveGroups(
    entity.value, settings.value.groupBy, parentEntityKey.value, props.record?.id ?? null)
}

// Everything a `{{...}}` in a filter can be resolved against, in one object, so records.js does not
// have to know where any of it came from.
const context = computed(() => ({
  personId: session.personId,
  userId: session.userId,
  state: props.state ?? {},
  record: props.record ?? {},
}))

/**
 * What a new row starts with.
 *
 * The list's own equality filters come first and need no declaring: a list showing `my_day eq true`
 * that added rows with `my_day` unset would create something that vanishes on the next load. A
 * comparison says which rows to SHOW rather than what a new one should BE, so only `eq` is read,
 * and `newDefaults` says the rest.
 */
const blank = computed(() => {
  const seed = {}
  for (const filter of definition.value?.filters || []) {
    if (filter.operator === 'eq' && filter.field && !filter.optional) {
      seed[filter.field] = resolveValue(filter.value, context.value)
    }
  }
  for (const [key, value] of Object.entries(props.newDefaults || {})) {
    seed[key] = resolveValue(value, context.value)
  }
  return seed
})

async function load() {
  if (!definition.value) {
    error.value = `No view named '${props.view}'.`
    loading.value = false
    return
  }
  loading.value = true
  error.value = null
  try {
    const page = await loadView(definition.value, context.value, { extraFilters: props.extraFilters })
    rows.value = page?.items ?? []
    total.value = page?.total ?? 0
    await resolveLabels()
    await loadGroups()
  } catch (failure) {
    error.value = failure.message
    rows.value = []
  } finally {
    loading.value = false
  }
}

// Narrowed before the table sees them, and only by the PAGE's search box. This list's own toolbar is
// the control's — it searches resolved reference names and remembers its state per person, neither
// of which a filter here could do.
const visible = computed(() => {
  if (!props.search || !props.state) return rows.value
  return rows.value.filter((r) =>
    matchesSearch(r, props.state[props.search.state], entityKey.value, props.search.fields, labelFor))
})

// --- what the table asks us to do ---------------------------------------------------------------

// One place, because every one of these is the same three steps and the same failure: write, reload,
// and tell the rest of the page. A failed write RELOADS rather than leaving the optimistic value on
// screen — a row that still shows what you typed after the server refused it is the worst of the
// three possible answers.
async function write(action) {
  try {
    await action()
    recordsChanged(entityKey.value)
  } catch (failure) {
    toast(failure.message, 'error')
  } finally {
    await load()
  }
}

const onCellEdit = (record, patch) => write(() => updateRecord(entityKey.value, record.id, patch))

// A drag, a move-to-section, a promote. The control has already worked out the patch — which
// section, which position — because it is the one that knows where the row was dropped.
const onMove = (record, patch) => write(() => updateRecord(entityKey.value, record.id, patch))

// An inline add: one field typed in the row itself. Everything else comes from what the list already
// implies — its own filters, the section it was added under, the position after its last sibling.
function onAdd({ title, groupId, order }) {
  const body = { ...blank.value }
  const name = entity.value?.displayField
    ?? entity.value?.fields.find((f) => f.type === 'text' && f.required)?.key
    ?? entity.value?.fields.find((f) => f.type === 'text')?.key
  if (!name) return
  body[name] = title
  if (settings.value.groupBy?.field && groupId != null) body[settings.value.groupBy.field] = groupId
  if (settings.value.orderField && order != null) body[settings.value.orderField] = order
  return write(() => createRecord(entityKey.value, body))
}

// Sections are records, so managing them is ordinary CRUD against another entity — which is why the
// table emits an intent instead of writing: it has no idea a section is a `task_section` scoped to a
// project, and it should not have to.
function onAddGroup(name) {
  const t = target.value
  if (!t) return
  const body = { [t.nameField]: name }
  if (t.scopeKey && props.record?.id) body[t.scopeKey] = props.record.id
  if (t.orderField) {
    const last = Math.max(0, ...(groups.value || []).map((g) => Number(g.order) || 0))
    body[t.orderField] = last + 10
  }
  return write(() => createRecord(t.entityKey, body))
}

function onRenameGroup(id, name) {
  const t = target.value
  return t ? write(() => updateRecord(t.entityKey, id, { [t.nameField]: name })) : undefined
}

// The rows do not go with it. They stay on screen because grouping puts a row whose group is not in
// the list into the trailing "no section" bucket — which is the right OUTCOME, reached the wrong
// way: their `section` still holds the id of a section that no longer exists.
//
// `onDelete: setNull` is declared on the field and NOTHING IN A GENERATED APPLICATION READS IT — the
// compiler parses and validates it, and no emitter or runtime ever acts on it. So this delete leaves
// a dangling reference exactly as every other delete in the application does. Not a reason to make
// this one delete special: one entity quietly cleaning up after itself while the rest do not is
// harder to reason about than a gap that is the same everywhere.
function onDeleteGroup(id) {
  const t = target.value
  return t ? write(() => deleteRecord(t.entityKey, id)) : undefined
}

async function remove(row) {
  confirming.value = null
  await write(() => deleteRecord(entityKey.value, row.id))
}

// A create button three blocks away has no way to tell this list it added a row, so the two ends
// meet on one window event instead of threading a callback through every container between them.
let stop
onMounted(() => {
  load()
  stop = onRecordsChanged(entityKey.value, load)
})
onUnmounted(() => stop?.())
watch(() => props.extraFilters, load, { deep: true })
// A page facet writes screen state, and this list's filters read it. Reloading on any state change
// is coarser than watching the keys this view happens to name, and it cannot go stale when the
// filters change.
watch(() => props.state, load, { deep: true })

// A title of its own is worth drawing only when this block is the outermost surface and the
// definition actually named it.
const titled = computed(() => !props.hideTitle && depth === 0 && Boolean(definition.value?.label))

const peeking = ref(null)

function open(row) {
  if (settings.value.openDetail) {
    peeking.value = row
    return
  }
  router.push(recordRoute(entityKey.value, row.id))
}

// One table's remembered layout, told apart from every other table in the application. A saved view
// has a key; an inline block does not, so it is identified by what it is a list OF and where. Get
// this wrong and two tables share one set of hidden columns.
const settingsKey = computed(() =>
  props.view || `${props.record ? 'child' : 'block'}:${entityKey.value}:${definition.value?.label ?? ''}`)
</script>

<template>
  <component :is="depth === 0 ? 'v-card' : 'div'">
    <div class="d-flex align-center ga-2 flex-wrap px-4 py-3">
      <span v-if="titled" class="text-subtitle-1 font-weight-medium">{{ definition?.label }}</span>
      <!-- A bare "25" in a chip says nothing on its own. It only reads as a count when there is a
           title beside it to count; without one — this list is inside a `card` block that already
           named it, or the page heading says the same words — it says what it counts instead. -->
      <v-chip v-if="!loading && titled" size="x-small" variant="tonal">{{ total }}</v-chip>
      <span v-else-if="!loading" class="text-body-2 text-medium-emphasis">
        {{ total }} {{ (total === 1 ? entity?.label : entity?.labelPlural) || '' }}
      </span>
      <v-spacer />
      <v-btn
        v-if="create"
        size="small"
        variant="tonal"
        color="primary"
        prepend-icon="mdi-plus"
        @click="editing = { ...blank }"
      >
        New
      </v-btn>
    </div>

    <v-alert v-if="error" type="error" class="ma-4">{{ error }}</v-alert>
    <v-skeleton-loader v-else-if="loading" type="table" />

    <CordangoDataTable
      v-else-if="visible.length"
      :columns="columns"
      :rows="visible"
      :fields="entity?.fields || []"
      :ref-maps="labels"
      :ref-targets="refTargets"
      :entity-label="entity?.labelPlural || 'records'"
      :total="total"
      :handle="app.key"
      :settings-key="settingsKey"
      :filter-bar="settings.filterBar"
      :group-field="settings.groupBy?.field || null"
      :groups="groups"
      :ungrouped-label="settings.groupBy?.ungroupedLabel || '(No section)'"
      :show-empty-groups="settings.groupBy?.showEmpty === true"
      :order-field="settings.orderField"
      :can-add-group="Boolean(target)"
      :can-edit-groups="Boolean(target)"
      :inline-edit="settings.inlineEdit"
      :transitions-for="transitionsFor"
      :row-commands="commandsOnRow"
      :can-delete="settings.allowDelete"
      :can-add="create"
      @row-click="open"
      @edit="editing = $event"
      @delete="confirming = $event"
      @add="onAdd"
      @move="onMove"
      @add-group="onAddGroup"
      @rename-group="onRenameGroup"
      @delete-group="onDeleteGroup"
      @cell-edit="onCellEdit"
      @command="(command, record) => (pending = { command, record })"
    />

    <EmptyState v-else icon="mdi-table-off" title="Nothing here yet.">
      <v-btn v-if="create" variant="tonal" prepend-icon="mdi-plus" @click="editing = { ...blank }">
        Add the first one
      </v-btn>
    </EmptyState>

    <!-- A transition the table could not run itself, because running one may need a confirmation or
         an input form. `auto` fires the same flow the record page uses, so a move made from a cell
         and a move made from the process strip ask exactly the same questions. -->
    <CommandButton
      v-if="pending"
      auto
      :entity="entityKey"
      :record="pending.record"
      :command="pending.command"
      @done="pending = null; load(); recordsChanged(entityKey)"
      @cancelled="pending = null"
    />

    <RecordPeek
      v-if="peeking"
      :entity="entityKey"
      :record="peeking"
      :model-value="true"
      @update:model-value="peeking = null"
    />

    <RecordDialog
      v-if="editing"
      :entity="entityKey"
      :record="editing"
      @close="editing = null"
      @saved="editing = null; load(); recordsChanged(entityKey)"
    />

    <v-dialog :model-value="!!confirming" max-width="420" @update:model-value="confirming = null">
      <v-card>
        <v-card-title>Delete this record?</v-card-title>
        <v-card-text class="text-medium-emphasis">This cannot be undone.</v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="confirming = null">Cancel</v-btn>
          <v-btn color="error" variant="flat" @click="remove(confirming)">Delete</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
  </component>
</template>
