<script setup>
import { ref, computed, watch } from 'vue'
import { entityOf, formatValue } from '../records.js'
import { personName } from '../api.js'

// A face, or the initials standing in for one.
//
// This block kind had no case in the emitter at all, so every `kind: avatar` in every definition
// fell through to the default branch and rendered an "avatar" chip explaining that the build could
// not draw it — on every card of every repeat, next to the name it was supposed to illustrate.
//
// It is deliberately NOT restricted to people. A definition can put an avatar on any field, and for
// a non-person that means the initials of whatever the field says — a company, a project, a room.
// Refusing everything but a person reference would be a narrower rule than the language has, and
// the failure would look identical to the gap this replaces.

const props = defineProps({
  entity: String,
  record: Object,
  field: String,
  // 'xs' | 'sm' | 'md' | 'lg' | 'xl'
  size: { type: String, default: 'md' },
})

const definition = computed(() =>
  entityOf(props.entity)?.fields.find((f) => f.key === props.field) ?? null)

const raw = computed(() => props.record?.[props.field])

const isPerson = computed(() =>
  definition.value?.type === 'reference'
  && (definition.value.targetApp === 'platform' || definition.value.targetEntity === 'person'))

// A person's id says nothing about their initials, so the name has to be resolved before there is
// anything to draw. Everything else already reads as itself.
const resolved = ref('')
watch([isPerson, raw], async ([person, value]) => {
  resolved.value = person ? await personName(value) : ''
}, { immediate: true })

const text = computed(() => {
  if (isPerson.value) return resolved.value
  const value = raw.value
  if (value === null || value === undefined || value === '') return ''
  return String(formatValue(value, definition.value))
})

const initials = computed(() =>
  text.value
    .split(/\s+/)
    .filter(Boolean)
    .map((word) => word[0])
    .slice(0, 2)
    .join('')
    .toUpperCase() || '•')

const pixels = { xs: 28, sm: 36, md: 44, lg: 56, xl: 72 }
const px = computed(() => pixels[props.size] ?? pixels.md)
</script>

<template>
  <v-avatar
    :size="px"
    color="surface-light"
    class="text-medium-emphasis font-weight-medium"
    :title="text || undefined"
  >
    <span :style="{ fontSize: Math.round(px * 0.4) + 'px' }">{{ initials }}</span>
  </v-avatar>
</template>
