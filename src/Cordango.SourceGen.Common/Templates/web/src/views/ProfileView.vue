<script setup>
import { ref, computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { session, changePassword } from '../session.js'
import { toast } from '../records.js'
import PageShell from '../blocks/PageShell.vue'

// Your own account: who this application thinks you are, and the one thing about it you can change
// without an administrator.
//
// The change-password endpoint had been here since the first release with nothing calling it, which
// is the same as not having it — somebody handed a password could read the API reference and use
// curl, or they could keep the password.

const { t } = useI18n()
const router = useRouter()

const current = ref('')
const next = ref('')
const repeat = ref('')
const busy = ref(false)
const error = ref(null)

const mismatch = computed(() => repeat.value.length > 0 && next.value !== repeat.value)
const ready = computed(() => current.value && next.value && next.value === repeat.value)

// The account was created BY somebody else, so the person is being asked to replace a password two
// people currently know. Saying so is the difference between a form that looks like nagging and one
// that looks like a reason.
const forced = computed(() => session.mustChangePassword)

async function submit() {
  if (!ready.value) return
  busy.value = true
  error.value = null
  try {
    await changePassword(current.value, next.value)
    current.value = ''
    next.value = ''
    repeat.value = ''
    toast(t('profile.changed'), 'success')
    if (forced.value === false) router.push({ name: 'home' })
  } catch (failure) {
    // The server's own words: Identity names the rule that was broken, and "Passwords must be at
    // least 12 characters" is something somebody can act on.
    error.value = failure.message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <PageShell :title="t('nav.profile')" :subtitle="t('profile.subtitle')">
    <v-alert
      v-if="forced"
      type="warning"
      prominent
      :title="t('profile.mustChange.title')"
      :text="t('profile.mustChange.body')"
    />

    <div class="d-flex flex-wrap ga-4 align-start">
      <v-card class="flex-grow-1" min-width="280" style="flex-basis: 320px">
        <v-card-title>{{ t('profile.account') }}</v-card-title>
        <v-list density="comfortable" class="pb-4">
          <v-list-item :title="session.displayName || '—'" :subtitle="t('profile.name')" />
          <v-list-item :title="session.email" :subtitle="t('profile.email')" />
          <v-list-item :subtitle="t('profile.roles')">
            <template #title>
              <div class="d-flex flex-wrap ga-1 mt-1">
                <v-chip
                  v-for="role in session.roles"
                  :key="role"
                  size="x-small"
                  variant="tonal"
                  :color="role === 'Administrator' ? 'primary' : undefined"
                >
                  {{ role }}
                </v-chip>
                <span v-if="!session.roles.length" class="text-medium-emphasis">—</span>
              </div>
            </template>
          </v-list-item>
        </v-list>
      </v-card>

      <v-card class="flex-grow-1" min-width="300" style="flex-basis: 420px">
        <v-card-title>{{ t('profile.password') }}</v-card-title>
        <v-card-text>
          <v-form class="d-flex flex-column ga-4" @submit.prevent="submit">
            <v-text-field
              v-model="current"
              :label="t('profile.current')"
              type="password"
              autocomplete="current-password"
            />
            <v-text-field
              v-model="next"
              :label="t('profile.new')"
              type="password"
              autocomplete="new-password"
            />
            <v-text-field
              v-model="repeat"
              :label="t('profile.repeat')"
              type="password"
              autocomplete="new-password"
              :error="mismatch"
              :error-messages="mismatch ? t('setup.mismatch') : []"
            />
            <v-alert v-if="error" type="error">{{ error }}</v-alert>
            <v-btn
              type="submit"
              color="primary"
              variant="flat"
              :disabled="!ready"
              :loading="busy"
            >
              {{ t('profile.change') }}
            </v-btn>
          </v-form>
        </v-card-text>
      </v-card>
    </div>
  </PageShell>
</template>
