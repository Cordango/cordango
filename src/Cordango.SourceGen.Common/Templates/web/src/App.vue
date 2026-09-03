<script setup>
import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { useDisplay } from 'vuetify'
import { session, isAdministrator } from './session.js'
import { app } from './app.js'
import { TOAST_EVENT } from './records.js'
import AccountMenu from './blocks/AccountMenu.vue'
import NotificationBell from './blocks/NotificationBell.vue'

const { t } = useI18n()
const route = useRoute()
const { mobile } = useDisplay()

// Two different gestures behind one button. On a phone the drawer is an overlay that is either
// there or not; on a desktop it is permanent and the button narrows it to a rail. Treating them as
// the same control is what produces the drawer that vanishes on desktop and never comes back.
const drawer = ref(!mobile.value)
const rail = ref(false)

watch(mobile, (isMobile) => {
  drawer.value = !isMobile
  if (isMobile) rail.value = false
})

function toggleNavigation() {
  if (mobile.value) drawer.value = !drawer.value
  else rail.value = !rail.value
}

// Closing on navigation matters only where the drawer covers the page. On a desktop it would shut
// the sidebar every time somebody used it.
watch(() => route.fullPath, () => {
  if (mobile.value) drawer.value = false
})

const toast = ref(null)

function onToast(event) {
  const detail = event.detail
  toast.value = typeof detail === 'string' ? { message: detail, tone: 'info' } : detail
}

onMounted(() => window.addEventListener(TOAST_EVENT, onToast))
onUnmounted(() => window.removeEventListener(TOAST_EVENT, onToast))

const toastColour = computed(() => ({
  error: 'error',
  success: 'success',
  warning: 'warning',
}[toast.value?.tone] ?? 'surface-variant'))

// What the app bar calls the page.
//
// A page's `route` in app.js is a PATH STRING — "/my-tasks" — not a route object, which is also why
// `:to="page.route"` works on the list items above. Reading `.name` and `.path` off it therefore
// matched nothing, and every screen the definition contributed sat under a blank app bar.
//
// The definition's own labels pass through untranslated: those are the business's words. Everything
// else here is the shell's and is translated.
const heading = computed(() => {
  const page = app.pages.find((p) => p.route === route.path)
  if (page) return page.label
  return {
    home: t('nav.home'),
    directory: t('nav.directory'),
    'access-keys': t('nav.keys'),
    profile: t('nav.profile'),
    'admin-users': t('admin.title'),
  }[route.name] ?? ''
})
</script>

<template>
  <v-app>
    <template v-if="session.authenticated">
      <v-navigation-drawer
        v-model="drawer"
        :rail="rail && !mobile"
        :permanent="!mobile"
        :temporary="mobile"
        width="248"
        class="cd-rail"
      >
        <div class="d-flex align-center ga-3 px-4 py-4">
          <v-avatar color="primary" size="32" rounded="md">
            <v-icon icon="mdi-hexagon-slice-6" size="20" />
          </v-avatar>
          <span v-if="!rail || mobile" class="cd-brand text-body-1 text-truncate">
            {{ t('app.name') }}
          </span>
        </div>

        <v-divider />

        <v-list nav class="pa-2">
          <v-list-item
            :to="{ name: 'home' }"
            prepend-icon="mdi-view-dashboard-outline"
            :title="t('nav.home')"
            rounded="md"
          />

          <!-- The definition's own screens, in the order it lists them. Their labels are the
               business's words and pass through untranslated. -->
          <v-list-item
            v-for="page in app.pages"
            :key="page.key"
            :to="page.route"
            :prepend-icon="page.icon ? `mdi-${page.icon}` : 'mdi-file-outline'"
            :title="page.label"
            rounded="md"
          />
        </v-list>

        <!-- Everything the RUNTIME provides, kept apart from what the application is about. A
             person looking for their tasks should not have to read past "Access keys" to find
             them. -->
        <template #append>
          <v-divider />
          <v-list nav class="pa-2">
            <v-list-item
              :to="{ name: 'directory' }"
              prepend-icon="mdi-account-group-outline"
              :title="t('nav.directory')"
              rounded="md"
            />
            <v-list-item
              v-if="isAdministrator()"
              :to="{ name: 'admin-users' }"
              prepend-icon="mdi-shield-account-outline"
              :title="t('admin.title')"
              rounded="md"
            />
            <v-list-item
              :to="{ name: 'access-keys' }"
              prepend-icon="mdi-key-variant"
              :title="t('nav.keys')"
              rounded="md"
            />
          </v-list>
        </template>
      </v-navigation-drawer>

      <v-app-bar height="60">
        <v-btn
          icon
          :aria-label="t('nav.toggleNavigation')"
          @click="toggleNavigation"
        >
          <v-icon icon="mdi-menu" />
        </v-btn>

        <span class="text-subtitle-1 font-weight-medium text-truncate">{{ heading }}</span>

        <v-spacer />

        <NotificationBell />
        <AccountMenu />
      </v-app-bar>
    </template>

    <v-main>
      <router-view />
    </v-main>

    <v-snackbar
      :model-value="Boolean(toast)"
      :color="toastColour"
      timeout="4500"
      location="bottom right"
      @update:model-value="toast = null"
    >
      {{ toast?.message }}
      <template #actions>
        <v-btn icon="mdi-close" size="small" @click="toast = null" />
      </template>
    </v-snackbar>
  </v-app>
</template>
