<script setup>
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { loadRecords, onRecordsChanged, sortTerm } from '../records.js'
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
})

const rows = ref([])
const loading = ref(true)
const error = ref(null)

const entityKey = computed(() => props.source?.entity || props.entity)
const gaps = { none: 'ga-0', sm: 'ga-2', md: 'ga-4', lg: 'ga-6' }

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
  <div v-else class="d-flex flex-column" :class="gaps[gap] || 'ga-4'">
    <template v-for="row in rows" :key="row.id">
      <slot :record="row" />
    </template>
  </div>
</template>
