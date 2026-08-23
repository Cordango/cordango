<script setup>
// A control writes screen state and nothing else. No command, no request, no record: a day/week
// toggle and a "previous week" arrow change what the page is ASKING FOR, and the lists reading that
// state reload themselves because their filters resolved to something different.

const props = defineProps({
  control: { type: String, required: true },
  stateKey: { type: String, required: true },
  state: { type: Object, required: true },
  label: { type: String, default: null },
  options: { type: Array, default: () => [] },
  step: { type: Object, default: () => ({ unit: 'day', amount: 1 }) },
})

// Shifted as a date, not as text. Adding seven to '2026-08-30' by string arithmetic gives August
// 37th; going through Date is what makes the month roll over.
function shift(direction) {
  const at = new Date(props.state[props.stateKey] || new Date().toISOString().slice(0, 10))
  const by = (props.step?.amount ?? 1) * direction

  if (props.step?.unit === 'month') at.setMonth(at.getMonth() + by)
  else at.setDate(at.getDate() + by * (props.step?.unit === 'week' ? 7 : 1))

  props.state[props.stateKey] = at.toISOString().slice(0, 10)
}

const shown = () => {
  const raw = props.state[props.stateKey]
  if (!raw) return ''
  const at = new Date(raw)
  return Number.isNaN(at.getTime()) ? String(raw) : at.toLocaleDateString()
}
</script>

<template>
  <div class="d-flex align-center ga-2">
    <span v-if="label" class="text-body-2 text-medium-emphasis">{{ label }}</span>

    <v-btn-toggle
      v-if="control === 'segmented'"
      v-model="state[stateKey]"
      density="compact"
      variant="outlined"
      divided
      mandatory
    >
      <v-btn v-for="option in options" :key="option.value" :value="option.value" size="small">
        {{ option.label }}
      </v-btn>
    </v-btn-toggle>

    <template v-else>
      <v-btn icon="mdi-chevron-left" size="small" variant="text" @click="shift(-1)" />
      <span class="text-body-2">{{ shown() }}</span>
      <v-btn icon="mdi-chevron-right" size="small" variant="text" @click="shift(1)" />
    </template>
  </div>
</template>
