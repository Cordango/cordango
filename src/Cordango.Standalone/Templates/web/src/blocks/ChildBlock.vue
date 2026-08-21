<script setup>
import { computed } from 'vue'
import { app } from '../app.js'
import ViewBlock from './ViewBlock.vue'

// The rows of another entity that point back at this one. The definition names the child entity and
// the field holding the reference; the filter is that field equalling this record's id.
const props = defineProps({ entity: String, field: String, record: Object })

const view = computed(() => app.views.find((v) => v.entity === props.entity)?.key)
const filters = computed(() => [{ field: props.field, operator: 'eq', value: props.record?.id }])
</script>

<template>
  <ViewBlock v-if="view" :view="view" :extra-filters="filters" />
</template>
