<script setup>
import { ref, onMounted, onUnmounted } from 'vue'
import { Chart, registerables } from 'chart.js'
import { loadAggregate } from '../records.js'
import { session } from '../session.js'

Chart.register(...registerables)

const props = defineProps({
  label: String,
  chartType: { type: String, default: 'bar' },
  source: { type: Object, required: true },
})

const canvas = ref(null)
const empty = ref(false)
let chart = null

// donut is Cordango's word and doughnut is Chart.js's. Everything else lines up.
const typeOf = (kind) => (kind === 'donut' ? 'doughnut' : kind === 'area' ? 'line' : kind)

onMounted(async () => {
  let buckets = []
  try {
    const result = await loadAggregate(props.source, session)
    buckets = result?.buckets ?? []
  } catch {
    empty.value = true
    return
  }

  if (buckets.length === 0) {
    empty.value = true
    return
  }

  chart = new Chart(canvas.value, {
    type: typeOf(props.chartType),
    data: {
      labels: buckets.map((b) => b.key || '—'),
      datasets: [{ label: props.label, data: buckets.map((b) => Number(b.value ?? 0)) }],
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      plugins: { legend: { display: typeOf(props.chartType) !== 'bar' } },
    },
  })
})

// Chart.js keeps a global registry keyed by canvas; leaving one behind makes the next mount of the
// same page fail with "canvas is already in use".
onUnmounted(() => chart?.destroy())
</script>

<template>
  <v-card>
    <v-card-title v-if="label" class="text-subtitle-1">{{ label }}</v-card-title>
    <v-card-text>
      <div v-if="empty" class="text-medium-emphasis text-body-2 py-8 text-center">Nothing to chart yet.</div>
      <div v-else style="height: 260px"><canvas ref="canvas" /></div>
    </v-card-text>
  </v-card>
</template>
