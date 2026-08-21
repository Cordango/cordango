<script setup>
import { ref, onMounted, onUnmounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { session, signOut } from './session.js'
import { app } from './app.js'
import NotificationBell from './blocks/NotificationBell.vue'

const { t, locale } = useI18n()
const router = useRouter()

const drawer = ref(true)
const toast = ref(null)

function onToast(event) {
  toast.value = event.detail
}

onMounted(() => window.addEventListener('{{AppKey}}:toast', onToast))
onUnmounted(() => window.removeEventListener('{{AppKey}}:toast', onToast))

function setLocale(value) {
  locale.value = value
  localStorage.setItem('{{AppKey}}.locale', value)
}

async function leave() {
  await signOut()
  router.push({ name: 'login' })
}
</script>

<template>
  <v-app>
    <template v-if="session.authenticated">
      <v-navigation-drawer v-model="drawer">
        <v-list nav>
          <v-list-item :to="{ name: 'home' }" prepend-icon="mdi-home" :title="t('nav.home')" />

          <!-- The definition's own screens, in the order it lists them. Their labels are the
               business's words and pass through untranslated. -->
          <v-list-item
            v-for="page in app.pages"
            :key="page.key"
            :to="page.route"
            :prepend-icon="page.icon ? `mdi-${page.icon}` : 'mdi-file-outline'"
            :title="page.label"
          />

          <v-divider class="my-2" />
          <v-list-item :to="{ name: 'directory' }" prepend-icon="mdi-account-group" :title="t('nav.directory')" />
        </v-list>
      </v-navigation-drawer>

      <v-app-bar>
        <v-app-bar-nav-icon @click="drawer = !drawer" />
        <v-app-bar-title>{{ t('app.name') }}</v-app-bar-title>
        <v-spacer />
        <NotificationBell />
        <v-btn size="small" variant="text" @click="setLocale(locale === 'en' ? 'de' : 'en')">
          {{ locale === 'en' ? 'DE' : 'EN' }}
        </v-btn>
        <v-btn variant="text" @click="leave">{{ t('nav.signOut') }}</v-btn>
      </v-app-bar>
    </template>

    <v-main>
      <router-view />
    </v-main>

    <v-snackbar :model-value="Boolean(toast)" timeout="4000" @update:model-value="toast = null">
      {{ toast }}
    </v-snackbar>
  </v-app>
</template>
