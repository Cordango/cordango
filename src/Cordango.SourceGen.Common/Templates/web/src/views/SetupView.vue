<script setup>
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { completeSetup } from '../session.js'

// The first screen this application ever shows, and it shows it exactly once.
//
// Nothing here is a password anybody else chose. The alternative designs were a shipped default —
// the same on every copy of every application this toolchain produces — or one generated at startup
// and printed to the log, which asks somebody who opened a browser to go and read a container log.
// So the person who is going to use the account creates it, and from that moment the endpoint
// behind this form answers 409 to everybody.

const { t } = useI18n()
const router = useRouter()

const email = ref('')
const displayName = ref('')
const password = ref('')
const confirm = ref('')
const busy = ref(false)
const error = ref(null)

// Twelve, because that is what the server's Identity policy requires. Checking it here as well is
// not duplication for its own sake: it turns a round trip into instant feedback while somebody is
// still typing. The server stays the one that decides.
const MINIMUM = 12

const tooShort = computed(() => password.value.length > 0 && password.value.length < MINIMUM)
const mismatch = computed(() => confirm.value.length > 0 && confirm.value !== password.value)
const ready = computed(() =>
  email.value.includes('@') && password.value.length >= MINIMUM && confirm.value === password.value)

async function submit() {
  if (!ready.value) return

  busy.value = true
  error.value = null
  try {
    await completeSetup(email.value, password.value, displayName.value)
    router.push({ name: 'home' })
  } catch (failure) {
    // The server's sentence, which names the rule that was broken — "Passwords must have at least
    // one non-alphanumeric character" is something a person can act on, and "invalid" is not.
    error.value = failure.message || t('setup.failed')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="d-flex align-center justify-center pa-4" style="min-height: 100vh">
    <div style="width: 100%; max-width: 460px">
      <div class="text-center mb-6">
        <v-avatar color="primary" size="52" rounded="lg" class="mb-4">
          <v-icon icon="mdi-hexagon-slice-6" size="28" />
        </v-avatar>
        <h1 class="text-h5 font-weight-bold">{{ t('setup.title') }}</h1>
        <p class="text-body-2 text-medium-emphasis mt-2 mb-0">{{ t('setup.intro') }}</p>
      </div>

      <v-card>
        <v-card-text class="pa-6">
          <v-form class="d-flex flex-column ga-4" @submit.prevent="submit">
            <v-text-field
              v-model="email"
              :label="t('setup.email')"
              type="email"
              autocomplete="username"
              prepend-inner-icon="mdi-email-outline"
              autofocus
            />
            <v-text-field
              v-model="displayName"
              :label="t('setup.displayName')"
              :hint="t('setup.displayNameHint')"
              persistent-hint
              autocomplete="name"
              prepend-inner-icon="mdi-account-outline"
            />
            <v-text-field
              v-model="password"
              :label="t('setup.password')"
              type="password"
              autocomplete="new-password"
              prepend-inner-icon="mdi-lock-outline"
              :error="tooShort"
              :error-messages="tooShort ? t('setup.short', { count: MINIMUM }) : []"
            />
            <v-text-field
              v-model="confirm"
              :label="t('setup.confirm')"
              type="password"
              autocomplete="new-password"
              prepend-inner-icon="mdi-lock-check-outline"
              :error="mismatch"
              :error-messages="mismatch ? t('setup.mismatch') : []"
            />
            <v-alert v-if="error" type="error">{{ error }}</v-alert>
            <v-btn
              type="submit"
              color="primary"
              variant="flat"
              size="large"
              block
              :loading="busy"
              :disabled="!ready"
            >
              {{ t('setup.submit') }}
            </v-btn>
          </v-form>
        </v-card-text>
      </v-card>
    </div>
  </div>
</template>
