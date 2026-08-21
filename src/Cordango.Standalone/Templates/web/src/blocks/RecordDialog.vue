<script setup>
import { ref, computed } from 'vue'
import { entityOf, createRecord, updateRecord } from '../records.js'
import FieldInput from './FieldInput.vue'

const props = defineProps({ entity: String, record: Object })
const emit = defineEmits(['close', 'saved'])

const open = ref(true)
const busy = ref(false)
const error = ref(null)
const fields = ref({ ...props.record })

const isNew = computed(() => !props.record?.id)
const definition = computed(() => entityOf(props.entity))

// What a person can fill in. System fields are set by the runtime, and fields the definition marks
// hideOnCreate are filled later in the process — putting an approver box on a creation form invites
// somebody to approve their own request.
const shown = computed(() =>
  (definition.value?.fields ?? []).filter(
    (f) => !f.system && !f.readOnly && !(isNew.value && f.hideOnCreate),
  ))

async function save() {
  busy.value = true
  error.value = null
  try {
    const body = {}
    for (const field of shown.value) {
      if (fields.value[field.key] !== undefined) body[field.key] = fields.value[field.key]
    }

    if (isNew.value) await createRecord(props.entity, body)
    else await updateRecord(props.entity, props.record.id, body)

    emit('saved')
  } catch (failure) {
    error.value = failure.message
  } finally {
    busy.value = false
  }
}

function close(value) {
  if (!value) emit('close')
}
</script>

<template>
  <v-dialog v-model="open" max-width="640" @update:model-value="close">
    <v-card>
      <v-card-title>{{ isNew ? 'New ' + (definition?.label || '') : definition?.label }}</v-card-title>
      <v-card-text>
        <FieldInput
          v-for="field in shown"
          :key="field.key"
          :field="field"
          :model-value="fields[field.key]"
          @update:model-value="(v) => (fields[field.key] = v)"
        />
        <v-alert v-if="error" type="error" variant="tonal" class="mt-2">{{ error }}</v-alert>
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn variant="text" @click="emit('close')">Cancel</v-btn>
        <v-btn color="primary" :loading="busy" @click="save">Save</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>
