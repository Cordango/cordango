<script setup>
import { toRef } from 'vue'
import { provideScope } from '../scopes.js'

// One named record, provided to whatever is drawn inside it. A repeat draws one of these per row, so
// each row's blocks find their own row under the name rather than whichever row rendered last — a
// single provide on the repeat itself would hand every row the same one.
//
// It draws nothing of its own. The names it now holds go back out through the slot as well, for the
// one reader that cannot inject: a `visibleWhen` the page evaluates in its own template.
const props = defineProps({
  name: { type: String, default: null },
  record: { type: Object, default: null },
})

const scopes = provideScope(props.name, toRef(props, 'record'))
</script>

<template>
  <slot :scopes="scopes" />
</template>
