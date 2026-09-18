<script setup>
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import {
  viewOf, entityOf, loadView, displayOf, onRecordsChanged, recordsChanged, optionColor, recordRoute,
} from '../records.js'
import { session } from '../session.js'
import { iso, gridStart, gridDays, weekdayNames, monthLabel, shiftMonth, bucketByDay } from '../monthGrid.js'
import RecordDialog from './RecordDialog.vue'
import { useSurface } from './surface.js'

// A month grid over records that carry a date.
//
// It is a `view` whose `type` is 'calendar', which is the same saved query a table renders — same
// entity, same filters, same permissions. Only the arrangement differs, and that is the point: a
// calendar is a way of LOOKING at rows, not a second kind of data.
//
// The month is loaded as a range rather than a page: a calendar showing thirty days must show every
// record in those thirty days, and "the first hundred" would silently drop the rest of a busy month.

const props = defineProps({
  view: String,
  definition: { type: Object, default: null },
  state: { type: Object, default: null },
  // The record this calendar is inside, on a detail screen — what `{{record.id}}` resolves against.
  record: { type: Object, default: null },
  create: { type: Boolean, default: true },
})

const router = useRouter()

// A calendar inside a `card` block is already in a card, the same way a table and a stat are.
const depth = useSurface()

const definition = computed(() => props.definition ?? viewOf(props.view))
const entityKey = computed(() => definition.value?.entity)
const entity = computed(() => entityOf(entityKey.value))

const dateField = computed(() =>
  definition.value?.config?.dateField ?? definition.value?.startField
  ?? entity.value?.fields.find((f) => f.type === 'date' || f.type === 'datetime')?.key)

const endField = computed(() => definition.value?.config?.endField ?? definition.value?.endField ?? null)
const colorField = computed(() => definition.value?.config?.colorField ?? null)

const cursor = ref(new Date())
const rows = ref([])
const loading = ref(true)
const error = ref(null)
const editing = ref(null)

// The grid itself is monthGrid.js — shared with the personal calendar, so the two cannot disagree
// about which week a date falls in.
const from = computed(() => gridStart(cursor.value))
const days = computed(() => gridDays(cursor.value))
const label = computed(() => monthLabel(cursor.value))
const weekdays = weekdayNames()

async function load() {
  if (!definition.value || !dateField.value) {
    error.value = definition.value
      ? `'${definition.value.key}' is a calendar with no date field to place its records on.`
      : `No view named '${props.view}'.`
    loading.value = false
    return
  }
  loading.value = true
  error.value = null

  const last = new Date(from.value)
  last.setDate(last.getDate() + 41)

  try {
    const page = await loadView(
      definition.value,
      {
        personId: session.personId,
        userId: session.userId,
        state: props.state ?? {},
        record: props.record ?? {},
      },
      {
        take: 500,
        extraFilters: [
          { field: dateField.value, operator: 'gte', value: iso(from.value) },
          { field: dateField.value, operator: 'lte', value: iso(last) },
        ],
      })
    rows.value = page?.items ?? []
  } catch (failure) {
    error.value = failure.message
    rows.value = []
  } finally {
    loading.value = false
  }
}

const byDay = computed(() => bucketByDay(
  rows.value,
  (row) => row[dateField.value],
  endField.value ? (row) => row[endField.value] : null))

const colorOf = (row) => (colorField.value
  ? optionColor(entity.value?.fields.find((f) => f.key === colorField.value), row[colorField.value])
  : undefined)

const move = (by) => { cursor.value = shiftMonth(cursor.value, by) }

const open = (row) => router.push(recordRoute(entityKey.value, row.id))

const add = (key) => {
  if (props.create) editing.value = { [dateField.value]: key }
}

let stop
onMounted(() => {
  load()
  stop = onRecordsChanged(entityKey.value, load)
})
onUnmounted(() => stop?.())
watch(cursor, load)
watch(() => props.state, load, { deep: true })
</script>

<template>
  <component :is="depth === 0 ? 'v-card' : 'div'">
    <div class="d-flex align-center px-4 py-3">
      <span class="text-subtitle-1">{{ definition?.label }}</span>
      <v-spacer />
      <v-btn icon="mdi-chevron-left" size="small" variant="text" @click="move(-1)" />
      <span class="text-body-2 mx-2" style="min-width: 9rem; text-align: center">{{ label }}</span>
      <v-btn icon="mdi-chevron-right" size="small" variant="text" @click="move(1)" />
      <v-btn size="small" variant="text" class="ml-2" @click="cursor = new Date()">Today</v-btn>
    </div>

    <v-alert v-if="error" type="error" variant="tonal" class="ma-4">{{ error }}</v-alert>
    <v-skeleton-loader v-else-if="loading" type="image" />

    <v-card-text v-else>
      <div class="calendar-grid text-caption text-medium-emphasis mb-1">
        <div v-for="name in weekdays" :key="name" class="pa-1">{{ name }}</div>
      </div>

      <div class="calendar-grid">
        <v-sheet
          v-for="day in days"
          :key="day.key"
          class="pa-1 d-flex flex-column ga-1"
          :class="{ 'text-disabled': day.outside }"
          border
          rounded
          style="min-height: 6.5rem; cursor: pointer"
          @click="add(day.key)"
        >
          <div class="d-flex align-center">
            <v-chip v-if="day.today" size="x-small" color="primary" variant="flat">{{ day.day }}</v-chip>
            <span v-else class="text-caption">{{ day.day }}</span>
          </div>

          <v-chip
            v-for="row in (byDay[day.key] || [])"
            :key="row.id"
            size="x-small"
            variant="tonal"
            :color="colorOf(row)"
            class="text-truncate"
            @click.stop="open(row)"
          >
            {{ displayOf(entityKey, row) }}
          </v-chip>
        </v-sheet>
      </div>
    </v-card-text>

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
.calendar-grid {
  display: grid;
  grid-template-columns: repeat(7, minmax(0, 1fr));
  gap: 0.25rem;
}
</style>
