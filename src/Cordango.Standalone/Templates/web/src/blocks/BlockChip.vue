<script setup>
import { computed } from 'vue'
import { fieldOf } from '../records.js'
import FieldValue from './FieldValue.vue'

// A `chip` block: one value on its own, with no label beside it.
//
// It has two origins and only one was read. `field` names a field on the bound record and goes
// through FieldValue, so a coloured select looks the same here as it does in a table cell — the
// colour is the definition's, and a status that is amber in one place must not be grey in another.
// `value` is a literal the definition wrote itself, and a chip block carrying one used to render an
// empty span: it was asked for the record's `undefined` field and correctly found nothing there.

const props = defineProps({ entity: String, record: Object, field: String, value: String })

const definition = computed(() => (props.field ? fieldOf(props.entity, props.field) : null))
</script>

<template>
  <FieldValue v-if="field" :field="definition" :value="record?.[field]" />
  <v-chip v-else-if="value" size="small" variant="tonal">{{ value }}</v-chip>
</template>
