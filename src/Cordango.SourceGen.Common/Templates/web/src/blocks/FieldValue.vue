<script setup>
import { computed } from 'vue'
import { formatValue, optionColor, optionLabel } from '../records.js'
import PersonName from './PersonName.vue'

const props = defineProps({ field: Object, value: null })

const isChip = computed(() => props.field?.type === 'select' && optionColor(props.field, props.value))
const isPerson = computed(() =>
  props.field?.type === 'reference' && (props.field.targetApp === 'platform' || props.field.targetEntity === 'person'))
</script>

<template>
  <span v-if="value === null || value === undefined || value === ''" class="text-disabled">—</span>

  <v-chip v-else-if="isChip" size="small" :color="optionColor(field, value)" variant="tonal">
    {{ optionLabel(field, value) }}
  </v-chip>

  <PersonName v-else-if="isPerson" :id="value" />

  <a v-else-if="field?.type === 'attachment'" :href="`/api/media/${value}`" target="_blank" rel="noopener">
    Download
  </a>

  <a v-else-if="field?.type === 'url'" :href="value" target="_blank" rel="noopener">{{ value }}</a>
  <a v-else-if="field?.type === 'email'" :href="`mailto:${value}`">{{ value }}</a>

  <span v-else>{{ formatValue(value, field) }}</span>
</template>
