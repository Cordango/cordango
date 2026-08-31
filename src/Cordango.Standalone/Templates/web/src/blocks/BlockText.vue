<script setup>
import { computed } from 'vue'
import { fieldOf, formatValue } from '../records.js'

// A line of prose on a screen: the sentence above a table that says what the table is for.
//
// The language spells the words `value`, with `text` as an alias, and the emitter used to read only
// the alias — so every paragraph written the canonical way arrived here empty and the component
// rendered a blank line. The prop is still called `text` because that is what a Vue component with
// one string in it should call it; both spellings are resolved before they get here.

const props = defineProps({
  text: String,
  // 'xs' | 'sm' | 'md' | 'lg' | 'xl'
  size: { type: String, default: 'sm' },
  // 'normal' | 'medium' | 'bold'
  weight: { type: String, default: 'normal' },
  color: { type: String, default: null },
  icon: { type: String, default: null },
  // Kept from the first version of this component, where 'muted' was the only choice there was.
  tone: { type: String, default: 'body' },
  // The record this line is about, where there is one — a detail screen, or the selected row of a
  // split. It is what makes `{full_name}` a name rather than four literal characters and a field
  // key, which is how a heading written the way the language documents it used to render.
  record: { type: Object, default: null },
  entity: { type: String, default: null },
})

// `{field_key}` -> that field of the record, formatted the way the same value is formatted
// everywhere else. A token naming a field the record does not carry is left ALONE rather than
// blanked: a line reading "{customre}" says which key was mistyped, and an empty line says nothing.
const resolved = computed(() => {
  const text = props.text ?? ''
  if (!props.record || !text.includes('{')) return text
  return text.replace(/\{([a-zA-Z0-9_]+)\}/g, (whole, key) => {
    if (!(key in props.record)) return whole
    const field = props.entity ? fieldOf(props.entity, key) : null
    return field ? formatValue(props.record[key], field) : String(props.record[key] ?? '')
  })
})

const sizes = {
  xs: 'text-caption', sm: 'text-body-2', md: 'text-body-1', lg: 'text-h6', xl: 'text-h5',
}
const weights = {
  normal: 'font-weight-regular', medium: 'font-weight-medium', bold: 'font-weight-bold',
}

const classes = computed(() => [
  sizes[props.size] ?? sizes.sm,
  weights[props.weight] ?? weights.normal,
  props.color ? `text-${props.color}` : (props.tone === 'muted' ? 'text-medium-emphasis' : ''),
])
</script>

<template>
  <p v-if="text" class="d-flex align-start ga-2 mb-0" :class="classes">
    <v-icon v-if="icon" :icon="`mdi-${icon}`" size="16" class="mt-1" />
    <span>{{ resolved }}</span>
  </p>
</template>
