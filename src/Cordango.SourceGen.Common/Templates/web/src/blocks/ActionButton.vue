<script setup>
import { computed } from 'vue'
import { commandsOf, recordsChanged } from '../records.js'
import CommandButton from './CommandButton.vue'

// An `action` block: one named command, placed on the screen rather than in the record's own
// toolbar. The button itself is CommandButton, which already knows how to ask for input, confirm,
// and hide itself when the record's state does not allow the move.
const props = defineProps({ entity: String, record: Object, command: String })

const definition = computed(() => commandsOf(props.entity).find((c) => c.key === props.command) || null)
</script>

<template>
  <CommandButton
    v-if="definition"
    :entity="entity"
    :record="record"
    :command="definition"
    @done="recordsChanged(entity)"
  />
</template>
