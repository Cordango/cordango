<script setup>
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import {
  viewOf, entityOf, loadView, displayOf, referenceOptions, processOf, updateRecord, commandsOf,
  onRecordsChanged, recordsChanged, matchesSearch, formatStat, optionColor, optionLabel, toast, recordRoute,
} from '../records.js'
import { session } from '../session.js'
import CommandButton from './CommandButton.vue'
import RecordDialog from './RecordDialog.vue'
import EmptyState from './EmptyState.vue'
import FieldValue from './FieldValue.vue'
import { useSurface } from './surface.js'

// A board: the same rows a table would show, stacked into columns by one field.
//
// It is a `view` whose type is 'kanban' and it is also the `board` block, and those are the same
// surface asked for in two places. Both arrive here as a view definition — the shape a saved view
// has — so the query, the filters and the permissions are the table's and only the arrangement
// differs. There is no second path on which a filter could be dropped.
//
// WHAT A COLUMN IS depends on the field, and the difference matters. A process-governed status has
// its columns fixed by the process — every state, in the process's own order, whether or not
// anything is in it, because an empty column is the useful half of a pipeline. A plain select has
// its options. A reference has its rows. Anything else falls back to the values actually present,
// which is the only honest answer for a free-text field.

const props = defineProps({
  view: String,
  definition: { type: Object, default: null },
  state: { type: Object, default: null },
  // Bound to a page-level filterbar's search box: `{ state, fields }`.
  search: { type: Object, default: null },
  // The record this board is inside, on a detail screen — what `{{record.id}}` resolves against.
  record: { type: Object, default: null },
  create: { type: Boolean, default: false },
  hideTitle: { type: Boolean, default: false },
})

const router = useRouter()
const depth = useSurface()

const definition = computed(() => props.definition ?? viewOf(props.view))
const config = computed(() => definition.value?.config ?? {})
const entityKey = computed(() => definition.value?.entity)
const entity = computed(() => entityOf(entityKey.value))

// `groupByField` is how a saved kanban view spells it and `groupField` is how the block does. Both
// are read rather than one being translated into the other somewhere upstream, because a component
// that understands only one spelling fails silently on the other — which is exactly how a whole
// class of these blocks came to render nothing at all.
const groupKey = computed(() => config.value.groupByField ?? config.value.groupField ?? null)
const groupField = computed(() => entity.value?.fields.find((f) => f.key === groupKey.value) ?? null)
const cardFields = computed(() => {
  const keys = config.value.cardFields
    // No card fields named: the first couple of plain ones, which is enough to tell two cards apart
    // without turning a card into a form.
    ?? entity.value?.fields.filter((f) => !f.system && f.key !== groupKey.value).slice(0, 3).map((f) => f.key)
    ?? []
  return keys.map((key) => entity.value?.fields.find((f) => f.key === key)).filter(Boolean)
})

const sumField = computed(() =>
  entity.value?.fields.find((f) => f.key === config.value.sumField) ?? null)

const process = computed(() => {
  const found = processOf(entityKey.value)
  return found && found.stateField === groupKey.value ? found : null
})

// Read-only when the definition says so, and read-only when the reader cannot write anyway. A card
// that lifts under the cursor and then refuses to land is worse than one that never lifted.
const movable = computed(() =>
  config.value.interaction !== 'visualization' && !groupField.value?.readOnly && !groupField.value?.computed)

const rows = ref([])
const loading = ref(true)
const error = ref(null)
const editing = ref(null)
const dragging = ref(null)
const over = ref(null)
// A transition that wants a confirmation or an input field before it runs.
const pending = ref(null)

const references = ref([])

async function resolveColumns() {
  if (groupField.value?.type !== 'reference') { references.value = []; return }
  references.value = await referenceOptions(groupField.value.targetEntity)
}

const visible = computed(() => {
  if (!props.search || !props.state) return rows.value
  return rows.value.filter((r) =>
    matchesSearch(r, props.state[props.search.state], entityKey.value, props.search.fields))
})

const columns = computed(() => {
  if (process.value) {
    return process.value.states.map((s) => ({ value: s.key, label: s.label ?? s.key, color: s.color }))
  }
  if (groupField.value?.options?.length) {
    return groupField.value.options.map((o) => ({
      value: o.value, label: o.label ?? o.value, color: o.color,
    }))
  }
  if (references.value.length) {
    return references.value.map((o) => ({ value: o.value, label: o.label, color: undefined }))
  }
  // Whatever is actually there. Sorted so the columns do not reshuffle every time the board
  // reloads, which would make a board impossible to read at a glance.
  const seen = [...new Set(visible.value.map((r) => r[groupKey.value]).filter((v) => v !== null && v !== undefined && v !== ''))]
  return seen.sort().map((value) => ({ value, label: String(value), color: undefined }))
})

// One pass over the rows rather than a filter per column: a board with eight columns would
// otherwise walk the whole dataset eight times on every keystroke in the search box.
const byColumn = computed(() => {
  const buckets = new Map(columns.value.map((c) => [String(c.value), []]))
  const loose = []
  for (const row of visible.value) {
    const key = String(row[groupKey.value] ?? '')
    if (buckets.has(key)) buckets.get(key).push(row)
    else loose.push(row)
  }
  return { buckets, loose }
})

// A record whose group value matches no column still exists and still has to be reachable. A
// board that silently dropped it would be a list that loses rows.
const unplaced = computed(() => byColumn.value.loose)

const cardsIn = (value) => byColumn.value.buckets.get(String(value)) ?? []

const totalIn = (value) => {
  if (!sumField.value) return null
  return cardsIn(value).reduce((sum, row) => sum + (Number(row[sumField.value.key]) || 0), 0)
}

const context = computed(() => ({
  personId: session.personId,
  userId: session.userId,
  state: props.state ?? {},
  record: props.record ?? {},
}))

async function load() {
  if (!definition.value) {
    error.value = `No view named '${props.view}'.`
    loading.value = false
    return
  }
  if (!groupKey.value) {
    error.value = `'${definition.value.key}' is a board with no field to make columns from.`
    loading.value = false
    return
  }
  loading.value = true
  error.value = null
  try {
    // A board shows every card in its columns, so it loads a window rather than a page: "the first
    // hundred" would leave the rightmost column looking empty on a busy month.
    const page = await loadView(definition.value, context.value, { take: 400 })
    rows.value = page?.items ?? []
    await resolveColumns()
  } catch (failure) {
    error.value = failure.message
    rows.value = []
  } finally {
    loading.value = false
  }
}

/**
 * Move a card to another column.
 *
 * <p>Three different things wear the same gesture, and telling them apart is the whole job.</p>
 *
 * <p>On a plain field a drop is an edit and goes out as a patch. On a process-governed one it is a
 * TRANSITION, and the process decides which of those exist — so the move is matched against the
 * declared ones and refused by name when there is none. Dragging "Archived" back to "Draft" is not a
 * failed save; it is a move the lifecycle does not have, and saying so is more use than a server
 * error.</p>
 *
 * <p>A transition with a COMMAND runs that command, because the command is where the guard, the
 * effects and any confirmation live — and where it asks for input, the same dialog every other
 * command uses does the asking. A FREE transition has no command and is an ordinary write of the
 * state field: the server's process guard allows a legal move and refuses an illegal one, so the
 * plain patch is both correct and the same path the inline status cell takes.</p>
 */
async function drop(column) {
  const row = dragging.value
  dragging.value = null
  over.value = null
  if (!row || !movable.value) return

  const from = row[groupKey.value] ?? null
  if (String(from ?? '') === String(column.value)) return

  try {
    if (process.value) {
      const transition = (process.value.transitions ?? []).find((t) =>
        String(t.to) === String(column.value) && (t.from ?? []).some((f) => String(f) === String(from ?? '')))

      if (!transition) {
        const to = columns.value.find((c) => String(c.value) === String(column.value))
        toast(`'${process.value.key}' has no move from '${optionLabel(groupField.value, from) || from || '—'}' `
          + `to '${to?.label ?? column.value}'.`, 'warning')
        return
      }

      if (transition.command) {
        const command = commandsOf(entityKey.value).find((c) => c.key === transition.command)

        if (!command) {
          // The transition names a command this application does not have. Writing the state field
          // anyway would perform the move without the effects the command carries, which is worse
          // than not moving: the record would look transitioned and nothing else would have
          // happened.
          toast(`'${transition.label ?? transition.key}' runs '${transition.command}', `
            + 'which this application does not have.', 'error')
          return
        }

        // Handed to CommandButton rather than run here: it already knows how to confirm, how to
        // collect input fields and how to report what came back.
        pending.value = { row, command }
        return
      }
    }

    await updateRecord(entityKey.value, row.id, { [groupKey.value]: column.value })
    await load()
    recordsChanged(entityKey.value)
  } catch (failure) {
    toast(failure.message, 'error')
    await load()
  }
}

const open = (row) => router.push(recordRoute(entityKey.value, row.id))

const colorOf = (column) => column.color
  ?? (groupField.value ? optionColor(groupField.value, column.value) : undefined)

const titled = computed(() => !props.hideTitle && Boolean(definition.value?.label))

let stop
onMounted(() => {
  load()
  stop = onRecordsChanged(entityKey.value, load)
})
onUnmounted(() => stop?.())
watch(() => props.state, load, { deep: true })
</script>

<template>
  <component :is="depth === 0 ? 'v-card' : 'div'">
    <div v-if="titled || create" class="d-flex align-center ga-2 flex-wrap px-4 py-3">
      <span v-if="titled" class="text-subtitle-1 font-weight-medium">{{ definition?.label }}</span>
      <v-chip v-if="!loading && titled" size="x-small" variant="tonal">{{ visible.length }}</v-chip>
      <v-spacer />
      <v-btn
        v-if="create"
        size="small"
        variant="tonal"
        color="primary"
        prepend-icon="mdi-plus"
        @click="editing = {}"
      >
        New
      </v-btn>
    </div>

    <v-alert v-if="error" type="error" variant="tonal" class="ma-4">{{ error }}</v-alert>
    <v-skeleton-loader v-else-if="loading" type="image" />

    <div v-else-if="visible.length" class="cd-board pa-4">
      <div
        v-for="column in columns"
        :key="String(column.value)"
        class="cd-board-column"
        :class="over === String(column.value) ? 'cd-board-column--over' : ''"
        @dragover.prevent="movable ? (over = String(column.value)) : null"
        @dragleave="over === String(column.value) ? (over = null) : null"
        @drop.prevent="drop(column)"
      >
        <div class="d-flex align-center ga-2 px-1 pb-2">
          <v-icon v-if="colorOf(column)" icon="mdi-circle" size="10" :color="colorOf(column)" />
          <span class="text-caption font-weight-medium text-truncate">{{ column.label }}</span>
          <v-spacer />
          <span v-if="sumField" class="text-caption text-medium-emphasis">
            {{ formatStat(totalIn(column.value), sumField.type === 'money' ? 'money' : 'number',
                          { currency: sumField.currency }) }}
            ·
          </span>
          <span class="text-caption text-medium-emphasis">{{ cardsIn(column.value).length }}</span>
        </div>

        <div class="d-flex flex-column ga-2">
          <v-sheet
            v-for="row in cardsIn(column.value)"
            :key="row.id"
            class="pa-3 cd-board-card"
            border
            rounded
            :draggable="movable"
            @dragstart="dragging = row"
            @dragend="dragging = null; over = null"
            @click="open(row)"
          >
            <div class="text-body-2 font-weight-medium mb-1">{{ displayOf(entityKey, row) }}</div>
            <div
              v-for="field in cardFields"
              :key="field.key"
              class="d-flex align-center ga-2 text-caption"
            >
              <span class="text-medium-emphasis text-truncate">{{ field.label }}</span>
              <v-spacer />
              <FieldValue :field="field" :value="row[field.key]" />
            </div>
          </v-sheet>

          <!-- A column with nothing in it still has to be a drop target, and a zero-height one is
               not something anybody can aim at. -->
          <div v-if="!cardsIn(column.value).length" class="cd-board-empty text-caption text-disabled">
            Nothing here
          </div>
        </div>
      </div>

      <div v-if="unplaced.length" class="cd-board-column">
        <div class="d-flex align-center ga-2 px-1 pb-2">
          <span class="text-caption font-weight-medium">(No {{ groupField?.label || 'column' }})</span>
          <v-spacer />
          <span class="text-caption text-medium-emphasis">{{ unplaced.length }}</span>
        </div>
        <div class="d-flex flex-column ga-2">
          <v-sheet
            v-for="row in unplaced"
            :key="row.id"
            class="pa-3 cd-board-card"
            border
            rounded
            :draggable="movable"
            @dragstart="dragging = row"
            @dragend="dragging = null; over = null"
            @click="open(row)"
          >
            <div class="text-body-2 font-weight-medium">{{ displayOf(entityKey, row) }}</div>
          </v-sheet>
        </div>
      </div>
    </div>

    <EmptyState v-else icon="mdi-view-column-outline" title="Nothing on the board yet." />

    <CommandButton
      v-if="pending"
      auto
      :entity="entityKey"
      :record="pending.row"
      :command="pending.command"
      @done="pending = null; load(); recordsChanged(entityKey)"
      @cancelled="pending = null"
    />

    <RecordDialog
      v-if="editing"
      :entity="entityKey"
      :record="editing"
      @close="editing = null"
      @saved="editing = null; load(); recordsChanged(entityKey)"
    />
  </component>
</template>

<style scoped>
/* Columns scroll sideways rather than shrinking. Eight statuses squeezed into one screen width
   gives eight columns too narrow to read a title in, and the fix people reach for — wrapping — puts
   the last column under the first, which is not a board any more. */
.cd-board {
  display: flex;
  gap: 0.75rem;
  overflow-x: auto;
  align-items: flex-start;
}

.cd-board-column {
  flex: 0 0 17rem;
  max-width: 17rem;
  border-radius: 8px;
  padding: 0.5rem;
  background: rgba(var(--v-border-color), 0.04);
  transition: background-color 120ms ease;
}

.cd-board-column--over {
  background: rgba(var(--v-theme-primary), 0.1);
}

.cd-board-card {
  cursor: pointer;
}

.cd-board-card[draggable='true'] {
  cursor: grab;
}

.cd-board-card[draggable='true']:active {
  cursor: grabbing;
}

.cd-board-empty {
  padding: 0.75rem;
  text-align: center;
  border: 1px dashed rgba(var(--v-border-color), 0.25);
  border-radius: 8px;
}
</style>
