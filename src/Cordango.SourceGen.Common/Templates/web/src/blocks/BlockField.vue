<script setup>
import { ref, computed, watch } from 'vue'
import { fieldOf, loadReferenced } from '../records.js'
import FieldValue from './FieldValue.vue'

// ONE field of the bound record, drawn the way the definition asked for it.
//
// This used to be `<RecordFields :fields="['name']" :columns="1" />`, which is a form row: a small
// grey caption with a value under it. That is the right shape for a detail pane and the wrong one
// everywhere else — a card whose title is a `field` block came out as the word "Name" above the
// project's name, three times per card, and the whole card read as a half-filled form. The
// presentation the definition carried (`size: lg`, `weight: bold`, `grow: true`) was dropped on the
// floor, because a form row has nowhere to put it.
//
// So there are TWO shapes here and `label` is the discriminator, which is the same rule the platform
// renderer uses:
//
//   - LABELLED is a fact. Caption above value; this is the detail-pane shape.
//   - UNLABELLED is a value in a line. It inherits size and weight, and it can `grow` to push its
//     neighbours to the ends of the row — which is what makes a card title a card title.
//
// Rendering the value itself stays with FieldValue, so a select is still a coloured chip and a
// person is still a name rather than a uuid, in both shapes and in tables too.

const props = defineProps({
  entity: String,
  record: Object,
  field: String,

  // An authored caption. Its ABSENCE is meaningful — see above — so there is no default.
  label: { type: String, default: null },

  // 'xs' | 'sm' | 'md' | 'lg' | 'xl'
  size: { type: String, default: 'md' },
  // 'normal' | 'medium' | 'bold'
  weight: { type: String, default: 'normal' },
  color: { type: String, default: null },
  icon: { type: String, default: null },

  // Take the free space in the row, so what follows is pushed to the end of it.
  grow: { type: Boolean, default: false },
})

// `item.sku` is the `sku` of the record `item` points at — the part an order line is for. The build
// only lets one hop through, and only to an entity of this application, so the path is a reference
// on this record and a field of its target, and nothing longer.
const path = computed(() => String(props.field ?? '').split('.'))
const via = computed(() => (path.value.length === 2 ? fieldOf(props.entity, path.value[0]) : null))

const definition = computed(() => via.value
  ? fieldOf(via.value.targetEntity, path.value[1])
  : fieldOf(props.entity, props.field))

const related = ref(null)

watch(() => (via.value ? props.record?.[via.value.key] : null), async (id) => {
  related.value = null
  if (!id) return
  const record = await loadReferenced(via.value, id)
  if (id === props.record?.[via.value?.key]) related.value = record
}, { immediate: true })

const value = computed(() => (via.value ? related.value?.[path.value[1]] : props.record?.[props.field]))

const sizes = {
  xs: 'text-caption', sm: 'text-body-2', md: 'text-body-1', lg: 'text-h6', xl: 'text-h5',
}
const weights = {
  normal: 'font-weight-regular', medium: 'font-weight-medium', bold: 'font-weight-bold',
}

const classes = computed(() => [
  sizes[props.size] ?? sizes.md,
  weights[props.weight] ?? weights.normal,
  props.color ? `text-${props.color}` : '',
])
</script>

<template>
  <div v-if="label" class="cd-fact" :class="grow ? 'flex-grow-1' : ''">
    <div class="text-caption text-medium-emphasis">{{ label }}</div>
    <div :class="classes">
      <FieldValue :field="definition" :value="value" />
    </div>
  </div>

  <span
    v-else
    class="d-inline-flex align-center ga-1 text-truncate"
    :class="[...classes, grow ? 'flex-grow-1' : '']"
    :style="grow ? 'min-width: 0' : ''"
  >
    <v-icon v-if="icon" :icon="`mdi-${icon}`" size="16" />
    <FieldValue :field="definition" :value="value" />
  </span>
</template>
