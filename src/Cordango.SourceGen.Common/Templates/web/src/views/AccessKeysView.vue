<script setup>
import { ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from '../api.js'
import PageShell from '../blocks/PageShell.vue'
import EmptyState from '../blocks/EmptyState.vue'

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
  <PageShell :title="t('keys.title')" :subtitle="t('keys.intro')">
    <v-alert v-if="error" type="error">{{ error }}</v-alert>

    <!-- Shown once and never again. The server stores only a hash, so there is no second chance to
         copy this and no endpoint that could hand it back. -->
    <v-alert v-if="minted" type="success" prominent>
      <div class="text-subtitle-2 mb-2">{{ t('keys.copyNow') }}</div>
      <v-textarea
        :model-value="minted.token"
        readonly
        rows="2"
        density="compact"
        hide-details
        class="mb-3"
      />
      <v-btn size="small" variant="tonal" prepend-icon="mdi-content-copy" @click="copy">
        {{ t('keys.copy') }}
      </v-btn>
    </v-alert>

    <v-card>
      <v-card-title>{{ t('keys.newKey') }}</v-card-title>
      <v-card-text class="pt-0">
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
      <v-card-actions class="px-4 pb-4">
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
      <div v-if="keys.length" class="cd-table-scroll">
        <v-table hover>
          <thead>
            <tr>
              <th>{{ t('keys.label') }}</th>
              <th>{{ t('keys.created') }}</th>
              <th>{{ t('keys.lastUsed') }}</th>
              <th>{{ t('keys.expires') }}</th>
              <th class="cd-row-actions" />
            </tr>
          </thead>
          <tbody>
            <tr v-for="key in keys" :key="key.id">
              <td class="font-weight-medium">{{ key.label }}</td>
              <td class="text-medium-emphasis">{{ formatDateTime(key.created) }}</td>
              <td class="text-medium-emphasis">
                {{ key.lastUsed ? formatDateTime(key.lastUsed) : t('keys.never') }}
              </td>
              <td class="text-medium-emphasis">{{ formatDateTime(key.expires) }}</td>
              <td class="cd-row-actions text-right">
                <v-btn size="small" color="error" @click="revoke(key)">
                  {{ t('keys.revoke') }}
                </v-btn>
              </td>
            </tr>
          </tbody>
        </v-table>
      </div>

      <v-card-text v-else-if="loading">
        <v-skeleton-loader type="table-row@2" />
      </v-card-text>

      <EmptyState v-else icon="mdi-key-outline" :title="t('keys.none')" />
    </v-card>
  </PageShell>
</template>
