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

        <!--
          Up to two commands stay as buttons, because a record with one obvious next step should
          show it. Beyond that they collapse: a detail header with six buttons across it has no
          primary action at all, which is the same problem the table rows had.
        -->
        <div class="d-flex flex-wrap align-center ga-1">
          <template v-if="commands.length <= 2">
            <CommandButton
              v-for="command in commands"
              :key="command.key"
              :entity="entity"
              :record="record"
              :command="command"
              @done="emit('changed')"
            />
          </template>

          <v-btn
            v-if="actions.includes('edit')"
            size="small"
            variant="tonal"
            prepend-icon="mdi-pencil-outline"
            @click="emit('edit')"
          >
            Edit
          </v-btn>

          <v-menu v-if="commands.length > 2 || actions.includes('delete')" location="bottom end">
            <template #activator="{ props }">
              <v-btn icon="mdi-dots-horizontal" size="small" v-bind="props" />
            </template>
            <v-list>
              <template v-if="commands.length > 2">
                <CommandButton
                  v-for="command in commands"
                  :key="command.key"
                  as="item"
                  :entity="entity"
                  :record="record"
                  :command="command"
                  @done="emit('changed')"
                />
                <v-divider v-if="actions.includes('delete')" />
              </template>
              <v-list-item
                v-if="actions.includes('delete')"
                prepend-icon="mdi-delete-outline"
                title="Delete"
                base-color="error"
                @click="emit('remove')"
              />
            </v-list>
          </v-menu>
        </div>
      </div>
    </v-card-text>
  </v-card>
</template>
