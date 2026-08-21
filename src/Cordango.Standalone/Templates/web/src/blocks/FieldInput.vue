<script setup>
import { computed, ref } from 'vue'
import { api } from '../api.js'
import { loadRecords } from '../records.js'

const props = defineProps({ field: Object, modelValue: null })
const emit = defineEmits(['update:model-value'])

const value = computed({
  get: () => props.modelValue,
  set: (v) => emit('update:model-value', v),
})

const rules = computed(() => (props.field?.required ? [(v) => !!v || 'Required'] : []))

const inputTypes = {
  date: 'date',
  datetime: 'datetime-local',
  integer: 'number',
  decimal: 'number',
  money: 'number',
  email: 'email',
  url: 'url',
}

const inputType = computed(() => inputTypes[props.field?.type] || 'text')

const suffix = computed(() => {
  if (props.field?.unit) return props.field.unit
  if (props.field?.type === 'money') return props.field.currency || 'EUR'
  return undefined
})

// Reference pickers load their options when the menu first opens, not on mount. A form with six
// references would otherwise fetch six tables before showing the person anything.
const options = ref([])
const loadingOptions = ref(false)

async function loadOptions() {
  if (options.value.length || loadingOptions.value) return
  loadingOptions.value = true
  try {
    const isPerson = props.field.targetApp === 'platform' || props.field.targetEntity === 'person'
    const target = isPerson ? 'directory/person' : props.field.targetEntity
    const page = await loadRecords(target, { take: 200 })
    options.value = (page?.items ?? []).map((r) => ({
      value: r.id,
      title: r.full_name || r.name || r.title || r.description || r.id,
    }))
  } finally {
    loadingOptions.value = false
  }
}

const uploading = ref(false)

async function upload(files) {
  const file = Array.isArray(files) ? files[0] : files
  if (!file) return
  uploading.value = true
  try {
    // The field stores the reference the server hands back, not the file. Content-addressed, so the
    // same document attached twice is stored once.
    const stored = await api.upload(file)
    value.value = stored.reference
  } finally {
    uploading.value = false
  }
}
</script>

<template>
  <v-textarea
    v-if="field.type === 'longtext'"
    v-model="value"
    :label="field.label"
    :hint="field.help"
    :rules="rules"
    rows="3"
    auto-grow
  />

  <v-checkbox
    v-else-if="field.type === 'boolean'"
    v-model="value"
    :label="field.label"
    :hint="field.help"
  />

  <v-select
    v-else-if="field.type === 'select'"
    v-model="value"
    :label="field.label"
    :hint="field.help"
    :rules="rules"
    :items="field.options || []"
    item-title="label"
    item-value="value"
  />

  <v-select
    v-else-if="field.type === 'multiselect'"
    v-model="value"
    :label="field.label"
    :items="field.options || []"
    item-title="label"
    item-value="value"
    multiple
    chips
  />

  <v-autocomplete
    v-else-if="field.type === 'reference'"
    v-model="value"
    :label="field.label"
    :hint="field.help"
    :rules="rules"
    :items="options"
    :loading="loadingOptions"
    @update:menu="loadOptions"
  />

  <v-file-input
    v-else-if="field.type === 'attachment'"
    :label="field.label"
    :hint="field.help"
    :loading="uploading"
    @update:model-value="upload"
  />

  <v-text-field
    v-else
    v-model="value"
    :label="field.label"
    :hint="field.help"
    :rules="rules"
    :type="inputType"
    :suffix="suffix"
  />
</template>
