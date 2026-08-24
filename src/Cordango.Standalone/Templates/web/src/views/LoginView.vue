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
const reveal = ref(false)
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
  <div class="d-flex align-center justify-center pa-4" style="min-height: 100vh">
    <div style="width: 100%; max-width: 400px">
      <div class="text-center mb-6">
        <v-avatar color="primary" size="52" rounded="lg" class="mb-4">
          <v-icon icon="mdi-hexagon-slice-6" size="28" />
        </v-avatar>
        <h1 class="text-h5 font-weight-bold">{{ t('app.name') }}</h1>
        <p class="text-body-2 text-medium-emphasis mt-1 mb-0">{{ t('auth.intro') }}</p>
      </div>

      <v-card>
        <v-card-text class="pa-6">
          <v-form class="d-flex flex-column ga-4" @submit.prevent="submit">
            <v-text-field
              v-model="email"
              :label="t('auth.email')"
              type="email"
              autocomplete="username"
              prepend-inner-icon="mdi-email-outline"
              autofocus
            />
            <v-text-field
              v-model="password"
              :label="t('auth.password')"
              :type="reveal ? 'text' : 'password'"
              autocomplete="current-password"
              prepend-inner-icon="mdi-lock-outline"
              :append-inner-icon="reveal ? 'mdi-eye-off-outline' : 'mdi-eye-outline'"
              @click:append-inner="reveal = !reveal"
            />
            <v-checkbox v-model="remember" :label="t('auth.remember')" density="compact" />
            <v-alert v-if="error" type="error">{{ error }}</v-alert>
            <v-btn type="submit" color="primary" variant="flat" size="large" block :loading="busy">
              {{ t('auth.submit') }}
            </v-btn>
          </v-form>
        </v-card-text>
      </v-card>
    </div>
  </div>
</template>
