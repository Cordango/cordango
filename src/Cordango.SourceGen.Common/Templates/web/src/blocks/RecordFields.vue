<script setup>
import { computed } from 'vue'
import { entityOf } from '../records.js'
import FieldValue from './FieldValue.vue'

const props = defineProps({
  entity: String,
  record: Object,
  fields: { type: Array, default: null },
  columns: { type: Number, default: 2 },
})

const shown = computed(() => {
  const entity = entityOf(props.entity)
  if (!entity) return []
  const keys = props.fields ?? entity.fields.filter((f) => !f.system).map((f) => f.key)
  return keys.map((key) => entity.fields.find((f) => f.key === key)).filter(Boolean)
})
</script>

<template>
  <v-row dense>
    <v-col v-for="field in shown" :key="field.key" cols="12" :md="12 / columns">
      <div class="text-caption text-medium-emphasis">{{ field.label }}</div>
      <div><FieldValue :field="field" :value="record?.[field.key]" /></div>
    </v-col>
  </v-row>
</template>
