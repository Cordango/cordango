import { createRouter, createWebHistory } from 'vue-router'
import { session, loadSession } from './session.js'

import HomeView from './views/HomeView.vue'
import DirectoryView from './views/DirectoryView.vue'
import AccessKeysView from './views/AccessKeysView.vue'
import LoginView from './views/LoginView.vue'
import SetupView from './views/SetupView.vue'

// Generated screens are added to this list, one route each. Routes you add yourself belong in a
// file of your own — regenerating replaces this one.
const routes = [
  { path: '/', name: 'home', component: HomeView },
  { path: '/directory', name: 'directory', component: DirectoryView },
  { path: '/access-keys', name: 'access-keys', component: AccessKeysView },
  { path: '/login', name: 'login', component: LoginView, meta: { anonymous: true } },
  { path: '/setup', name: 'setup', component: SetupView, meta: { anonymous: true } },
]

export const router = createRouter({
  history: createWebHistory(),
  routes,
})

router.beforeEach(async (to) => {
  // Ask once, on the first navigation. Every later route already knows.
  if (!session.loaded) await loadSession()

  // A database with no administrator has exactly one thing anybody can do, so there is exactly one
  // page to be on. Once one exists the setup route is not a page any more.
  if (session.setupRequired) return to.name === 'setup' ? true : { name: 'setup' }
  if (to.name === 'setup') return session.authenticated ? { name: 'home' } : { name: 'login' }

  if (to.meta.anonymous) return true
  if (session.authenticated) return true

  // `redirect` so that signing in returns to the page that was actually wanted. Landing everybody
  // on the home page after login quietly loses the link somebody followed to get here.
  return { name: 'login', query: { redirect: to.fullPath } }
})
