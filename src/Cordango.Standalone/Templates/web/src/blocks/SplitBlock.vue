<script setup>
import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import { useRouter } from 'vue-router'
import {
  fieldOf, loadView, displayOf, formatValue, optionColor, optionLabel,
  matchesSearch, recordRoute, onRecordsChanged,
} from '../records.js'
import { session } from '../session.js'
import EmptyState from './EmptyState.vue'

// The INBOX: a list on the left, the selected record's detail on the right.
//
// The pattern for a queue somebody works through — leads to call, requests to triage, applications
// to read. A table sends you to a record page and back for each one, and the going back is the part
// that makes forty leads feel like forty tasks. Here the list stays put and only the right half
// changes, so the next one is one click away and you never lose your place.
//
// This component owns WHICH rows and WHICH one is selected. What the right half looks like is the
// block's own `blocks`, emitted into the `detail` slot with the selected row as `record` — so every
// block that works on a record page works here, unchanged and without knowing it is in a split.
const props = defineProps({
  definition: { type: Object, required: true },
  fields: { type: Array, default: () => [] },
  label: { type: String, default: '' },
  emptyText: { type: String, default: '' },
  state: { type: Object, default: null },
  // The record this split is inside, when it sits on a detail screen: `{{record.id}}` in a filter
  // resolves against it.
  record: { type: Object, default: null },
})

const router = useRouter()

const rows = ref([])
const loading = ref(true)
const failed = ref(false)
const query = ref('')
const selectedId = ref(null)

const entityKey = computed(() => props.definition?.entity)
const listFields = computed(() =>
  (props.fields || []).map((key) => fieldOf(entityKey.value, key)).filter(Boolean))

// The row's shape is read off the FIELD TYPES rather than off their names, because a split is
// generic and the author chose these fields for a list, not for this layout. Money and numbers sit
// on the right where the eye compares them; a date sits under them for the same reason; a select
// becomes a chip, which is what a status or a channel is; anything else is the line under the title.
const figureFields = computed(() =>
  listFields.value.filter((f) => ['money', 'decimal', 'integer'].includes(f.type)))
const dateFields = computed(() =>
  listFields.value.filter((f) => ['date', 'datetime'].includes(f.type)))
const chipFields = computed(() =>
  listFields.value.filter((f) => ['select', 'multiselect'].includes(f.type)))
const lineFields = computed(() => listFields.value.filter((f) =>
  !figureFields.value.includes(f) && !dateFields.value.includes(f) && !chipFields.value.includes(f)))

const searchable = computed(() => (props.fields || []).length ? props.fields : undefined)

const visible = computed(() => {
  if (!query.value.trim()) return rows.value
  return rows.value.filter((row) => matchesSearch(row, query.value, entityKey.value, searchable.value))
})

const selected = computed(() =>
  visible.value.find((r) => r.id === selectedId.value) ?? visible.value[0] ?? null)

async function load() {
  if (!props.definition?.entity) return
  loading.value = true
  failed.value = false
  try {
    const page = await loadView(props.definition, {
      personId: session.personId,
      userId: session.userId,
      state: props.state ?? {},
      record: props.record ?? {},
    })
    rows.value = page?.items ?? []
  } catch {
    // A failed load stays visible as a failure. An empty list and a list that could not be read are
    // different facts, and showing "nothing to work on" for the second is the reassuring lie.
    failed.value = true
    rows.value = []
  } finally {
    loading.value = false
  }
}

// The selection survives a reload by ID rather than by position: a list re-sorted after somebody
// marked the open record contacted must not silently move on to a different lead.
watch(visible, (list) => {
  if (!list.length) { selectedId.value = null; return }
  if (!list.some((r) => r.id === selectedId.value)) selectedId.value = list[0].id
})

watch(() => [props.definition, props.state, props.record], load, { deep: true })
onMounted(load)

let stop = null
onMounted(() => { stop = onRecordsChanged(entityKey.value, load) })
onUnmounted(() => { if (stop) stop() })

function open(row) {
  router.push(recordRoute(entityKey.value, row.id))
}
</script>

<template>
  <v-card variant="flat" border class="sp">
    <v-card-title v-if="label" class="text-subtitle-1 font-weight-medium">{{ label }}</v-card-title>

    <div class="sp-body">
      <div class="sp-list">
        <div class="sp-search">
          <v-text-field
            v-model="query" density="compact" variant="outlined" hide-details
            prepend-inner-icon="mdi-magnify" placeholder="Search…" />
        </div>

        <div v-if="loading" class="pa-4 text-medium-emphasis">Loading…</div>
        <div v-else-if="failed" class="pa-4 text-error">This list could not be loaded.</div>
        <EmptyState v-else-if="!visible.length" :title="emptyText || 'Nothing to work on'" />

        <v-list v-else density="compact" class="py-0">
          <v-list-item
            v-for="row in visible" :key="row.id"
            :active="row.id === selectedId" color="primary"
            class="sp-row" @click="selectedId = row.id">
            <div class="sp-row-top">
              <span class="sp-title">{{ displayOf(entityKey, row) }}</span>
              <span v-if="figureFields.length" class="sp-figure">
                {{ formatValue(row[figureFields[0].key], figureFields[0]) }}
              </span>
            </div>
            <div v-if="lineFields.length || dateFields.length" class="sp-row-top sp-meta">
              <span>{{ lineFields.length ? formatValue(row[lineFields[0].key], lineFields[0]) : '' }}</span>
              <span v-if="dateFields.length">{{ formatValue(row[dateFields[0].key], dateFields[0]) }}</span>
            </div>
            <div v-if="chipFields.length" class="sp-chips">
              <v-chip
                v-for="f in chipFields" :key="f.key" size="x-small" label
                :color="optionColor(f, row[f.key])" variant="flat">
                {{ optionLabel(f, row[f.key]) }}
              </v-chip>
            </div>
          </v-list-item>
        </v-list>
      </div>

      <div class="sp-detail">
        <template v-if="selected">
          <div class="sp-detail-head">
            <v-btn
              size="small" variant="text" class="text-none" append-icon="mdi-open-in-new"
              @click="open(selected)">Open full record</v-btn>
          </div>
          <!-- The author's own blocks, with the selected row as `record`. Everything a record page
               can draw, this can draw. -->
          <slot name="detail" :record="selected" />
        </template>
        <EmptyState v-else title="Nothing selected" body="Pick one from the list." />
      </div>
    </div>
  </v-card>
</template>

<style scoped>
.sp-body { display: grid; grid-template-columns: minmax(260px, 340px) 1fr; align-items: start; }
.sp-list { border-right: 1px solid rgba(var(--v-border-color), var(--v-border-opacity)); min-width: 0; }
.sp-search { padding: 12px; }
.sp-detail { padding: 16px; min-width: 0; }
.sp-detail-head { display: flex; justify-content: flex-end; margin-bottom: 4px; }
.sp-row { display: block; padding-top: 10px; padding-bottom: 10px; }
.sp-row-top { display: flex; align-items: baseline; justify-content: space-between; gap: 8px; }
.sp-title { font-weight: 600; }
.sp-figure { font-variant-numeric: tabular-nums; white-space: nowrap; }
.sp-meta { font-size: 12px; color: rgba(var(--v-theme-on-surface), .7); }
.sp-chips { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 6px; }

/* On a phone the two halves stack: the list first, then the record it opened. */
@media (max-width: 860px) {
  .sp-body { grid-template-columns: 1fr; }
  .sp-list { border-right: none; border-bottom: 1px solid rgba(var(--v-border-color), var(--v-border-opacity)); }
}
</style>
