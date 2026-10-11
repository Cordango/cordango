<script setup>
import { ref, computed, watch } from 'vue'
import { formatValue, optionColor, optionLabel, loadReferenced, referenceLabel } from '../records.js'
import PersonName from './PersonName.vue'

const props = defineProps({ field: Object, value: null })

const isChip = computed(() => props.field?.type === 'select' && optionColor(props.field, props.value))
const isPerson = computed(() =>
  props.field?.type === 'reference' && (props.field.targetApp === 'platform' || props.field.targetEntity === 'person'))

// Any other reference reads as what the record it points at is CALLED. It printed the raw id, so the
// part on an order line was a uuid in the one spot somebody looks to see which part it is.
const isReference = computed(() => props.field?.type === 'reference' && !isPerson.value)
// `undefined` while it is being asked for, `null` once it could not be read.
const referenced = ref(undefined)

watch(() => [isReference.value, props.value], async ([reference, id]) => {
  referenced.value = undefined
  if (!reference || !id) return
  const record = await loadReferenced(props.field, id)
  if (id === props.value) referenced.value = record
}, { immediate: true })

const referenceText = computed(() => {
  if (referenced.value === undefined) return ''
  return referenced.value ? referenceLabel(props.field, referenced.value) : String(props.value)
})
</script>

<template>
  <span v-if="value === null || value === undefined || value === ''" class="text-disabled">—</span>

  <v-chip v-else-if="isChip" size="small" :color="optionColor(field, value)" variant="tonal">
    {{ optionLabel(field, value) }}
  </v-chip>

  <PersonName v-else-if="isPerson" :id="value" />

  <!-- The id stands in only when the record cannot be read at all. -->
  <span v-else-if="isReference">{{ referenceText }}</span>

  <a v-else-if="field?.type === 'attachment'" :href="`/api/media/${value}`" target="_blank" rel="noopener">
    Download
  </a>

  <a v-else-if="field?.type === 'url'" :href="value" target="_blank" rel="noopener">{{ value }}</a>
  <a v-else-if="field?.type === 'email'" :href="`mailto:${value}`">{{ value }}</a>

  <span v-else>{{ formatValue(value, field) }}</span>
</template>
