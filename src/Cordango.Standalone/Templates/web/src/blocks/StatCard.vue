<script setup>
import { ref, onMounted, computed } from 'vue'
import { useRouter } from 'vue-router'
import { loadAggregate, formatValue } from '../records.js'
import { session } from '../session.js'

const props = defineProps({
  label: String,
  icon: String,
  format: String,
  source: { type: Object, required: true },
  link: { type: Object, default: null },
})

const router = useRouter()
const value = ref(null)
const loading = ref(true)
const failed = ref(false)

const shown = computed(() => {
  if (value.value === null || value.value === undefined) return '—'
  // `format` on a stat names how to read the number, and money is the one that changes what the
  // figure MEANS rather than just how it looks.
  return formatValue(value.value, { type: props.format || 'decimal', currency: props.source?.currency })
})

onMounted(async () => {
  try {
    const result = await loadAggregate(props.source, session)
    value.value = result?.buckets?.[0]?.value ?? 0
  } catch {
    failed.value = true
  } finally {
    loading.value = false
  }
})

function open() {
  if (!props.link?.page) return
  const query = {}
  for (const filter of props.link.filters || []) query[filter.field] = filter.value
  router.push({ path: `/${props.link.page.replaceAll('_', '-')}`, query })
}
</script>

<template>
  <v-card :class="link ? 'flex-grow-1 cursor-pointer' : 'flex-grow-1'" min-width="180" @click="open">
    <v-card-text>
      <div class="d-flex align-center ga-2 text-medium-emphasis text-caption">
        <v-icon v-if="icon" :icon="`mdi-${icon}`" size="small" />
        {{ label }}
      </div>
      <div class="text-h5 mt-1">
        <v-progress-circular v-if="loading" indeterminate size="20" />
        <span v-else-if="failed" class="text-error text-body-2">unavailable</span>
        <span v-else>{{ shown }}</span>
      </div>
    </v-card-text>
  </v-card>
</template>
