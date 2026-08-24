import { createRouter, createWebHistory } from 'vue-router'
import { session, loadSession } from './session.js'

import HomeView from './views/HomeView.vue'
import DirectoryView from './views/DirectoryView.vue'
import AdminUsersView from './views/AdminUsersView.vue'
import ProfileView from './views/ProfileView.vue'
import AccessKeysView from './views/AccessKeysView.vue'
import LoginView from './views/LoginView.vue'
import SetupView from './views/SetupView.vue'

// The definition's screens, then the record pages behind them. Routes you add
// yourself belong in a file of your own — regenerating replaces this one.
const routes = [
  { path: '/', name: 'home', component: HomeView },
  { path: '/directory', name: 'directory', component: DirectoryView },
  { path: '/admin/users', name: 'admin-users', component: AdminUsersView, meta: { administrator: true } },
  { path: '/profile', name: 'profile', component: ProfileView },
  { path: '/access-keys', name: 'access-keys', component: AccessKeysView },
  { path: '/login', name: 'login', component: LoginView, meta: { anonymous: true } },
  { path: '/setup', name: 'setup', component: SetupView, meta: { anonymous: true } },
]

export const router = createRouter({
  history: createWebHistory(),
  routes,
})

router.beforeEach(async (to) => {
  // Asked once, on the first navigation. Every later route already knows.
  if (!session.loaded) await loadSession()

  // A database with no administrator has exactly one thing anybody can do, so
  // there is exactly one page to be on. Once one exists, setup is not a page.
  if (session.setupRequired) return to.name === 'setup' ? true : { name: 'setup' }
  if (to.name === 'setup') return session.authenticated ? { name: 'home' } : { name: 'login' }

  if (to.meta.anonymous) return true

  if (!session.authenticated) {
    // `redirect` so that signing in returns to the page that was wanted. Landing
    // everybody on the home page quietly loses the link somebody followed.
    return { name: 'login', query: { redirect: to.fullPath } }
  }

  // An account created by an administrator is on a password two people know. Nothing else in the
  // application is reachable until that stops being true — a prompt somebody can dismiss is a
  // prompt everybody dismisses.
  if (session.mustChangePassword && to.name !== 'profile') return { name: 'profile' }

  // The server decides this too, and refuses the request either way. Checking it here only saves
  // somebody the trip to a screen that would have come back empty.
  if (to.meta.administrator && !session.isAdministrator) return { name: 'home' }

  return true
})
