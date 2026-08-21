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
  <v-container class="d-flex justify-center align-center" style="min-height: 80vh">
    <v-card width="480" class="pa-4">
      <v-card-title>{{ t('setup.title') }}</v-card-title>
      <v-card-subtitle class="text-wrap">{{ t('setup.intro') }}</v-card-subtitle>
      <v-card-text>
        <v-form @submit.prevent="submit">
          <v-text-field
            v-model="email"
            :label="t('setup.email')"
            type="email"
            autocomplete="username"
            autofocus
          />
          <v-text-field
            v-model="displayName"
            :label="t('setup.displayName')"
            :hint="t('setup.displayNameHint')"
            persistent-hint
            autocomplete="name"
            class="mb-2"
          />
          <v-text-field
            v-model="password"
            :label="t('setup.password')"
            type="password"
            autocomplete="new-password"
            :error="tooShort"
            :error-messages="tooShort ? t('setup.short', { count: MINIMUM }) : []"
          />
          <v-text-field
            v-model="confirm"
            :label="t('setup.confirm')"
            type="password"
            autocomplete="new-password"
            :error="mismatch"
            :error-messages="mismatch ? t('setup.mismatch') : []"
          />
          <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}</v-alert>
          <v-btn type="submit" color="primary" block :loading="busy" :disabled="!ready">
            {{ t('setup.submit') }}
          </v-btn>
        </v-form>
      </v-card-text>
    </v-card>
  </v-container>
</template>
