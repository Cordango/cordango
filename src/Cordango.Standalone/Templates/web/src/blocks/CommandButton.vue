<script setup>
import { ref, computed } from 'vue'
import { runCommand, entityOf, processOf } from '../records.js'
import FieldInput from './FieldInput.vue'

const props = defineProps({
  entity: String,
  record: Object,
  command: Object,
  density: { type: String, default: 'default' },
})
const emit = defineEmits(['done'])

const busy = ref(false)
const error = ref(null)
const asking = ref(false)
const input = ref({})

const entity = computed(() => entityOf(props.entity))
const inputFields = computed(() =>
  (props.command.input?.fields || []).map((k) => entity.value?.fields.find((f) => f.key === k)).filter(Boolean))

// A command that moves the record can only run from the states its transition allows. Hiding the
// button is friendlier than letting somebody press it and be told no — and the server checks
// anyway, because a hidden button is not a permission.
const available = computed(() => {
  const process = processOf(props.entity)
  const transition = process?.transitions?.find((t) => t.command === props.command.key)
  if (!transition) return true
  return (transition.from || []).includes(props.record?.[process.stateField])
})

const colors = { primary: 'primary', danger: 'error', neutral: undefined }

async function run() {
  if ((inputFields.value.length || props.command.confirm) && !asking.value) {
    asking.value = true
    return
  }

  busy.value = true
  error.value = null
  try {
    const result = await runCommand(props.entity, props.record.id, props.command.key, input.value)
    asking.value = false
    input.value = {}
    if (result?.message) window.dispatchEvent(new CustomEvent('cordango:toast', { detail: result.message }))
    emit('done', result)
  } catch (failure) {
    error.value = failure.message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <template v-if="available">
    <v-btn
      :color="colors[command.style]"
      :density="density"
      variant="tonal"
      size="small"
      class="ml-1"
      :loading="busy"
      @click="run"
    >
      {{ command.label }}
    </v-btn>

    <v-dialog v-model="asking" max-width="480">
      <v-card>
        <v-card-title>{{ command.confirm?.title || command.label }}</v-card-title>
        <v-card-text>
          <p v-if="command.confirm?.body" class="mb-4">{{ command.confirm.body }}</p>
          <FieldInput
            v-for="field in inputFields"
            :key="field.key"
            :field="field"
            :model-value="input[field.key]"
            @update:model-value="(v) => (input[field.key] = v)"
          />
          <v-alert v-if="error" type="error" variant="tonal" class="mt-2">{{ error }}</v-alert>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn variant="text" @click="asking = false">Cancel</v-btn>
          <v-btn
            :color="command.confirm?.tone === 'danger' ? 'error' : 'primary'"
            :loading="busy"
            @click="run"
          >
            {{ command.confirm?.confirmLabel || command.label }}
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
  </template>
</template>
