<script setup>
import { ref, computed, onMounted } from 'vue'
import { entityOf, loadRecords, createRecord, updateRecord, recordsChanged } from '../records.js'
import FieldInput from './FieldInput.vue'
import { useSurface } from './surface.js'

// A `settings` block: one row of configuration, edited in place. A settings entity holds a single
// record rather than a list, so there is nothing to choose between and no table to show — and if
// the row does not exist yet, saving creates it rather than telling somebody to go and make one.
const props = defineProps({
  entity: String,
  // The page heading already says this. Set by the emitter when the two would be the same words.
  hideTitle: { type: Boolean, default: false },
})

const depth = useSurface()

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
  <component :is="depth === 0 ? 'v-card' : 'div'">
    <v-card-title v-if="definition && !hideTitle">{{ definition.label }}</v-card-title>
    <!--
      The gap is the container's job, and nothing was doing it.

      Vuetify normally leaves room under every input for a validation message, and the theme's
      `hideDetails: 'auto'` default takes that room back — deliberately, because most of these
      fields never have anything to say. What it also took was the only thing separating one field
      from the next, so eleven settings rendered as eleven 48px boxes at a 48px pitch: borders
      touching, top to bottom, with no space anywhere on the page. RecordDialog already spaced its
      fields this way, which is why a form in a dialog looked right and this one did not.
    -->
    <v-card-text class="d-flex flex-column ga-4">
      <v-skeleton-loader v-if="loading" type="article" />
      <template v-else>
        <FieldInput
          v-for="field in shown"
          :key="field.key"
          :field="field"
          :model-value="record[field.key]"
          @update:model-value="(v) => (record[field.key] = v)"
        />
        <v-alert v-if="error" type="error" variant="tonal">{{ error }}</v-alert>
        <v-alert v-else-if="saved" type="success" variant="tonal">Saved.</v-alert>
      </template>
    </v-card-text>
    <v-card-actions v-if="!loading">
      <v-spacer />
      <v-btn color="primary" :loading="busy" @click="save">Save</v-btn>
    </v-card-actions>
  </component>
</template>
