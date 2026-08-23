<script setup>
import { ref, computed, onMounted } from 'vue'
import { entityOf, loadRecords, createRecord, updateRecord, recordsChanged } from '../records.js'
import FieldInput from './FieldInput.vue'

// A `settings` block: one row of configuration, edited in place. A settings entity holds a single
// record rather than a list, so there is nothing to choose between and no table to show — and if
// the row does not exist yet, saving creates it rather than telling somebody to go and make one.
const props = defineProps({ entity: String })

const record = ref(null)
const loading = ref(true)
const busy = ref(false)
const error = ref(null)
const saved = ref(false)

const definition = computed(() => entityOf(props.entity))
const shown = computed(() =>
  (definition.value?.fields ?? []).filter((f) => !f.system && !f.readOnly))

onMounted(async () => {
  try {
    const page = await loadRecords(props.entity, { take: 1 })
    record.value = { ...(page?.items?.[0] ?? {}) }
  } catch (failure) {
    error.value = failure.message
    record.value = {}
  } finally {
    loading.value = false
  }
})

async function save() {
  busy.value = true
  error.value = null
  saved.value = false
  try {
    const body = {}
    for (const field of shown.value) body[field.key] = record.value[field.key] ?? null

    const result = record.value.id
      ? await updateRecord(props.entity, record.value.id, body)
      : await createRecord(props.entity, body)

    record.value = { ...result }
    saved.value = true
    recordsChanged(props.entity)
  } catch (failure) {
    error.value = failure.message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <v-card variant="outlined">
    <v-card-title v-if="definition">{{ definition.label }}</v-card-title>
    <v-card-text>
      <v-skeleton-loader v-if="loading" type="article" />
      <template v-else>
        <FieldInput
          v-for="field in shown"
          :key="field.key"
          :field="field"
          :model-value="record[field.key]"
          @update:model-value="(v) => (record[field.key] = v)"
        />
        <v-alert v-if="error" type="error" variant="tonal" class="mt-2">{{ error }}</v-alert>
        <v-alert v-else-if="saved" type="success" variant="tonal" class="mt-2">Saved.</v-alert>
      </template>
    </v-card-text>
    <v-card-actions v-if="!loading">
      <v-spacer />
      <v-btn color="primary" :loading="busy" @click="save">Save</v-btn>
    </v-card-actions>
  </v-card>
</template>
