<script setup>
import { computed } from 'vue'
import { fieldOf, formatValue } from '../records.js'

// A `tiles` block: several of one record's own figures, side by side. Not StatCard — that reads an
// aggregate across many records, and these are columns off the record already on screen.
const props = defineProps({
  entity: String,
  record: Object,
  tiles: { type: Array, default: () => [] },
})

const shown = computed(() =>
  props.tiles.map((tile) => {
    const field = fieldOf(props.entity, tile.field)
    const value = props.record?.[tile.field]
    return {
      key: tile.field,
      label: tile.label || field?.label || tile.field,
      icon: tile.icon,
      // A blank figure is shown as a dash rather than as nothing, because an empty tile reads as a
      // rendering fault and a dash reads as "no value yet" — which is what it is.
      text: value === null || value === undefined || value === ''
        ? '—'
        : formatValue(value, field),
    }
  }))
</script>

<template>
  <v-row dense>
    <v-col v-for="tile in shown" :key="tile.key" cols="12" sm="6" md="3">
      <v-sheet border rounded class="pa-3 h-100">
        <div class="d-flex align-center ga-1 text-caption text-medium-emphasis">
          <v-icon v-if="tile.icon" :icon="'mdi-' + tile.icon" size="x-small" />
          <span>{{ tile.label }}</span>
        </div>
        <div class="text-h6 mt-1">{{ tile.text }}</div>
      </v-sheet>
    </v-col>
  </v-row>
</template>
