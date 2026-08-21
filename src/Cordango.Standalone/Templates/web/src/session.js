import { reactive } from 'vue'
import { api } from './api.js'

// Who is signed in, as one shared object rather than a store library. There is exactly one session
// per browser tab and nothing else needs the machinery a store brings.

export const session = reactive({
  loaded: false,
  authenticated: false,
  // True only while this database has no administrator at all. It is what sends the first person
  // who opens the application to the setup form instead of to a login form they cannot pass.
  setupRequired: false,
  id: null,
  email: null,
  displayName: null,
  personId: null,
  roles: [],
})

function apply(data) {
  session.authenticated = Boolean(data?.authenticated)
  session.setupRequired = Boolean(data?.setupRequired)
  session.id = data?.id ?? null
  session.email = data?.email ?? null
  session.displayName = data?.displayName ?? null
  session.personId = data?.personId ?? null
  session.roles = data?.roles ?? []
  session.loaded = true
}

/** Ask the server who we are. Called once before the first route resolves, so a reload lands where
 *  a signed-in person expects instead of bouncing through the login form. */
export async function loadSession() {
  try {
    apply(await api.get('/api/account/me'))
  } catch {
    // Unreachable server or an unexpected answer both mean the same thing here: we cannot claim
    // anybody is signed in. Fail closed and let the login form say so.
    apply(null)
  }
  return session
}

/** Create the first administrator and sign in as them. The server accepts this only while there is
 *  no administrator, and answers 409 from the moment there is one. */
export async function completeSetup(email, password, displayName) {
  apply(await api.post('/api/account/setup', { email, password, displayName }))
  return session
}

export async function signIn(email, password, rememberMe) {
  apply(await api.post('/api/account/login', { email, password, rememberMe }))
  return session
}

export async function signOut() {
  await api.post('/api/account/logout')
  apply(null)
}

export const isAdministrator = () => session.roles.includes('Administrator')
