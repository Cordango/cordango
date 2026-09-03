<script setup>
import { computed } from 'vue'
import { processOf } from '../records.js'

const props = defineProps({ entity: String, record: Object })

const process = computed(() => processOf(props.entity))
const states = computed(() => process.value?.states ?? [])
const currentIndex = computed(() =>
  states.value.findIndex((s) => s.key === props.record?.[process.value?.stateField]))
</script>

<template>
  <v-card v-if="process" variant="tonal">
    <v-card-text class="d-flex flex-wrap align-center ga-2">
      <template v-for="(state, index) in states" :key="state.key">
        <v-chip
          size="small"
          :color="index <= currentIndex ? state.color : undefined"
          :variant="index === currentIndex ? 'flat' : 'outlined'"
        >
          {{ state.label }}
        </v-chip>
        <v-icon v-if="index < states.length - 1" icon="mdi-chevron-right" size="small" class="text-disabled" />
      </template>
    </v-card-text>
  </v-card>
</template>
