<script setup>
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import {
  viewOf, entityOf, loadView, commandsOf, deleteRecord, referenceOptions, processOf,
  onRecordsChanged, recordsChanged, matchesSearch, formatValue, resolveValue, optionLabel, recordRoute,
} from '../records.js'
import { session } from '../session.js'
import RecordDialog from './RecordDialog.vue'
import CommandButton from './CommandButton.vue'
import EmptyState from './EmptyState.vue'
import FilterBar from './FilterBar.vue'
import InlineCell from './InlineCell.vue'
import { useSurface } from './surface.js'

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
  // What a new record starts with, beyond what this list's own equality filters already imply.
  newDefaults: { type: Object, default: null },
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
    inlineEdit: props.inlineEdit || config.inlineEdit === true,
    allowDelete: props.allowDelete || config.allowDelete === true,
  }
})

const rows = ref([])
const total = ref(0)
const loading = ref(true)
const error = ref(null)
const editing = ref(null)
const confirming = ref(null)

// The toolbar this list owns, if it has one. Its own state object rather than the page's: a table
// filter bar narrows THIS table, and two tables on a page must not share one search box.
const own = ref({ q: '' })
const ownFacets = computed(() =>
  (settings.value.filterBar?.facets || []).map((field) => ({ state: `facet_${field}`, field })))

const columns = computed(() => {
  const keys = definition.value?.config?.columns
    ?? definition.value?.columns
    // No columns named means every field somebody could have entered. Not every field: the audit
    // stamps would push the ones people care about off the right of the screen.
    ?? entity.value?.fields.filter((f) => !f.system).slice(0, 6).map((f) => f.key)
    ?? []
  return keys.map((key) => entity.value?.fields.find((f) => f.key === key)).filter(Boolean)
})

const rowCommands = computed(() =>
  commandsOf(entityKey.value).filter((c) => (c.placements || []).includes('tableRow')))

// A field the server would refuse anyway is not one to offer a control for. A process-governed
// status is refused for a different reason: its legal moves are the process's, not the field's, so
// a plain dropdown over its options would offer transitions the lifecycle forbids.
const processState = computed(() => processOf(entityKey.value)?.stateField)

const editableIn = (field, index) =>
  settings.value.inlineEdit && index > 0 && !field.system && !field.readOnly && !field.computed
  && field.key !== processState.value

// Reference columns arrive as ids. Resolving them here rather than per cell means the table asks
// once for the whole page instead of once per row, and it is the same map the search reads so a
// query matches the name a person can actually see.
const labels = ref({})

async function resolveLabels() {
  const grouping = settings.value.groupBy?.field
    ? entity.value?.fields.filter((f) => f.key === settings.value.groupBy.field) ?? []
    : []
  const references = [...columns.value, ...grouping].filter((f) => f.type === 'reference'
    && f.targetApp !== 'platform' && f.targetEntity && f.targetEntity !== 'person')
  const resolved = {}
  for (const field of references) {
    const options = await referenceOptions(field.targetEntity)
    resolved[field.key] = Object.fromEntries(options.map((o) => [o.value, o.label]))
  }
  labels.value = resolved
}

const labelFor = (field, value) => labels.value[field.key]?.[value] ?? formatValue(value, field)

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
  } catch (failure) {
    error.value = failure.message
    rows.value = []
  } finally {
    loading.value = false
  }
}

// Narrowed after loading, not before: a free-text query has to match what somebody SEES, and a
// facet on this table's own toolbar is client-side by definition. A page-level facet is a filter
// leaf instead, so it goes to the server and reloads through `load`.
const visible = computed(() => {
  let result = rows.value

  if (props.search && props.state) {
    result = result.filter((r) =>
      matchesSearch(r, props.state[props.search.state], entityKey.value, props.search.fields, labelFor))
  }
  if (settings.value.filterBar?.search) {
    result = result.filter((r) => matchesSearch(r, own.value.q, entityKey.value, settings.value.filterBar.search, labelFor))
  }
  for (const facet of ownFacets.value) {
    const wanted = own.value[facet.state]
    if (wanted !== undefined && wanted !== null && wanted !== '') {
      result = result.filter((r) => String(r[facet.field] ?? '') === String(wanted))
    }
  }
  return result
})

// The rows as the table renders them: one section per distinct value of the grouping field, in
// option order for a select and in load order for a reference. A row whose group is empty falls into
// a section of its own at the end rather than disappearing.
const sections = computed(() => {
  if (!settings.value.groupBy?.field) return [{ key: '__all', label: null, rows: visible.value }]

  const key = settings.value.groupBy.field
  const field = entity.value?.fields.find((f) => f.key === key)
  const order = field?.options?.map((o) => o.value) ?? []
  const buckets = new Map()

  for (const row of visible.value) {
    const value = row[key] ?? ''
    if (!buckets.has(value)) buckets.set(value, [])
    buckets.get(value).push(row)
  }

  if (settings.value.groupBy.showEmpty) for (const value of order) if (!buckets.has(value)) buckets.set(value, [])

  const rank = (value) => {
    if (value === '') return Number.MAX_SAFE_INTEGER
    const at = order.indexOf(value)
    return at === -1 ? Number.MAX_SAFE_INTEGER - 1 : at
  }

  return [...buckets.entries()]
    .sort((a, b) => rank(a[0]) - rank(b[0]))
    .map(([value, rows]) => ({
      key: String(value),
      label: value === ''
        ? (settings.value.groupBy.ungroupedLabel || '(No section)')
        : (labels.value[key]?.[value] ?? (field?.options ? optionLabel(field, value) : String(value))),
      rows,
    }))
})

async function remove(row) {
  await deleteRecord(entityKey.value, row.id)
  confirming.value = null
  load()
  recordsChanged(entityKey.value)
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

const open = (row) => router.push(recordRoute(entityKey.value, row.id))
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

    <div v-if="settings.filterBar" class="px-4 pb-2">
      <FilterBar
        :entity="entityKey"
        :state="own"
        :search="settings.filterBar.search ? { state: 'q', placeholder: 'Search' } : null"
        :facets="ownFacets"
      />
    </div>

    <v-alert v-if="error" type="error" class="ma-4">{{ error }}</v-alert>
    <v-skeleton-loader v-else-if="loading" type="table" />

    <div v-else-if="visible.length" class="cd-table-scroll">
      <v-table hover>
        <thead>
          <tr>
            <th v-for="field in columns" :key="field.key">{{ field.label }}</th>
            <th v-if="rowCommands.length || settings.allowDelete" class="cd-row-actions" />
          </tr>
        </thead>
        <tbody v-for="section in sections" :key="section.key">
          <tr v-if="section.label" class="cd-group">
            <td
              :colspan="columns.length + (rowCommands.length || settings.allowDelete ? 1 : 0)"
              class="text-caption text-medium-emphasis font-weight-medium"
            >
              {{ section.label }}
              <v-chip size="x-small" variant="tonal" class="ml-2">{{ section.rows.length }}</v-chip>
            </td>
          </tr>
          <tr v-for="row in section.rows" :key="row.id" style="cursor: pointer" @click="open(row)">
            <td
              v-for="(field, index) in columns"
              :key="field.key"
              :style="editableIn(field, index) ? 'cursor: auto' : ''"
              @click="editableIn(field, index) ? $event.stopPropagation() : null"
            >
              <InlineCell
                :entity="entityKey"
                :field="field"
                :record="row"
                :editable="editableIn(field, index)"
                :labels="labels[field.key] || null"
                @saved="load(); recordsChanged(entityKey)"
                @failed="error = $event"
              />
            </td>

            <!--
              One button, not one per command.

              This rendered every `tableRow` command as its own button in the last cell. Six
              commands on a task — add to my day, due today, due tomorrow, mark important, no longer
              important, remove from my day — produced eighteen centimetres of grey buttons wrapping
              onto two lines on every row, which is not a table any more. The commands are the same
              and the guard is the same; they are simply behind the control that means "there is
              more here".
            -->
            <td v-if="rowCommands.length || settings.allowDelete" class="cd-row-actions text-right" @click.stop>
              <div class="cd-hover-actions d-inline-flex align-center">
                <v-menu v-if="rowCommands.length" location="bottom end">
                  <template #activator="{ props }">
                    <v-btn
                      icon="mdi-dots-horizontal"
                      size="small"
                      v-bind="props"
                      :aria-label="'More actions'"
                    />
                  </template>
                  <v-list>
                    <CommandButton
                      v-for="command in rowCommands"
                      :key="command.key"
                      as="item"
                      :entity="entityKey"
                      :record="row"
                      :command="command"
                      @done="load"
                    />
                  </v-list>
                </v-menu>

                <v-btn
                  v-if="settings.allowDelete"
                  icon="mdi-delete-outline"
                  size="small"
                  @click="confirming = row"
                />
              </div>
            </td>
          </tr>
        </tbody>
      </v-table>
    </div>

    <EmptyState v-else icon="mdi-table-off" title="Nothing here yet.">
      <v-btn v-if="create" variant="tonal" prepend-icon="mdi-plus" @click="editing = { ...blank }">
        Add the first one
      </v-btn>
    </EmptyState>

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
