<script setup>
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { signIn } from '../session.js'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()

const email = ref('')
const password = ref('')
const remember = ref(false)
const busy = ref(false)
const error = ref(null)

async function submit() {
  busy.value = true
  error.value = null
  try {
    await signIn(email.value, password.value, remember.value)
    router.push(route.query.redirect || { name: 'home' })
  } catch (failure) {
    // The server's message, because it already says the right thing in the reader's language and
    // distinguishes "wrong password" from "locked out" without saying which accounts exist.
    error.value = failure.message || t('auth.failed')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <v-container class="d-flex justify-center align-center" style="min-height: 80vh">
    <v-card width="420" class="pa-4">
      <v-card-title>{{ t('auth.title') }}</v-card-title>
      <v-card-text>
        <v-form @submit.prevent="submit">
          <v-text-field
            v-model="email"
            :label="t('auth.email')"
            type="email"
            autocomplete="username"
            autofocus
          />
          <v-text-field
            v-model="password"
            :label="t('auth.password')"
            type="password"
            autocomplete="current-password"
          />
          <v-checkbox v-model="remember" :label="t('auth.remember')" density="compact" />
          <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}</v-alert>
          <v-btn type="submit" color="primary" block :loading="busy">{{ t('auth.submit') }}</v-btn>
        </v-form>
      </v-card-text>
    </v-card>
  </v-container>
</template>
