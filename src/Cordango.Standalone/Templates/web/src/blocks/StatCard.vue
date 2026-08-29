<script setup>
import { ref, computed, watch, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { loadAggregate, formatStat } from '../records.js'
import { session } from '../session.js'
import { useSurface } from './surface.js'

// One figure, and what it means.
//
// A stat has TWO origins and they are not variations of each other. `source` is an aggregate over a
// collection — "42 open tickets" — and costs a request. `field` is a value the bound record already
// carries — "this project: 12 open" — and costs nothing, because the row is already here.
//
// Only `source` was implemented. A definition that said `field` produced a card with no source at
// all, and since `source` was a REQUIRED prop the component then asked the server for an aggregate
// over `undefined`, failed, and rendered "unavailable" — on every per-record stat in the
// application. Neither origin is required now, and a stat with neither says so rather than
// pretending the server is down.

const props = defineProps({
  label: String,
  icon: String,
  format: String,
  // 'xs' | 'sm' | 'md' | 'lg' | 'xl' — how loud the figure is, not how big the card is.
  size: { type: String, default: 'md' },
  weight: { type: String, default: 'bold' },
  color: { type: String, default: null },

  // ORIGIN ONE: an aggregate over a collection.
  source: { type: Object, default: null },
  // ORIGIN TWO: a field on the record this block is bound to.
  field: { type: String, default: null },
  record: { type: Object, default: null },

  // The denominator that turns the figure into a share: a number, or a sibling field on the record.
  max: { type: [Number, String], default: null },

  link: { type: Object, default: null },
  grow: { type: Boolean, default: true },
})

const router = useRouter()

// A stat inside a `card` block has already been given a border and a caption by that card. Drawing
// its own on top produced the box-in-a-box that made every dashboard look broken.
const depth = useSurface()

const aggregate = ref(null)
const loading = ref(false)
const failed = ref(false)

const fromRecord = computed(() => props.field !== null && props.record !== null)

const raw = computed(() => (fromRecord.value ? props.record?.[props.field] : aggregate.value))

const denominator = computed(() => {
  if (props.max === null || props.max === undefined || props.max === '') return null
  // A number is the denominator. A string names a sibling field on the same record, which is the
  // only way to say "of this project's total" without a second query.
  const value = typeof props.max === 'number' ? props.max : Number(props.record?.[props.max])
  return Number.isFinite(value) && value > 0 ? value : null
})

const shown = computed(() => {
  const value = raw.value
  if (value === null || value === undefined || value === '') return '—'

  // A share is the figure DIVIDED, and formatting it as a plain number would print the numerator
  // and call it a percentage.
  if (props.format === 'share' || props.format === 'percent') {
    if (denominator.value === null) return formatStat(value, props.format, props.source ?? {})
    return formatStat(Number(value) / denominator.value, props.format)
  }

  return formatStat(value, props.format, {
    currency: props.source?.currency ?? props.record?.currency,
  })
})

const meter = computed(() => {
  if (denominator.value === null) return null
  const value = Number(raw.value)
  if (!Number.isFinite(value)) return null
  return Math.max(0, Math.min(100, (value / denominator.value) * 100))
})

const sizes = { xs: 'text-body-1', sm: 'text-h6', md: 'text-h5', lg: 'text-h4', xl: 'text-h3' }
const weights = { normal: 'font-weight-regular', medium: 'font-weight-medium', bold: 'font-weight-bold' }

const figureClass = computed(() => [
  sizes[props.size] ?? sizes.md,
  weights[props.weight] ?? weights.bold,
  props.color ? `text-${props.color}` : '',
])

async function load() {
  if (!props.source) return
  loading.value = true
  failed.value = false
  try {
    const result = await loadAggregate(props.source, session)
    aggregate.value = result?.buckets?.[0]?.value ?? 0
  } catch {
    failed.value = true
  } finally {
    loading.value = false
  }
}

onMounted(load)
// A stat inside a repeat is re-bound as the list loads rather than re-created, so a source that
// names the row it is in has to be asked again.
watch(() => props.source, load, { deep: true })

const clickable = computed(() => Boolean(props.link?.page))

function open() {
  if (!clickable.value) return
  const query = {}
  for (const filter of props.link.filters || []) query[filter.field] = filter.value
  router.push({ path: `/${props.link.page.replaceAll('_', '-')}`, query })
}
</script>

<template>
  <!--
    Bounded, not merely growing.

    And NESTED, it is still a flex item. `w-100` inside a card was right for a stat sitting alone in
    a column and wrong for the ordinary case of two side by side: `width: 100%` in a wrapping row
    means each one takes a whole line, so "Open 2  Done 0" came out as two full-width rows and read
    as four separate facts. `flex: 1 1 auto` shares the row where there is one and still fills the
    width of a column, because a column flex item stretches on the cross axis anyway.

    `flex-grow-1` alone gave two stats inside a card half the page each — a card 550 pixels wide
    holding the word "Open" and a dash. A stat is a figure; it wants to be read at a glance beside
    its neighbours, which means every one of them the same size and none of them the size of a
    poster. So it grows to fill a strip of four and stops well before it fills a row of two.
  -->
  <component
    :is="depth === 0 ? 'v-card' : 'div'"
    :class="clickable ? 'cursor-pointer' : ''"
    :style="depth === 0
      ? (grow ? 'flex: 1 1 168px; max-width: 260px' : 'width: 200px')
      : 'flex: 1 1 auto; min-width: 0'"
    :ripple="depth === 0 ? false : undefined"
    @click="open"
  >
    <div :class="depth === 0 ? 'pa-4' : ''">
      <!-- No label means the container already carried it. An empty caption row there is a blank
           line the reader has to account for. -->
      <div
        v-if="label || clickable"
        class="d-flex align-center ga-2 text-caption text-medium-emphasis text-truncate"
      >
        <v-icon v-if="icon" :icon="`mdi-${icon}`" size="16" />
        <span class="text-truncate">{{ label }}</span>
        <v-spacer />
        <v-icon v-if="clickable" icon="mdi-arrow-top-right" size="14" class="text-disabled" />
      </div>

      <div class="d-flex align-baseline ga-2" :class="(label || clickable) ? 'mt-2' : ''">
        <v-progress-circular v-if="loading" indeterminate size="20" width="2" />
        <span v-else-if="failed" class="text-body-2 text-medium-emphasis">
          <v-icon icon="mdi-alert-circle-outline" size="16" class="mr-1" />unavailable
        </span>
        <template v-else>
          <span :class="figureClass">{{ shown }}</span>
          <span
            v-if="denominator !== null && format !== 'share' && format !== 'percent'"
            class="text-caption text-medium-emphasis"
          >
            / {{ formatStat(denominator, 'number') }}
          </span>
        </template>
      </div>

      <v-progress-linear
        v-if="meter !== null && !loading && !failed"
        :model-value="meter"
        :color="color || 'primary'"
        height="4"
        class="mt-3"
      />
    </div>
  </component>
</template>
