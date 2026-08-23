<script setup>
import { computed } from 'vue'
import { fieldOf } from '../records.js'
import FieldValue from './FieldValue.vue'

// A `chip` block: one field's value on its own, with no label beside it. Rendered through
// FieldValue so a coloured select looks the same here as it does in a table cell — the colour is
// the definition's, and a status that is amber in one place must not be grey in another.
const props = defineProps({ entity: String, record: Object, field: String })

const definition = computed(() => fieldOf(props.entity, props.field))
</script>

<template>
  <FieldValue :field="definition" :value="record?.[field]" />
</template>
