<script setup>
import { ref, computed, watch, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from '../api.js'
import { toast } from '../records.js'
import { isAdministrator } from '../session.js'
import PageShell from '../blocks/PageShell.vue'
import EmptyState from '../blocks/EmptyState.vue'

// People, teams and the organisations outside this one.
//
// This screen used to be a read-only table over three of the five directory entities, with no way
// to add anybody, change anything or even see the other two. The API was never the limitation —
// `DirectoryGateway` has answered an administrator with full access since it was written, and the
// controllers behind it are ordinary record controllers with POST, PATCH and DELETE. The screen
// simply did not ask.
//
// The directory is what every reference picker in the application reads, so a directory nobody can
// edit is an application whose people can never change.

const { t } = useI18n()

/**
 * The five entities, and what each one is.
 *
 * <p>Described here rather than read from a manifest because these belong to the RUNTIME, not to
 * the definition — an application says `targetApp: "platform"` and points at a directory it assumes
 * exists, so there is nothing generated to read the shape from. Showing `full_name` as a column
 * heading is still worse than showing "Name".</p>
 */
const tabs = [
  {
    key: 'person',
    icon: 'mdi-account-outline',
    display: 'full_name',
    columns: ['full_name', 'email', 'department', 'location', 'employment_status'],
    fields: [
      { key: 'full_name', type: 'text', required: true },
      { key: 'email', type: 'email' },
      { key: 'department', type: 'reference', target: 'department' },
      { key: 'manager', type: 'reference', target: 'person' },
      { key: 'location', type: 'text' },
      { key: 'hire_date', type: 'date' },
      {
        key: 'employment_status',
        type: 'select',
        options: ['active', 'inactive', 'left'],
      },
    ],
  },
  {
    key: 'department',
    icon: 'mdi-sitemap-outline',
    display: 'name',
    columns: ['name', 'handle', 'parent', 'lead'],
    fields: [
      { key: 'name', type: 'text', required: true },
      { key: 'handle', type: 'text' },
      { key: 'parent', type: 'reference', target: 'department' },
      { key: 'lead', type: 'reference', target: 'person' },
    ],
  },
  {
    key: 'group',
    icon: 'mdi-account-multiple-outline',
    display: 'name',
    columns: ['name', 'handle', 'group_type', 'description'],
    fields: [
      { key: 'name', type: 'text', required: true },
      { key: 'handle', type: 'text' },
      { key: 'group_type', type: 'text' },
      { key: 'parent', type: 'reference', target: 'group' },
      { key: 'description', type: 'longtext' },
    ],
  },
  {
    key: 'organization',
    icon: 'mdi-domain',
    display: 'name',
    columns: ['name', 'status', 'industry', 'city', 'country'],
    fields: [
      { key: 'name', type: 'text', required: true },
      { key: 'status', type: 'select', options: ['prospect', 'active', 'former'] },
      { key: 'industry', type: 'text' },
      { key: 'website', type: 'url' },
      { key: 'email', type: 'email' },
      { key: 'phone', type: 'text' },
      { key: 'street', type: 'text' },
      { key: 'postcode', type: 'text' },
      { key: 'city', type: 'text' },
      { key: 'country', type: 'text' },
      { key: 'notes', type: 'longtext' },
    ],
  },
  {
    key: 'contact',
    icon: 'mdi-card-account-details-outline',
    display: 'full_name',
    columns: ['full_name', 'organization', 'job_title', 'email', 'phone'],
    fields: [
      { key: 'full_name', type: 'text', required: true },
      { key: 'organization', type: 'reference', target: 'organization' },
      { key: 'job_title', type: 'text' },
      { key: 'email', type: 'email' },
      { key: 'phone', type: 'text' },
      { key: 'mobile', type: 'text' },
      { key: 'is_primary', type: 'boolean' },
      { key: 'notes', type: 'longtext' },
    ],
  },
]

const tab = ref('person')
const rows = ref([])
const loading = ref(false)
const error = ref(null)
const search = ref('')

const editing = ref(null)
const saving = ref(false)
const formError = ref(null)
const confirming = ref(null)

// Referenced rows, so a department column reads as a name rather than as a uuid. Cached per entity
// for the life of the page — a form with three pickers onto `person` asks once.
const references = ref({})

const current = computed(() => tabs.find((one) => one.key === tab.value))
const editable = computed(() => isAdministrator())

const label = (key) => t(`directory.field.${key}`)

const visible = computed(() => {
  const needle = search.value.trim().toLowerCase()
  if (!needle) return rows.value
  return rows.value.filter((row) =>
    current.value.columns.some((column) =>
      String(display(row, column) ?? '').toLowerCase().includes(needle)))
})

function display(row, key) {
  const field = current.value.fields.find((f) => f.key === key)
  const value = row[key]
  if (value === null || value === undefined || value === '') return ''
  if (field?.type === 'reference') return references.value[field.target]?.[value] ?? value
  if (field?.type === 'boolean') return value ? '✓' : '—'
  if (field?.type === 'select') return t(`directory.value.${value}`, value)
  return value
}

async function referenceRows(entity) {
  if (references.value[entity]) return
  try {
    const page = await api.get(`/api/directory/${entity}?take=500`)
    references.value = {
      ...references.value,
      [entity]: Object.fromEntries(
        (page?.items ?? []).map((r) => [r.id, r.full_name || r.name || r.id])),
    }
  } catch {
    // A picker that cannot load its options is an empty picker, which is honest. It must not stop
    // the table behind it from rendering.
    references.value = { ...references.value, [entity]: {} }
  }
}

function options(field) {
  const map = references.value[field.target] ?? {}
  return Object.entries(map).map(([value, title]) => ({ value, title }))
}

async function load() {
  loading.value = true
  error.value = null
  try {
    const page = await api.get(`/api/directory/${tab.value}?take=200`)
    rows.value = page?.items ?? []
    await Promise.all(
      [...new Set(current.value.fields.filter((f) => f.type === 'reference').map((f) => f.target))]
        .map(referenceRows))
  } catch (failure) {
    error.value = failure.message
    rows.value = []
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch(tab, () => { search.value = ''; load() })

function add() {
  editing.value = { id: null }
  formError.value = null
}

function edit(row) {
  if (!editable.value) return
  editing.value = { ...row }
  formError.value = null
}

async function save() {
  saving.value = true
  formError.value = null
  const body = {}
  for (const field of current.value.fields) {
    const value = editing.value[field.key]
    body[field.key] = value === '' ? null : value
  }
  try {
    if (editing.value.id) {
      await api.patch(`/api/directory/${tab.value}/${encodeURIComponent(editing.value.id)}`, body)
    } else {
      await api.post(`/api/directory/${tab.value}`, body)
    }
    editing.value = null
    // The picker cache is now stale — a department added here has to appear in the next form that
    // points at one.
    references.value = {}
    toast(t('directory.saved'), 'success')
    await load()
  } catch (failure) {
    formError.value = failure.message
  } finally {
    saving.value = false
  }
}

async function remove(row) {
  try {
    await api.delete(`/api/directory/${tab.value}/${encodeURIComponent(row.id)}`)
    confirming.value = null
    references.value = {}
    toast(t('directory.deleted'), 'success')
    await load()
  } catch (failure) {
    toast(failure.message, 'error')
  }
}
</script>

<template>
  <PageShell :title="t('directory.title')" :subtitle="t('directory.subtitle')">
    <template #actions>
      <v-btn
        v-if="editable"
        color="primary"
        variant="flat"
        prepend-icon="mdi-plus"
        @click="add"
      >
        {{ t(`directory.new.${tab}`) }}
      </v-btn>
    </template>

    <v-card>
      <v-tabs v-model="tab" show-arrows>
        <v-tab v-for="one in tabs" :key="one.key" :value="one.key" :prepend-icon="one.icon">
          {{ t(`directory.entity.${one.key}`) }}
        </v-tab>
      </v-tabs>

      <v-divider />

      <v-card-text class="pb-0 d-flex align-center ga-3 flex-wrap">
        <v-text-field
          v-model="search"
          :placeholder="t('common.search')"
          prepend-inner-icon="mdi-magnify"
          density="compact"
          clearable
          hide-details
          style="max-width: 320px"
        />
        <v-chip v-if="!loading" size="small" variant="tonal">{{ visible.length }}</v-chip>
        <v-spacer />
        <!-- Said once, plainly, rather than leaving somebody to discover it by clicking a row and
             watching nothing happen. -->
        <span v-if="!editable" class="text-caption text-medium-emphasis">
          <v-icon icon="mdi-information-outline" size="14" class="mr-1" />{{ t('directory.readOnly') }}
        </span>
      </v-card-text>

      <v-alert v-if="error" type="error" class="ma-4">
        {{ error }}
        <template #append>
          <v-btn size="small" @click="load">{{ t('common.retry') }}</v-btn>
        </template>
      </v-alert>

      <v-skeleton-loader v-else-if="loading" type="table" />

      <div v-else-if="visible.length" class="cd-table-scroll mt-3">
        <v-table :hover="editable">
          <thead>
            <tr>
              <th v-for="column in current.columns" :key="column">{{ label(column) }}</th>
              <th v-if="editable" class="cd-row-actions" />
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="row in visible"
              :key="row.id"
              :style="editable ? 'cursor: pointer' : ''"
              @click="edit(row)"
            >
              <td v-for="column in current.columns" :key="column">
                <span v-if="display(row, column)">{{ display(row, column) }}</span>
                <span v-else class="text-disabled">—</span>
              </td>
              <td v-if="editable" class="cd-row-actions text-right" @click.stop>
                <div class="cd-hover-actions d-inline-flex">
                  <v-btn icon="mdi-pencil-outline" size="small" @click="edit(row)" />
                  <v-btn icon="mdi-delete-outline" size="small" @click="confirming = row" />
                </div>
              </td>
            </tr>
          </tbody>
        </v-table>
      </div>

      <EmptyState
        v-else
        :icon="current.icon"
        :title="search ? t('common.noMatches') : t(`directory.empty.${tab}`)"
      >
        <v-btn v-if="editable && !search" variant="tonal" prepend-icon="mdi-plus" @click="add">
          {{ t(`directory.new.${tab}`) }}
        </v-btn>
      </EmptyState>
    </v-card>

    <v-dialog :model-value="!!editing" max-width="560" scrollable @update:model-value="editing = null">
      <v-card v-if="editing">
        <v-card-title>
          {{ editing.id ? t(`directory.edit.${tab}`) : t(`directory.new.${tab}`) }}
        </v-card-title>
        <v-card-text class="d-flex flex-column ga-4">
          <template v-for="field in current.fields" :key="field.key">
            <v-textarea
              v-if="field.type === 'longtext'"
              v-model="editing[field.key]"
              :label="label(field.key)"
              rows="2"
              auto-grow
            />
            <v-checkbox
              v-else-if="field.type === 'boolean'"
              v-model="editing[field.key]"
              :label="label(field.key)"
            />
            <v-select
              v-else-if="field.type === 'select'"
              v-model="editing[field.key]"
              :label="label(field.key)"
              :items="field.options.map((value) => ({ value, title: t(`directory.value.${value}`, value) }))"
              clearable
            />
            <v-autocomplete
              v-else-if="field.type === 'reference'"
              v-model="editing[field.key]"
              :label="label(field.key)"
              :items="options(field)"
              clearable
              @update:menu="referenceRows(field.target)"
            />
            <v-text-field
              v-else
              v-model="editing[field.key]"
              :label="label(field.key)"
              :type="field.type === 'date' ? 'date' : field.type === 'email' ? 'email' : 'text'"
            />
          </template>

          <v-alert v-if="formError" type="error">{{ formError }}</v-alert>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="editing = null">{{ t('common.cancel') }}</v-btn>
          <v-btn color="primary" variant="flat" :loading="saving" @click="save">
            {{ t('common.save') }}
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <v-dialog :model-value="!!confirming" max-width="440" @update:model-value="confirming = null">
      <v-card v-if="confirming">
        <v-card-title>{{ t('directory.deleteTitle') }}</v-card-title>
        <v-card-text class="text-medium-emphasis">{{ t('directory.deleteBody') }}</v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="confirming = null">{{ t('common.cancel') }}</v-btn>
          <v-btn color="error" variant="flat" @click="remove(confirming)">{{ t('common.delete') }}</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
  </PageShell>
</template>
