<script setup>
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { useTheme } from 'vuetify'
import { session, signOut, isAdministrator } from '../session.js'
import { THEME_CHOICES, storedTheme, rememberTheme } from '../theme.js'

// Who is signed in, and everything that is about THEM rather than about the application: their
// name, their roles, their password, how they want this to look and what language to read it in.
//
// It exists because the shell had none of this. A bar with a bare "Sign out" on it tells somebody
// neither which account they are using nor how to change anything about it — and on a system where
// one person may hold two accounts, "which account am I in" is a question with consequences.

const { t, locale } = useI18n()
const router = useRouter()
const theme = useTheme()

const initials = computed(() => {
  const name = session.displayName || session.email || ''
  const words = name.replace(/@.*$/, '').split(/[^\p{L}\p{N}]+/u).filter(Boolean)
  if (!words.length) return '?'
  return (words.length === 1 ? words[0].slice(0, 2) : words[0][0] + words[1][0]).toUpperCase()
})

const themeChoice = computed({
  get: () => storedTheme(),
  set: (choice) => {
    // Stored AND applied. Vuetify holds the live theme; localStorage is what survives a reload, and
    // theme.js reads it back when createVuetify runs.
    rememberTheme(choice)
    theme.change(choice)
  },
})

const themeIcons = {
  system: 'mdi-theme-light-dark',
  light: 'mdi-white-balance-sunny',
  dark: 'mdi-weather-night',
}

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
  <v-menu location="bottom end" :close-on-content-click="false" min-width="272">
    <template #activator="{ props }">
      <v-btn icon v-bind="props" :aria-label="t('account.menu')" class="ml-1">
        <v-avatar color="primary" size="34">
          <span class="text-caption font-weight-bold">{{ initials }}</span>
        </v-avatar>
      </v-btn>
    </template>

    <v-card>
      <div class="d-flex align-center ga-3 pa-4">
        <v-avatar color="primary" size="40">
          <span class="text-body-2 font-weight-bold">{{ initials }}</span>
        </v-avatar>
        <div class="min-width-0">
          <div class="text-body-2 font-weight-medium text-truncate">
            {{ session.displayName || session.email }}
          </div>
          <div class="text-caption text-medium-emphasis text-truncate">{{ session.email }}</div>
        </div>
      </div>

      <!-- Roles, shown rather than hidden. What somebody can and cannot do in this application is
           decided by these, and "why can I not see that screen" is otherwise unanswerable from
           inside the application. -->
      <div v-if="session.roles.length" class="px-4 pb-3 d-flex flex-wrap ga-1">
        <v-chip
          v-for="role in session.roles"
          :key="role"
          size="x-small"
          variant="tonal"
          :color="role === 'Administrator' ? 'primary' : undefined"
        >
          {{ role }}
        </v-chip>
      </div>

      <v-divider />

      <v-list density="compact" nav>
        <v-list-item
          :to="{ name: 'profile' }"
          prepend-icon="mdi-account-outline"
          :title="t('account.profile')"
        />
        <v-list-item
          v-if="isAdministrator()"
          :to="{ name: 'admin-users' }"
          prepend-icon="mdi-shield-account-outline"
          :title="t('admin.title')"
        />
      </v-list>

      <v-divider />

      <div class="px-4 py-3 d-flex flex-column ga-3">
        <div>
          <div class="text-caption text-medium-emphasis mb-1">{{ t('account.appearance') }}</div>
          <v-btn-toggle
            v-model="themeChoice"
            density="compact"
            variant="outlined"
            divided
            mandatory
            class="w-100"
          >
            <v-btn
              v-for="choice in THEME_CHOICES"
              :key="choice"
              :value="choice"
              size="small"
              :prepend-icon="themeIcons[choice]"
              class="flex-grow-1"
            >
              {{ t(`account.theme.${choice}`) }}
            </v-btn>
          </v-btn-toggle>
        </div>

        <div>
          <div class="text-caption text-medium-emphasis mb-1">{{ t('account.language') }}</div>
          <v-btn-toggle
            :model-value="locale"
            density="compact"
            variant="outlined"
            divided
            mandatory
            class="w-100"
            @update:model-value="setLocale"
          >
            <v-btn value="en" size="small" class="flex-grow-1">English</v-btn>
            <v-btn value="de" size="small" class="flex-grow-1">Deutsch</v-btn>
          </v-btn-toggle>
        </div>
      </div>

      <v-divider />

      <v-card-actions>
        <v-btn
          block
          prepend-icon="mdi-logout"
          variant="text"
          @click="leave"
        >
          {{ t('nav.signOut') }}
        </v-btn>
      </v-card-actions>
    </v-card>
  </v-menu>
</template>

<style scoped>
/* text-truncate does nothing inside a flex child without this: the child's min-width defaults to
   its content, so a long email address widens the menu instead of being cut. */
.min-width-0 {
  min-width: 0;
}
</style>
