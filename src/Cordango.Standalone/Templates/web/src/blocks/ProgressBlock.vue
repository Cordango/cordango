<script setup>
import { ref, onMounted, computed } from 'vue'
import { loadAggregate } from '../records.js'
import { session } from '../session.js'

const props = defineProps({ label: String, source: Object, target: [Number, String] })

const value = ref(0)
onMounted(async () => {
  try {
    const result = await loadAggregate(props.source, session)
    value.value = Number(result?.buckets?.[0]?.value ?? 0)
  } catch { /* a figure that cannot be read shows as zero rather than breaking the page */ }
})

const percent = computed(() => {
  const target = Number(props.target || 0)
  return target > 0 ? Math.min(100, (value.value / target) * 100) : 0
})
</script>

<template>
  <v-card>
    <v-card-text>
      <div class="d-flex justify-space-between text-caption text-medium-emphasis">
        <span>{{ label }}</span><span>{{ value }} / {{ target }}</span>
      </div>
      <v-progress-linear :model-value="percent" height="10" rounded class="mt-2" />
    </v-card-text>
  </v-card>
</template>
