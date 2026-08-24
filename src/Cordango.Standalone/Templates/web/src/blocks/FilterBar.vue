<script setup>
import { ref, watch } from 'vue'
import { fieldChoices, fieldOf } from '../records.js'

// A page-level search box and facet dropdowns that WRITE screen state. It touches no data and runs
// no command: the lists on the page read the same state through their own filters, so one bar can
// drive a board and a table at once without either knowing the bar exists.
//
// Which is why every value here goes through `state` rather than being emitted. A component that
// emitted its query would have to be wired to each consumer by hand, and the whole point of the
// block is that nobody wires it.

const props = defineProps({
  entity: String,
  state: { type: Object, required: true },
  search: { type: Object, default: null },
  facets: { type: Array, default: () => [] },
})

const choices = ref({})

watch(
  () => props.facets,
  async (facets) => {
    const resolved = {}
    for (const facet of facets || []) {
      resolved[facet.state] = await fieldChoices(props.entity, facet.field)
    }
    choices.value = resolved
  },
  { immediate: true, deep: true })

const labelOf = (facet) => facet.label ?? fieldOf(props.entity, facet.field)?.label ?? facet.field

// A cleared dropdown must leave the state var EMPTY rather than null: the filter leaf reading it is
// `optional`, and "skip me" is spelled empty in one place so both ends agree.
const clear = (key) => { props.state[key] = '' }
</script>

<template>
  <v-sheet class="d-flex flex-wrap align-center ga-3 pa-3 mb-4" rounded border color="surface-light">
    <v-text-field
      v-if="search"
      v-model="state[search.state]"
      :placeholder="search.placeholder || 'Search'"
      density="compact"
      variant="outlined"
      hide-details
      clearable
      prepend-inner-icon="mdi-magnify"
      style="max-width: 320px"
      @click:clear="clear(search.state)"
    />

    <v-select
      v-for="facet in facets"
      :key="facet.state"
      v-model="state[facet.state]"
      :items="choices[facet.state] || []"
      item-title="label"
      item-value="value"
      :label="labelOf(facet)"
      density="compact"
      variant="outlined"
      hide-details
      clearable
      style="max-width: 220px"
      @click:clear="clear(facet.state)"
    />
  </v-sheet>
</template>
