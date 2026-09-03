<script setup>
import { ref, computed } from 'vue'
import { entityOf, recordsChanged } from '../records.js'
import RecordDialog from './RecordDialog.vue'

// A `create` block: the prominent way to add a record, placed where the screen wants it rather than
// tucked into a table's own toolbar. The dialog is the same one a table opens, so a record made
// here and a record made there go through one form and one set of rules.
const props = defineProps({
  entity: String,
  label: String,
  icon: String,
  style: { type: String, default: 'primary' },
})

const creating = ref(false)
const definition = computed(() => entityOf(props.entity))
const colors = { primary: 'primary', danger: 'error', neutral: undefined }

function saved() {
  creating.value = false
  recordsChanged(props.entity)
}
</script>

<template>
  <div>
    <v-btn
      :color="colors[style]"
      :prepend-icon="icon ? 'mdi-' + icon : 'mdi-plus'"
      @click="creating = true"
    >
      {{ label || 'New ' + (definition?.label || '') }}
    </v-btn>

    <RecordDialog
      v-if="creating"
      :entity="entity"
      :record="{}"
      @close="creating = false"
      @saved="saved"
    />
  </div>
</template>
