<script setup>
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import {
  loadRecords, onRecordsChanged, sortTerm, resolveValue, recordRoute,
  createRecord, updateRecord, deleteRecord, displayOf, toast,
} from '../records.js'
import { session } from '../session.js'
import EmptyState from './EmptyState.vue'
import RecordPeek from './RecordPeek.vue'
import { useSurface } from './surface.js'

// A child list read as a CHECKLIST: one tick per row, and a line at the bottom to add the next one.
//
// <p><b>Why this is not the table with a checkbox column.</b> The rows are the same records either
// way, and that is exactly what made a table the wrong answer for long enough to be worth writing
// down. A checklist is a different question. Somebody reading a project's milestones wants to know
// what is left, and to mark one done as they pass — two gestures, no dialog. The table asked them to
// open a record to tick a box, showed six columns of which one mattered, and offered "New" as a
// form. The rows were right and the job was not, which is the one kind of wrong a screenshot does
// not show.</p>
//
// <p><b>The two fields are resolved by the GENERATOR, not here.</b> `doneField` is the boolean the
// tick writes and `titleField` is the text the line reads. The platform derives both in the browser
// from entity metadata; this asks for them as props, the way AnswersBlock does, because the build
// already knows the entity and a fact worked out twice is a fact that drifts. It also means an
// entity with nothing to tick is a CORD2308 at build time rather than a column of dead checkboxes.
// </p>
const props = defineProps({
  // The same query shape ViewBlock takes — entity, filters (with `via` already expanded into a
  // `{{record.id}}` leaf), sort, limit, label. One shape for every list, whatever draws it.
  definition: { type: Object, default: null },
  // The screen's state, so a `{{state.x}}` in a filter resolves and the list reloads when it moves.
  state: { type: Object, default: null },
  // The record this list is inside. `{{record.id}}` resolves against it.
  record: { type: Object, default: null },

  // The boolean the tick writes. Absent means the ticks are read-only — the generator reports that
  // rather than emitting it, so in practice this is always set.
  doneField: { type: String, default: null },
  // The text each line reads, and the one field the add-line writes.
  titleField: { type: String, default: null },

  create: { type: Boolean, default: true },
  allowDelete: { type: Boolean, default: false },
  // Click a line to glance at the record rather than navigate to it. Same flag, same meaning, same
  // panel as every other list.
  openDetail: { type: Boolean, default: false },
  hideTitle: { type: Boolean, default: false },
})

const depth = useSurface()
const router = useRouter()

const rows = ref([])
const loading = ref(true)
const error = ref(null)
const draft = ref('')
const adding = ref(false)
const peeking = ref(null)

const entityKey = computed(() => props.definition?.entity ?? null)
const titled = computed(() => !props.hideTitle && depth === 0 && Boolean(props.definition?.label))

const context = computed(() => ({
  personId: session.personId,
  userId: session.userId,
  state: props.state ?? {},
  record: props.record ?? {},
}))

// What a new item starts as. Same rule as ViewBlock's: the list's own EQUALITY filters are what a
// new row has to satisfy to still be here after the next load, and the `via` leaf is one of them —
// which is how adding a milestone from a project's checklist lands on THAT project with nothing
// naming it. A comparison says which rows to show, not what a new one should be, so only `eq`
// counts.
const blank = computed(() => {
  const seed = {}
  for (const filter of props.definition?.filters || []) {
    if (filter.operator === 'eq' && filter.field && !filter.optional) {
      seed[filter.field] = resolveValue(filter.value, context.value)
    }
  }
  return seed
})

const labelOf = (row) =>
  (props.titleField ? row[props.titleField] : null) || displayOf(entityKey.value, row)

const isDone = (row) => Boolean(props.doneField && row[props.doneField])

async function load() {
  if (!entityKey.value) {
    rows.value = []
    loading.value = false
    return
  }

  loading.value = true
  error.value = null
  try {
    const page = await loadRecords(entityKey.value, {
      filters: props.definition?.filters || [],
      sort: sortTerm(props.definition?.sort),
      take: props.definition?.limit ?? 100,
      context: context.value,
    })
    rows.value = page?.items ?? []
  } catch (failure) {
    error.value = failure.message
    rows.value = []
  } finally {
    loading.value = false
  }
}

// The tick is written OPTIMISTICALLY and put back if the write refuses. A checkbox that waits for a
// round trip before it moves reads as a checkbox that did not take, and somebody clicks it twice.
async function toggle(row) {
  if (!props.doneField) return

  const was = isDone(row)
  row[props.doneField] = !was
  try {
    await updateRecord(entityKey.value, row.id, { [props.doneField]: !was })
  } catch (failure) {
    row[props.doneField] = was
    toast(failure.message, 'error')
  }
}

async function add() {
  const text = draft.value.trim()
  if (!text || !props.titleField || adding.value) return

  adding.value = true
  try {
    await createRecord(entityKey.value, { ...blank.value, [props.titleField]: text })
    draft.value = ''
    await load()
  } catch (failure) {
    toast(failure.message, 'error')
  } finally {
    adding.value = false
  }
}

async function remove(row) {
  try {
    await deleteRecord(entityKey.value, row.id)
    await load()
  } catch (failure) {
    toast(failure.message, 'error')
  }
}

function open(row) {
  if (props.openDetail) {
    peeking.value = row
    return
  }
  router.push(recordRoute(entityKey.value, row.id))
}

let stop
onMounted(() => {
  load()
  stop = onRecordsChanged(entityKey.value, load)
})
onUnmounted(() => stop?.())
watch(() => props.definition, load, { deep: true })
watch(() => props.state, load, { deep: true })
</script>

<template>
  <component :is="depth === 0 ? 'v-card' : 'div'">
    <v-card-title v-if="titled" class="text-subtitle-1">{{ definition.label }}</v-card-title>

    <v-alert v-if="error" type="error" class="ma-2">{{ error }}</v-alert>
    <v-skeleton-loader v-else-if="loading" type="list-item" />

    <template v-else>
      <EmptyState v-if="rows.length === 0 && !create" :title="$t('checklist.empty')" />

      <div v-for="row in rows" :key="row.id" class="cd-check-row">
        <v-checkbox-btn
          :model-value="isDone(row)"
          :disabled="!doneField"
          density="compact"
          hide-details
          @update:model-value="toggle(row)"
        />
        <!-- A span with a role rather than a <button>: the house rule keeps raw form elements out
             of the tree, and this is a line of text that happens to open a record. The role and the
             tabindex are what keep it reachable from the keyboard, the same pair RepeatBlock uses
             for a clickable card. -->
        <span
          class="cd-check-label"
          :class="{ 'cd-done': isDone(row) }"
          role="link"
          tabindex="0"
          @click="open(row)"
          @keydown.enter="open(row)"
        >{{ labelOf(row) }}</span>
        <v-spacer />
        <v-btn
          v-if="allowDelete"
          icon="mdi-close"
          size="x-small"
          variant="text"
          class="cd-check-remove"
          :aria-label="$t('common.delete')"
          @click="remove(row)"
        />
      </div>

      <!-- The add line is a line, not a form. One field, Enter commits, and the rest of the record
           is what `blank` already knows. A checklist whose only way to add was a dialog would be a
           table wearing ticks. -->
      <div v-if="create && titleField" class="cd-check-add">
        <v-icon icon="mdi-plus" size="18" class="mr-2 text-medium-emphasis" />
        <v-text-field
          v-model="draft"
          variant="plain"
          density="compact"
          hide-details
          single-line
          :disabled="adding"
          :placeholder="$t('checklist.add')"
          @keyup.enter="add"
        />
      </div>
    </template>

    <RecordPeek
      v-if="peeking"
      :entity="entityKey"
      :record="peeking"
      :model-value="true"
      @update:model-value="peeking = null"
    />
  </component>
</template>

<style scoped>
.cd-check-row {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 2px 12px 2px 4px;
  border-bottom: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
}
.cd-check-row:last-of-type { border-bottom: none; }

.cd-check-label { padding: 4px 0; cursor: pointer; }
.cd-check-label:hover { text-decoration: underline; }
.cd-check-label:focus-visible { outline: 2px solid rgb(var(--v-theme-primary)); outline-offset: 2px; }

.cd-done { text-decoration: line-through; opacity: 0.6; }
.cd-done:hover { text-decoration: line-through underline; }

.cd-check-remove { opacity: 0; transition: opacity 120ms ease; }
.cd-check-row:hover .cd-check-remove, .cd-check-remove:focus-visible { opacity: 1; }

.cd-check-add { display: flex; align-items: center; padding: 2px 12px 6px 8px; }
</style>
