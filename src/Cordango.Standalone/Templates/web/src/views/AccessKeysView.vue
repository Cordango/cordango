<script setup>
import { ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from '../api.js'

const { t, locale } = useI18n()

// Formatted against the shell's active language rather than the browser's, so a person who chose
// German sees German dates even on a machine set to something else.
function formatDateTime(value) {
  if (!value) return '—'
  return new Intl.DateTimeFormat(locale.value, { dateStyle: 'medium', timeStyle: 'short' })
    .format(new Date(value))
}

const keys = ref([])
const loading = ref(false)
const error = ref(null)

const creating = ref(false)
const label = ref('')
const expires = ref('')

// The secret, for exactly as long as this page is open. Never stored, never re-fetchable — the
// server keeps only a hash, so this variable is the one and only copy after the response is read.
const minted = ref(null)

async function load() {
  loading.value = true
  error.value = null
  try {
    keys.value = await api.get('/api/account/keys')
  } catch (failure) {
    error.value = failure.message
  } finally {
    loading.value = false
  }
}

async function mint() {
  error.value = null
  creating.value = true
  try {
    minted.value = await api.post('/api/account/keys', {
      label: label.value,
      expires: expires.value ? new Date(expires.value).toISOString() : null,
    })
    label.value = ''
    expires.value = ''
    await load()
  } catch (failure) {
    error.value = failure.message
  } finally {
    creating.value = false
  }
}

async function revoke(key) {
  error.value = null
  try {
    await api.delete(`/api/account/keys/${key.id}`)
    if (minted.value?.id === key.id) minted.value = null
    await load()
  } catch (failure) {
    error.value = failure.message
  }
}

function copy() {
  navigator.clipboard?.writeText(minted.value.token)
}

onMounted(load)
</script>

<template>
  <v-container>
    <h1 class="text-h5 mb-2">{{ t('keys.title') }}</h1>
    <p class="text-body-2 text-medium-emphasis mb-6">{{ t('keys.intro') }}</p>

    <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}</v-alert>

    <!-- Shown once and never again. The server stores only a hash, so there is no second chance to
         copy this and no endpoint that could hand it back. -->
    <v-alert v-if="minted" type="success" variant="tonal" class="mb-6">
      <div class="text-subtitle-2 mb-2">{{ t('keys.copyNow') }}</div>
      <v-textarea
        :model-value="minted.token"
        readonly
        rows="2"
        variant="outlined"
        density="compact"
        hide-details
        class="mb-2"
      />
      <v-btn size="small" variant="tonal" prepend-icon="mdi-content-copy" @click="copy">
        {{ t('keys.copy') }}
      </v-btn>
    </v-alert>

    <v-card class="mb-6">
      <v-card-title class="text-subtitle-1">{{ t('keys.newKey') }}</v-card-title>
      <v-card-text>
        <v-row>
          <v-col cols="12" md="6">
            <v-text-field
              v-model="label"
              :label="t('keys.label')"
              :hint="t('keys.labelHint')"
              persistent-hint
              maxlength="80"
            />
          </v-col>
          <v-col cols="12" md="6">
            <v-text-field
              v-model="expires"
              type="date"
              :label="t('keys.expires')"
              :hint="t('keys.expiresHint')"
              persistent-hint
            />
          </v-col>
        </v-row>
      </v-card-text>
      <v-card-actions>
        <v-btn
          color="primary"
          variant="flat"
          :loading="creating"
          :disabled="!label.trim()"
          @click="mint"
        >
          {{ t('keys.create') }}
        </v-btn>
      </v-card-actions>
    </v-card>

    <v-card>
      <v-table v-if="keys.length">
        <thead>
          <tr>
            <th>{{ t('keys.label') }}</th>
            <th>{{ t('keys.created') }}</th>
            <th>{{ t('keys.lastUsed') }}</th>
            <th>{{ t('keys.expires') }}</th>
            <th />
          </tr>
        </thead>
        <tbody>
          <tr v-for="key in keys" :key="key.id">
            <td>{{ key.label }}</td>
            <td>{{ formatDateTime(key.created) }}</td>
            <td>{{ key.lastUsed ? formatDateTime(key.lastUsed) : t('keys.never') }}</td>
            <td>{{ formatDateTime(key.expires) }}</td>
            <td class="text-right">
              <v-btn size="small" variant="text" color="error" @click="revoke(key)">
                {{ t('keys.revoke') }}
              </v-btn>
            </td>
          </tr>
        </tbody>
      </v-table>

      <v-card-text v-else-if="!loading" class="text-medium-emphasis">
        {{ t('keys.none') }}
      </v-card-text>

      <v-card-text v-else>
        <v-progress-linear indeterminate />
      </v-card-text>
    </v-card>
  </v-container>
</template>
