<script setup>
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import {
  viewOf, entityOf, loadView, displayOf, referenceOptions, onRecordsChanged, optionColor,
  formatValue, recordRoute,
} from '../records.js'
import { session } from '../session.js'
import EmptyState from './EmptyState.vue'
import { useSurface } from './surface.js'

// Records with a start and an end, laid along a date axis, one lane per whatever groups them.
//
// It is a `view` whose type is 'timeline' and it is also the `timeline` block; both arrive as a
// view definition, so the query is the table's and only the arrangement differs.
//
// THE READER OWNS THE WINDOW. The definition's `axis` says where to open and at what zoom, not what
// the reader is allowed to look at — a timeline that could only ever show the authored week would
// be a screenshot. Prev/next and the zoom buttons move the window, and the rows are re-fetched for
// it rather than filtered in the browser, because "the first hundred" over a year is a timeline
// that quietly loses its later half.

const props = defineProps({
  view: String,
  definition: { type: Object, default: null },
  state: { type: Object, default: null },
  record: { type: Object, default: null },
  hideTitle: { type: Boolean, default: false },
})

const router = useRouter()
const depth = useSurface()

const definition = computed(() => props.definition ?? viewOf(props.view))
const config = computed(() => definition.value?.config ?? {})
const entityKey = computed(() => definition.value?.entity)
const entity = computed(() => entityOf(entityKey.value))

const fieldOfKey = (key) => entity.value?.fields.find((f) => f.key === key) ?? null

// A saved timeline view spells these `groupBy`/`dateField`; the block spells them `rowBy`/
// `startField`. Both are read here rather than one being rewritten upstream — the same reason the
// board reads two spellings for its column field.
const laneKey = computed(() => config.value.rowBy ?? config.value.groupBy ?? null)
const startKey = computed(() =>
  config.value.startField ?? config.value.dateField
  ?? entity.value?.fields.find((f) => f.type === 'date' || f.type === 'datetime')?.key ?? null)
const endKey = computed(() => config.value.endField ?? null)
const colorKey = computed(() => config.value.colorField ?? null)
const labelKey = computed(() => config.value.labelField ?? null)

const RANGES = ['week', 'month', 'year']
const range = ref(RANGES.includes(config.value?.axis?.range) ? config.value.axis.range : 'month')
const cursor = ref(new Date())

const rows = ref([])
const loading = ref(true)
const error = ref(null)
const lanes = ref([])
// Where the records are, when they are not in the window being looked at.
const elsewhere = ref(null)

const iso = (at) => `${at.getFullYear()}-${String(at.getMonth() + 1).padStart(2, '0')}-${String(at.getDate()).padStart(2, '0')}`
const day = (text) => {
  const at = new Date(String(text ?? '').slice(0, 10))
  return Number.isNaN(at.getTime()) ? null : at
}

/** The window: where it starts, where it ends, and the ticks drawn across the top. */
const window_ = computed(() => {
  const at = cursor.value
  if (range.value === 'week') {
    const monday = new Date(at.getFullYear(), at.getMonth(), at.getDate() - ((at.getDay() + 6) % 7))
    const end = new Date(monday.getFullYear(), monday.getMonth(), monday.getDate() + 6)
    return { start: monday, end, ticks: 7, step: 'day' }
  }
  if (range.value === 'year') {
    return {
      start: new Date(at.getFullYear(), 0, 1),
      end: new Date(at.getFullYear(), 11, 31),
      ticks: 12,
      step: 'month',
    }
  }
  const first = new Date(at.getFullYear(), at.getMonth(), 1)
  const last = new Date(at.getFullYear(), at.getMonth() + 1, 0)
  return { start: first, end: last, ticks: last.getDate(), step: 'day' }
})

const ticks = computed(() => {
  const { start, ticks: count, step } = window_.value
  return Array.from({ length: count }, (_, i) => {
    if (step === 'month') {
      const at = new Date(start.getFullYear(), i, 1)
      return { key: `m${i}`, label: at.toLocaleDateString(undefined, { month: 'narrow' }) }
    }
    const at = new Date(start.getFullYear(), start.getMonth(), start.getDate() + i)
    return {
      key: iso(at),
      label: range.value === 'week'
        ? at.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric' })
        : String(at.getDate()),
      today: iso(at) === iso(new Date()),
    }
  })
})

const windowLabel = computed(() => {
  const { start, end } = window_.value
  if (range.value === 'year') return String(start.getFullYear())
  if (range.value === 'month') return start.toLocaleDateString(undefined, { month: 'long', year: 'numeric' })
  return `${start.toLocaleDateString(undefined, { day: 'numeric', month: 'short' })} – `
    + `${end.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })}`
})

const context = computed(() => ({
  personId: session.personId,
  userId: session.userId,
  state: props.state ?? {},
  record: props.record ?? {},
}))

async function resolveLanes() {
  const field = fieldOfKey(laneKey.value)
  if (field?.type === 'reference') {
    const options = await referenceOptions(
      field.targetApp === 'platform' || field.targetEntity === 'person' ? 'person' : field.targetEntity,
      field.targetApp === 'platform' || field.targetEntity === 'person' ? '/api/directory/person' : undefined)
    lanes.value = options.map((o) => ({ value: o.value, label: o.label }))
    return
  }
  if (field?.options?.length) {
    lanes.value = field.options.map((o) => ({ value: o.value, label: o.label ?? o.value }))
    return
  }
  lanes.value = []
}

async function load() {
  if (!definition.value) {
    error.value = `No view named '${props.view}'.`
    loading.value = false
    return
  }
  if (!startKey.value) {
    error.value = `'${definition.value.key}' is a timeline with no date field to place its records on.`
    loading.value = false
    return
  }
  loading.value = true
  error.value = null

  const { start, end } = window_.value
  try {
    // A bar that starts before the window and ends inside it belongs on screen, so the window is
    // matched against the END where there is one. Without that, every holiday that began last month
    // would vanish on the first of this one.
    const overlaps = endKey.value
      ? [
        { field: startKey.value, operator: 'lte', value: iso(end) },
        { field: endKey.value, operator: 'gte', value: iso(start) },
      ]
      : [
        { field: startKey.value, operator: 'gte', value: iso(start) },
        { field: startKey.value, operator: 'lte', value: iso(end) },
      ]

    const page = await loadView(definition.value, context.value, { take: 500, extraFilters: overlaps })
    rows.value = page?.items ?? []
    await resolveLanes()
    await findElsewhere()
  } catch (failure) {
    error.value = failure.message
    rows.value = []
  } finally {
    loading.value = false
  }
}

/**
 * When this window is empty, where the records actually are.
 *
 * <p>A timeline opens on today, and there is no reason a person's records should be near today — a
 * project that finished in spring, a plan that ends next year, or a demo dataset anchored to the day
 * it was generated. All of those open on an empty grid that says "nothing in this window", which
 * reads as "this surface is broken" rather than as "look somewhere else".</p>
 *
 * <p>Two extra requests, and only when the window came back empty: the earliest record and the
 * latest, through the view's OWN filters so it never points at a row this timeline excludes. The
 * nearer of the two is what the button jumps to.</p>
 */
async function findElsewhere() {
  elsewhere.value = null
  // Whether anything will actually be DRAWN, not whether the query returned anything: a row can
  // come back and still place no bar, and an empty grid is an empty grid either way.
  if (!startKey.value || rows.value.some((row) => span(row))) return

  const [first, last] = await Promise.all([
    loadView(definition.value, context.value, { take: 1, sort: startKey.value }),
    loadView(definition.value, context.value, { take: 1, sort: `-${startKey.value}` }),
  ])

  const bounds = [first?.items?.[0], last?.items?.[0]]
    .map((row) => day(row?.[startKey.value]))
    .filter(Boolean)
  if (!bounds.length) return

  const { start, end } = window_.value
  const nearest = bounds.reduce((best, at) => {
    const distance = at < start ? start - at : at - end
    return best === null || distance < best.distance ? { at, distance } : best
  }, null)

  elsewhere.value = nearest.at
}

/** Where a bar sits in the window, as two percentages. Clamped, so a bar that runs off either end
    is drawn to the edge rather than outside the lane. */
function span(row) {
  const { start, end } = window_.value
  const from = day(row[startKey.value])
  if (!from) return null
  const to = endKey.value ? (day(row[endKey.value]) ?? from) : from

  const total = (end - start) + 86400000
  const left = Math.max(0, (from - start) / total)
  const right = Math.min(1, ((to - start) + 86400000) / total)
  if (right <= 0 || left >= 1) return null

  const width = Math.max(right - left, 0.012)
  return {
    left: `${left * 100}%`,
    width: `${width * 100}%`,
    // A single-day marker on a year is about a pixel of real width and a floor of 1.2% to keep it
    // visible — nowhere near enough to hold a word. Below this the label goes BESIDE the bar rather
    // than inside it, which is what a gantt does and the only way a narrow bar says anything at all.
    narrow: width < 0.07,
  }
}

const sections = computed(() => {
  if (!laneKey.value) {
    return [{
      key: '__all',
      label: null,
      bars: rows.value.map((row) => ({ row, ...(span(row) ?? {}) })).filter((b) => b.left),
    }]
  }

  const known = new Map(lanes.value.map((l) => [String(l.value), { ...l, rows: [] }]))
  const extra = new Map()

  for (const row of rows.value) {
    const key = String(row[laneKey.value] ?? '')
    if (known.has(key)) known.get(key).rows.push(row)
    else {
      if (!extra.has(key)) extra.set(key, { value: key, label: key || '(None)', rows: [] })
      extra.get(key).rows.push(row)
    }
  }

  // A lane nobody has anything in is noise on a timeline, unlike a board column where the empty
  // one is the point. Only lanes with bars are drawn.
  // A bar that falls entirely outside the window has no place to be drawn, and a lane left with
  // none of them is an empty row of gridlines — so both are dropped here rather than rendered and
  // hidden.
  return [...known.values(), ...extra.values()]
    .map((lane) => ({
      key: String(lane.value),
      label: lane.label,
      bars: lane.rows.map((row) => ({ row, ...(span(row) ?? {}) })).filter((b) => b.left),
    }))
    .filter((lane) => lane.bars.length)
})

const barLabel = (row) => (labelKey.value
  ? formatValue(row[labelKey.value], fieldOfKey(labelKey.value))
  : displayOf(entityKey.value, row))

const barColor = (row) => (colorKey.value
  ? optionColor(fieldOfKey(colorKey.value), row[colorKey.value])
  : undefined)

function move(by) {
  const at = cursor.value
  if (range.value === 'week') cursor.value = new Date(at.getFullYear(), at.getMonth(), at.getDate() + (7 * by))
  else if (range.value === 'year') cursor.value = new Date(at.getFullYear() + by, at.getMonth(), 1)
  else cursor.value = new Date(at.getFullYear(), at.getMonth() + by, 1)
}

const open = (row) => router.push(recordRoute(entityKey.value, row.id))

const titled = computed(() => !props.hideTitle && Boolean(definition.value?.label))

let stop
onMounted(() => {
  load()
  stop = onRecordsChanged(entityKey.value, load)
})
onUnmounted(() => stop?.())
watch([cursor, range], load)
watch(() => props.state, load, { deep: true })
</script>

<template>
  <component :is="depth === 0 ? 'v-card' : 'div'">
    <div class="d-flex align-center ga-2 flex-wrap px-4 py-3">
      <span v-if="titled" class="text-subtitle-1 font-weight-medium">{{ definition?.label }}</span>
      <v-spacer />
      <v-btn-toggle v-model="range" density="compact" variant="outlined" mandatory>
        <v-btn v-for="option in RANGES" :key="option" :value="option" size="small">{{ option }}</v-btn>
      </v-btn-toggle>
      <v-btn icon="mdi-chevron-left" size="small" variant="text" @click="move(-1)" />
      <span class="text-body-2" style="min-width: 11rem; text-align: center">{{ windowLabel }}</span>
      <v-btn icon="mdi-chevron-right" size="small" variant="text" @click="move(1)" />
      <v-btn size="small" variant="text" @click="cursor = new Date()">Today</v-btn>
    </div>

    <v-alert v-if="error" type="error" variant="tonal" class="ma-4">{{ error }}</v-alert>
    <v-skeleton-loader v-else-if="loading" type="image" />

    <div v-else-if="sections.length" class="cd-timeline pa-4">
      <div class="cd-timeline-head">
        <div class="cd-timeline-rail" />
        <div class="cd-timeline-ticks">
          <div
            v-for="tick in ticks"
            :key="tick.key"
            class="cd-timeline-tick text-caption"
            :class="tick.today ? 'text-primary font-weight-bold' : 'text-medium-emphasis'"
          >
            {{ tick.label }}
          </div>
        </div>
      </div>

      <div v-for="section in sections" :key="section.key" class="cd-timeline-lane">
        <div class="cd-timeline-rail text-body-2 text-truncate pr-2">
          {{ section.label ?? '' }}
        </div>
        <div class="cd-timeline-track">
          <div
            v-for="tick in ticks"
            :key="tick.key"
            class="cd-timeline-gridline"
            :class="tick.today ? 'cd-timeline-gridline--today' : ''"
          />
          <div class="cd-timeline-bars">
            <div v-for="bar in section.bars" :key="bar.row.id" class="cd-timeline-row">
              <v-sheet
                class="cd-timeline-bar text-caption text-truncate px-2"
                rounded
                :color="barColor(bar.row) || 'primary'"
                :style="{ left: bar.left, width: bar.width }"
                @click="open(bar.row)"
              >
                <template v-if="!bar.narrow">{{ barLabel(bar.row) }}</template>
              </v-sheet>
              <span
                v-if="bar.narrow"
                class="cd-timeline-aside text-caption text-medium-emphasis"
                :style="{ left: `calc(${bar.left} + ${bar.width} + 6px)` }"
                @click="open(bar.row)"
              >
                {{ barLabel(bar.row) }}
              </span>
            </div>
          </div>
        </div>
      </div>
    </div>

    <EmptyState
      v-else
      icon="mdi-chart-timeline"
      :title="elsewhere ? 'Nothing in this window.' : 'Nothing to show.'"
    >
      <v-btn v-if="elsewhere" variant="tonal" prepend-icon="mdi-calendar-arrow-right" @click="cursor = elsewhere">
        Go to {{ elsewhere.toLocaleDateString(undefined, { month: 'long', year: 'numeric' }) }}
      </v-btn>
    </EmptyState>
  </component>
</template>

<style scoped>
/* A fixed rail on the left and a proportional track on the right. The bars are positioned as
   percentages of the track, so the whole thing reflows with the container and needs no measuring. */
.cd-timeline {
  overflow-x: auto;
}

.cd-timeline-head,
.cd-timeline-lane {
  display: flex;
  align-items: stretch;
  min-width: 34rem;
}

.cd-timeline-rail {
  flex: 0 0 9rem;
  max-width: 9rem;
  display: flex;
  align-items: center;
}

.cd-timeline-ticks {
  flex: 1 1 auto;
  display: flex;
}

.cd-timeline-tick {
  flex: 1 1 0;
  text-align: center;
  overflow: hidden;
  white-space: nowrap;
}

.cd-timeline-track {
  position: relative;
  flex: 1 1 auto;
  display: flex;
  min-height: 2.5rem;
  padding: 0.25rem 0;
  border-top: 1px solid rgba(var(--v-border-color), 0.12);
}

.cd-timeline-gridline {
  flex: 1 1 0;
  border-right: 1px solid rgba(var(--v-border-color), 0.08);
}

.cd-timeline-gridline--today {
  background: rgba(var(--v-theme-primary), 0.06);
}

.cd-timeline-bars {
  position: absolute;
  inset: 0.25rem 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

/* One line per bar, so two records overlapping in the same lane sit above each other instead of
   on top of each other. The line is the positioning context; the bar is placed along it. */
.cd-timeline-row {
  position: relative;
  height: 1.5rem;
}

/* The label of a bar too narrow to contain it. Not clipped by the track, so a marker near the right
   edge still reads — it is the last thing on the row either way. */
.cd-timeline-aside {
  position: absolute;
  top: 0;
  line-height: 1.5rem;
  white-space: nowrap;
  cursor: pointer;
  pointer-events: auto;
}

.cd-timeline-bar {
  position: absolute;
  top: 0;
  height: 1.5rem;
  line-height: 1.5rem;
  min-width: 0.5rem;
  cursor: pointer;
}
</style>
