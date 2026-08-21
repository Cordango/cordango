<script setup>
import { computed } from 'vue'
import { entityOf, commandsOf, optionColor, optionLabel } from '../records.js'
import FieldValue from './FieldValue.vue'
import CommandButton from './CommandButton.vue'

const props = defineProps({
  entity: String,
  record: Object,
  title: String,
  status: String,
  facts: { type: Array, default: () => [] },
  actions: { type: Array, default: () => [] },
})
const emit = defineEmits(['changed', 'edit', 'remove'])

const definition = computed(() => entityOf(props.entity))
const statusField = computed(() => definition.value?.fields.find((f) => f.key === props.status))
const factFields = computed(() =>
  props.facts.map((k) => definition.value?.fields.find((f) => f.key === k)).filter(Boolean))

// `edit` and `delete` are actions the runtime provides. Everything else the definition lists is a
// command it declared, and only the ones it declared appear.
const commands = computed(() => commandsOf(props.entity).filter((c) => props.actions.includes(c.key)))
</script>

<template>
  <v-card>
    <v-card-text>
      <div class="d-flex align-start ga-4 flex-wrap">
        <div class="flex-grow-1">
          <div class="d-flex align-center ga-2">
            <h2 class="text-h6 mb-0">{{ record?.[title] || record?.id }}</h2>
            <v-chip
              v-if="statusField && record?.[status]"
              size="small"
              :color="optionColor(statusField, record[status])"
              variant="flat"
            >
              {{ optionLabel(statusField, record[status]) }}
            </v-chip>
          </div>

          <div class="d-flex flex-wrap ga-6 mt-3">
            <div v-for="field in factFields" :key="field.key">
              <div class="text-caption text-medium-emphasis">{{ field.label }}</div>
              <FieldValue :field="field" :value="record?.[field.key]" />
            </div>
          </div>
        </div>

        <div class="d-flex flex-wrap align-center">
          <CommandButton
            v-for="command in commands"
            :key="command.key"
            :entity="entity"
            :record="record"
            :command="command"
            @done="emit('changed')"
          />
          <v-btn
            v-if="actions.includes('edit')"
            size="small"
            variant="text"
            class="ml-1"
            @click="emit('edit')"
          >
            Edit
          </v-btn>
          <v-btn
            v-if="actions.includes('delete')"
            size="small"
            variant="text"
            color="error"
            @click="emit('remove')"
          >
            Delete
          </v-btn>
        </div>
      </div>
    </v-card-text>
  </v-card>
</template>
