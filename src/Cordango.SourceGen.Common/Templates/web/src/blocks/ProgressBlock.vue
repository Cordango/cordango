<script setup>
import { ref, computed, watch, onMounted } from 'vue'
import { loadAggregate, formatStat } from '../records.js'
import { session } from '../session.js'

// How far along something is: a value, a denominator, and a bar.
//
// The language REQUIRES `field` and `max` on this block — a progress bar with no numerator and no
// denominator is not a progress bar. The emitter read neither, so every one of these rendered as
// "0 / " with an empty bar and no error anywhere: a definition could ask for it, the generator
// could claim to support it, the build could pass, and the screen still showed nothing.

const props = defineProps({
  label: String,
  // The record this bar is about, and the field on it holding the figure.
  record: { type: Object, default: null },
  field: { type: String, default: null },
  // A sibling field key on the same record, or a literal number.
  max: { type: [Number, String], default: null },
  // The collection form: an aggregate rather than a record's own field.
  source: { type: Object, default: null },
  target: { type: [Number, String], default: null },
  color: { type: String, default: null },
})

const aggregate = ref(null)
const loading = ref(false)

const value = computed(() => {
  const raw = props.field !== null && props.record !== null
    ? props.record?.[props.field]
    : aggregate.value
  const number = Number(raw)
  return Number.isFinite(number) ? number : null
})

const denominator = computed(() => {
  const declared = props.max ?? props.target
  if (declared === null || declared === undefined || declared === '') return null
  const number = typeof declared === 'number' ? declared : Number(props.record?.[declared] ?? declared)
  return Number.isFinite(number) && number > 0 ? number : null
})

const percent = computed(() => {
  if (value.value === null || denominator.value === null) return null
  return Math.max(0, Math.min(100, (value.value / denominator.value) * 100))
})

const caption = computed(() => {
  if (value.value === null) return '—'
  if (denominator.value === null) return formatStat(value.value, 'number')
  return `${formatStat(value.value, 'number')} / ${formatStat(denominator.value, 'number')}`
})

async function load() {
  if (!props.source) return
  loading.value = true
  try {
    const result = await loadAggregate(props.source, session)
    aggregate.value = Number(result?.buckets?.[0]?.value ?? 0)
  } catch {
    // A figure that cannot be read leaves the bar empty and the caption honest, rather than
    // showing a confident zero.
    aggregate.value = null
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch(() => props.source, load, { deep: true })
</script>

<template>
  <div class="w-100">
    <div class="d-flex justify-space-between align-center text-caption text-medium-emphasis mb-1">
      <span class="text-truncate">{{ label }}</span>
      <span class="flex-shrink-0 ml-2 font-weight-medium">
        {{ caption }}
        <template v-if="percent !== null"> · {{ Math.round(percent) }}%</template>
      </span>
    </div>
    <v-progress-linear
      :model-value="percent ?? 0"
      :indeterminate="loading"
      :color="color || 'primary'"
      height="6"
      rounded
      :bg-opacity="0.12"
    />
  </div>
</template>
