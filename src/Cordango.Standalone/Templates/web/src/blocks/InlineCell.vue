<script setup>
import { ref, computed, watch } from 'vue'
import { updateRecord, fieldChoices, formatValue, optionColor, optionLabel } from '../records.js'
import FieldValue from './FieldValue.vue'

// One cell of a list, edited where it sits.
//
// A task list is the case that makes this not a nicety: ticking "done" is the whole interaction, and
// a list that makes somebody open a record, change one checkbox and come back has turned a gesture
// into a trip. The first column still opens the record, so nothing is lost.
//
// It writes the ONE field it is about, with PATCH rather than a whole-record PUT. Sending the row
// back would overwrite whatever else changed on it since the page loaded, from a screen that never
// showed those fields to anybody.

const props = defineProps({
  entity: String,
  field: { type: Object, required: true },
  record: { type: Object, required: true },
  editable: { type: Boolean, default: false },
  // Reference ids the enclosing list has already resolved to names.
  labels: { type: Object, default: null },
})

const emit = defineEmits(['saved', 'failed'])

const draft = ref(props.record[props.field.key])
const saving = ref(false)
const choices = ref([])

watch(() => props.record[props.field.key], (v) => { draft.value = v })

const kind = computed(() => {
  const type = props.field.type
  if (type === 'boolean') return 'boolean'
  if (type === 'select' || type === 'reference') return 'choice'
  if (type === 'date') return 'date'
  if (type === 'datetime') return 'datetime'
  if (type === 'integer' || type === 'decimal' || type === 'money') return 'number'
  if (type === 'text' || type === 'longtext') return 'text'
  // Everything else — an attachment, a multiselect, a computed figure — reads here and is changed on
  // the record. Offering a half-control in a cell would be worse than not offering one.
  return 'read'
})

if (props.editable && kind.value === 'choice') {
  fieldChoices(props.entity, props.field.key).then((items) => { choices.value = items })
}

const shown = computed(() => {
  const value = props.record[props.field.key]
  if (value === null || value === undefined || value === '') return null
  return props.labels?.[value] ?? formatValue(value, props.field)
})

async function save(value) {
  if (value === props.record[props.field.key]) return
  saving.value = true
  try {
    await updateRecord(props.entity, props.record.id, { [props.field.key]: value })
    emit('saved')
  } catch (failure) {
    draft.value = props.record[props.field.key]
    emit('failed', failure.message)
  } finally {
    saving.value = false
  }
}

// A number arrives from a text field as a string, and PATCHing "12" into a decimal column is a
// different request from PATCHing 12.
const commit = () => save(kind.value === 'number' && draft.value !== '' && draft.value !== null
  ? Number(draft.value)
  : (draft.value === '' ? null : draft.value))
</script>

<template>
  <span v-if="!editable || kind === 'read'">
    <span v-if="labels && shown">{{ shown }}</span>
    <FieldValue v-else :field="field" :value="record[field.key]" />
  </span>

  <v-checkbox-btn
    v-else-if="kind === 'boolean'"
    :model-value="!!record[field.key]"
    :loading="saving"
    density="compact"
    class="d-inline-flex"
    @update:model-value="save($event)"
  />

  <v-select
    v-else-if="kind === 'choice'"
    v-model="draft"
    :items="choices"
    item-title="label"
    item-value="value"
    density="compact"
    variant="plain"
    hide-details
    clearable
    :loading="saving"
    style="min-width: 8rem"
    @update:model-value="save($event === undefined ? null : $event)"
  >
    <template #selection="{ item }">
      <v-chip v-if="optionColor(field, item.value)" size="x-small" variant="tonal" :color="optionColor(field, item.value)">
        {{ optionLabel(field, item.value) }}
      </v-chip>
      <span v-else class="text-body-2">{{ item.title }}</span>
    </template>
  </v-select>

  <v-text-field
    v-else
    v-model="draft"
    :type="kind === 'date' ? 'date' : kind === 'datetime' ? 'datetime-local' : kind === 'number' ? 'number' : 'text'"
    density="compact"
    variant="plain"
    hide-details
    :loading="saving"
    style="min-width: 7rem"
    @blur="commit"
    @keyup.enter="commit"
  />
</template>
