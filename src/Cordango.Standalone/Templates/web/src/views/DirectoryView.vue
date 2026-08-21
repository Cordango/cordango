<script setup>
import { ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from '../api.js'

// Column labels for the built-in directory. Its entities are the runtime's, not the definition's, so
// there is no manifest to read them from — but showing `full_name` as a heading is still worse than
// showing "Name".
const labels = {
  full_name: 'Name',
  email: 'Email',
  location: 'Location',
  employment_status: 'Status',
  name: 'Name',
  handle: 'Handle',
  city: 'City',
  country: 'Country',
  status: 'Status',
}

const { t } = useI18n()

const tab = ref('people')
const rows = ref([])
const loading = ref(false)
const error = ref(null)

const sources = {
  people: { path: '/api/directory/person', columns: ['full_name', 'email', 'location', 'employment_status'] },
  departments: { path: '/api/directory/department', columns: ['name', 'handle'] },
  organizations: { path: '/api/directory/organization', columns: ['name', 'city', 'country', 'status'] },
}

async function load() {
  loading.value = true
  error.value = null
  try {
    const page = await api.get(`${sources[tab.value].path}?take=200`)
    rows.value = page?.items ?? []
  } catch (failure) {
    error.value = failure.message
    rows.value = []
  } finally {
    loading.value = false
  }
}

onMounted(load)
</script>

<template>
  <v-container>
    <h1 class="text-h5 mb-4">{{ t('directory.title') }}</h1>

    <v-tabs v-model="tab" class="mb-4" @update:model-value="load">
      <v-tab value="people">{{ t('directory.people') }}</v-tab>
      <v-tab value="departments">{{ t('directory.departments') }}</v-tab>
      <v-tab value="organizations">{{ t('directory.organizations') }}</v-tab>
    </v-tabs>

    <v-alert v-if="error" type="error" variant="tonal" class="mb-4">
      {{ error }}
      <template #append>
        <v-btn size="small" variant="text" @click="load">{{ t('common.retry') }}</v-btn>
      </template>
    </v-alert>

    <v-skeleton-loader v-if="loading" type="table" />

    <v-table v-else-if="rows.length">
      <thead>
        <tr>
          <th v-for="column in sources[tab].columns" :key="column">{{ labels[column] || column }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in rows" :key="row.id">
          <td v-for="column in sources[tab].columns" :key="column">{{ row[column] }}</td>
        </tr>
      </tbody>
    </v-table>

    <v-alert v-else type="info" variant="tonal">{{ t('directory.empty') }}</v-alert>
  </v-container>
</template>
