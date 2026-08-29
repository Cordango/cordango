<script setup>
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { loadRecords, onRecordsChanged, sortTerm, recordRoute } from '../records.js'
import { session } from '../session.js'
import EmptyState from './EmptyState.vue'

// A `repeat` block: the same little layout, once per record. A row of scenario cards, a list of
// funding rounds — shapes a table cannot make, because each row is a composition rather than cells.
//
// The children are given the row through a scoped slot named `record`, which shadows whatever
// `record` meant outside. That is deliberate: a field inside a repeat means the repeated record,
// and any other reading would make the same block mean two things in two places.
const props = defineProps({
  entity: String,
  // The definition's own source fragment: which entity, which filters, which order, how many.
  source: { type: Object, default: () => ({}) },
  emptyText: { type: String, default: 'Nothing here yet.' },
  gap: { type: String, default: 'md' },

  // A CARD GRID rather than a list: wrapping columns that fill the width. This is the difference
  // between a directory of people and a stack of full-width rows, and it was silently dropped —
  // `wrap: true, cols: 3` emitted nothing at all, so every definition asking for a grid got a
  // column of page-wide cards and no diagnostic saying why.
  wrap: { type: Boolean, default: false },
  // How many across at the widest. NOT a fixed count: see the track sizing below.
  cols: { type: Number, default: null },
  // 'column' (default) | 'row' — for the non-wrapping case.
  direction: { type: String, default: 'column' },

  // The whole item opens its record.
  //
  // Decided by the GENERATOR, not here, because whether a click belongs to the item or to something
  // drawn inside it is a question about the block tree — and the generator has the tree at build
  // time. A card of fields is clickable; a card holding a table is not, because then every miss
  // beside a cell would navigate away from the row somebody was reading.
  clickable: { type: Boolean, default: false },
})

const rows = ref([])
const loading = ref(true)
const error = ref(null)

const entityKey = computed(() => props.source?.entity || props.entity)
const gaps = { none: '0px', sm: '8px', md: '16px', lg: '24px' }
const gapOf = (key) => gaps[key] ?? gaps.md

// THE AUTHORED COUNT IS THE WIDEST COUNT, not the only one. Three cards across is right on a laptop
// and absurd on a phone, so the count has to come down with the width — and it does so here in CSS
// rather than by measuring the container in JavaScript.
//
// `auto-fill` lays as many tracks as fit. Give each a minimum of "one nth of the row", and exactly n
// fit; floor that minimum at a readable width and fewer fit once the row is narrow enough that an
// nth would be too small. So the same declaration says "three across, but never narrower than
// 240px" with no resize listener, no measurement and nothing to be wrong about on first paint.
const MIN_CARD = '240px'

const layout = computed(() => {
  const gap = gapOf(props.gap)

  if (props.wrap) {
    const track = props.cols
      ? `minmax(max(${MIN_CARD}, calc((100% - ${props.cols - 1} * ${gap}) / ${props.cols})), 1fr)`
      : `minmax(min(${MIN_CARD}, 100%), 1fr)`
    return { display: 'grid', gridTemplateColumns: `repeat(auto-fill, ${track})`, gap }
  }

  const row = props.direction === 'row'
  return {
    display: 'flex',
    flexDirection: row ? 'row' : 'column',
    flexWrap: row ? 'wrap' : undefined,
    gap,
  }
})

async function load() {
  if (!entityKey.value) {
    rows.value = []
    loading.value = false
    return
  }

  loading.value = true
  error.value = null
  try {
    // Filters go in RAW: loadRecords resolves each one through filterTerm against the session, so
    // {{actor.id}} means the person reading the page rather than whoever built the definition.
    const page = await loadRecords(entityKey.value, {
      filters: props.source?.filters || [],
      sort: sortTerm(props.source?.sort),
      take: props.source?.limit ?? 50,
      context: session,
    })
    rows.value = page?.items ?? []
  } catch (failure) {
    error.value = failure.message
    rows.value = []
  } finally {
    loading.value = false
  }
}

const router = useRouter()
const open = (row) => router.push(recordRoute(entityKey.value, row.id))

let stop
onMounted(() => {
  load()
  stop = onRecordsChanged(entityKey.value, load)
})
onUnmounted(() => stop?.())
watch(() => props.source, load, { deep: true })
</script>

<template>
  <v-alert v-if="error" type="error">{{ error }}</v-alert>
  <v-skeleton-loader v-else-if="loading" type="list-item-two-line" />
  <EmptyState v-else-if="rows.length === 0" :title="emptyText" />
  <div v-else :style="layout">
    <template v-for="row in rows" :key="row.id">
      <!-- The wrapper is the grid item, so the card inside it stretches to the track rather than
           the click target being only as tall as the text. -->
      <div
        v-if="clickable && row.id"
        class="cd-repeat-item"
        role="link"
        tabindex="0"
        @click="open(row)"
        @keydown.enter="open(row)"
      >
        <slot :record="row" />
      </div>
      <slot v-else :record="row" />
    </template>
  </div>
</template>

<style scoped>
.cd-repeat-item { cursor: pointer; display: flex; border-radius: 4px; }
.cd-repeat-item > * { flex: 1 1 auto; min-width: 0; }
.cd-repeat-item:focus-visible { outline: 2px solid rgb(var(--v-theme-primary)); outline-offset: 2px; }
</style>
