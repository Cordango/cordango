<script setup>
import { ref, computed, watch } from 'vue'
import { useRouter } from 'vue-router'
import { CordangoSheet } from '@cordango/web-controls'
import { entityOf, displayOf, loadRecord, processOf, recordRoute } from '../records.js'
import RecordFields from './RecordFields.vue'
import RecordProcess from './RecordProcess.vue'

// A record read WITHOUT leaving the list.
//
// The trip a list makes you take is the whole problem: click a row to see when it is due, read one
// date, press back, lose your scroll position and your filters, find your place again. For the
// common question — what IS this one — the page was never the point.
//
// So this is a quick look and it says so. It shows the record's own fields and, where a lifecycle
// governs it, where it has got to. It does NOT reproduce the detail page's tabs: a panel that tried
// to be the page would be a worse page, and the link at the bottom is one click to the real thing.
//
// A definition opts in with `openDetail: true`. Left off, a row click still navigates — which is
// the right default for a list whose rows ARE the work rather than a thing to glance at.
//
// The panel is a side sheet rather than an anchored popover. A record has a dozen fields and a
// popover that size stops being in-context and becomes a dialog that forgot to dim the page.

const props = defineProps({
  entity: String,
  // The row the list handed over. Re-read on open, because a list carries the columns it shows and
  // a peek shows everything — the row in hand is a subset, and rendering it would print dashes
  // against every field the table happened not to select.
  record: Object,
  modelValue: Boolean,
})

const emit = defineEmits(['update:modelValue'])

const router = useRouter()

const open = computed({
  get: () => props.modelValue,
  set: (value) => emit('update:modelValue', value),
})

const full = ref(null)
const loading = ref(false)

watch(() => [props.modelValue, props.record?.id], async ([shown, id]) => {
  if (!shown || !id) return
  // Show what the list already knows immediately, then fill in the rest. A panel that opens empty
  // and populates a moment later reads as slow; one that opens with the title already in it does
  // not, even though it is the same request.
  full.value = props.record
  loading.value = true
  try {
    full.value = await loadRecord(props.entity, id)
  } catch {
    // The row is in hand and the panel is already showing it. A failed refresh means some fields
    // stay blank, which is a smaller wrong than replacing a readable panel with an error.
  } finally {
    loading.value = false
  }
}, { immediate: true })

const title = computed(() => (full.value ? displayOf(props.entity, full.value) : ''))
const fields = computed(() =>
  entityOf(props.entity)?.fields.filter((f) => !f.system).map((f) => f.key) ?? [])
const governed = computed(() => Boolean(processOf(props.entity)))

function openFull() {
  open.value = false
  router.push(recordRoute(props.entity, props.record.id))
}
</script>

<template>
  <CordangoSheet v-model="open" :title="title">
    <div v-if="full" class="d-flex flex-column ga-4">
      <RecordProcess v-if="governed" :entity="entity" :record="full" />
      <RecordFields :entity="entity" :record="full" :fields="fields" :columns="1" />
    </div>
    <v-progress-linear v-else indeterminate />

    <template #actions>
      <v-btn variant="text" @click="open = false">Close</v-btn>
      <v-btn color="primary" variant="flat" append-icon="mdi-arrow-top-right" @click="openFull">
        Open record
      </v-btn>
    </template>
  </CordangoSheet>
</template>
