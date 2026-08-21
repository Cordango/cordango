<script setup>
import { ref, computed, onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { viewOf, entityOf, loadView, commandsOf } from '../records.js'
import { session } from '../session.js'
import FieldValue from './FieldValue.vue'
import RecordDialog from './RecordDialog.vue'
import CommandButton from './CommandButton.vue'

const props = defineProps({
  view: String,
  // Extra conditions the page adds on top of the view's own — a stat card's deep link, a filter bar.
  extraFilters: { type: Array, default: () => [] },
  create: { type: Boolean, default: true },
})

const router = useRouter()
const definition = computed(() => viewOf(props.view))
const entity = computed(() => entityOf(definition.value?.entity))

const rows = ref([])
const total = ref(0)
const loading = ref(true)
const error = ref(null)
const editing = ref(null)

const columns = computed(() => {
  const keys = definition.value?.config?.columns
    // No columns named means every field somebody could have entered. Not every field: the audit
    // stamps would push the ones people care about off the right of the screen.
    ?? entity.value?.fields.filter((f) => !f.system).slice(0, 6).map((f) => f.key)
    ?? []
  return keys.map((key) => entity.value?.fields.find((f) => f.key === key)).filter(Boolean)
})

const rowCommands = computed(() =>
  commandsOf(definition.value?.entity).filter((c) => (c.placements || []).includes('tableRow')))

async function load() {
  loading.value = true
  error.value = null
  try {
    const page = await loadView(definition.value, session, { extraFilters: props.extraFilters })
    rows.value = page?.items ?? []
    total.value = page?.total ?? 0
  } catch (failure) {
    error.value = failure.message
    rows.value = []
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch(() => props.extraFilters, load, { deep: true })

const open = (row) => router.push(`/record/${definition.value.entity}/${encodeURIComponent(row.id)}`)
</script>

<template>
  <v-card>
    <v-card-title class="d-flex align-center">
      <span class="text-subtitle-1">{{ definition?.label }}</span>
      <v-chip v-if="!loading" size="x-small" class="ml-2" variant="tonal">{{ total }}</v-chip>
      <v-spacer />
      <v-btn v-if="create" size="small" variant="tonal" prepend-icon="mdi-plus" @click="editing = {}">
        New
      </v-btn>
    </v-card-title>

    <v-alert v-if="error" type="error" variant="tonal" class="ma-4">{{ error }}</v-alert>
    <v-skeleton-loader v-else-if="loading" type="table" />

    <v-table v-else-if="rows.length" hover>
      <thead>
        <tr>
          <th v-for="field in columns" :key="field.key">{{ field.label }}</th>
          <th v-if="rowCommands.length" />
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in rows" :key="row.id" style="cursor: pointer" @click="open(row)">
          <td v-for="field in columns" :key="field.key">
            <FieldValue :field="field" :value="row[field.key]" />
          </td>
          <td v-if="rowCommands.length" class="text-right" @click.stop>
            <CommandButton
              v-for="command in rowCommands"
              :key="command.key"
              :entity="definition.entity"
              :record="row"
              :command="command"
              density="compact"
              @done="load"
            />
          </td>
        </tr>
      </tbody>
    </v-table>

    <v-card-text v-else class="text-medium-emphasis">Nothing here yet.</v-card-text>

    <RecordDialog
      v-if="editing"
      :entity="definition.entity"
      :record="editing"
      @close="editing = null"
      @saved="editing = null; load()"
    />
  </v-card>
</template>
