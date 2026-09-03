import { createApp } from 'vue'
import { createVuetify } from 'vuetify'
import * as components from 'vuetify/components'
import * as directives from 'vuetify/directives'
import { createI18n } from 'vue-i18n'
import { CordangoControls, messages as controlWords } from '@cordango/web-controls'
import 'vuetify/styles'
import '@mdi/font/css/materialdesignicons.css'
import '@cordango/web-controls/styles'
import './styles.css'

import App from './App.vue'
import { router } from './router.js'
import { messages } from './i18n.js'
import { setLocaleSource, toast, recordRoute } from './records.js'
import { loadPeople, mediaUrl, loadTableSettings, saveTableSettings } from './api.js'
import { theme, defaults } from './theme.js'

/**
 * The shared controls' own words, UNDER the application's.
 *
 * A missing key is not an error — vue-i18n renders the key path — so a control whose words were
 * never installed says `table.expandAll` on a button and looks like a typo rather than a missing
 * dependency. Merging beneath means this application still wins wherever it has an opinion: say
 * `common.delete` in i18n.js and that is the word, in every control as well as every screen.
 */
function beneath(base, extra) {
  const merged = { ...extra }
  for (const [key, value] of Object.entries(base)) {
    merged[key] = value && typeof value === 'object' && !Array.isArray(value)
      ? beneath(value, merged[key] ?? {})
      : value
  }
  return merged
}

const words = Object.fromEntries(
  Object.keys(messages).map((tag) => [tag, beneath(messages[tag], controlWords[tag] ?? {})]))

const i18n = createI18n({
  legacy: false,
  locale: localStorage.getItem('{{AppKey}}.locale') || 'en',
  fallbackLocale: 'en',
  messages: words,
})

// Components and directives have to be handed over explicitly. `createVuetify()` on its own
// registers NOTHING, and the failure is quiet: Vue treats <v-card> as an unknown element, renders
// its children as bare text, and you get a page that says "Sign inSign in" with no form on it.
//
// This registers all of them, which is simple and correct at the cost of bundle size. To trim it,
// add vite-plugin-vuetify to vite.config.js and delete these two imports — it rewrites each usage
// into a per-component import so only what you use ships.
const vuetify = createVuetify({
  components,
  directives,
  icons: { defaultSet: 'mdi' },

  // What this application looks like. See theme.js — the palettes say what the colours mean, the
  // defaults say how every component uses them. Without the defaults an application is not
  // unstyled, it is Material's demo, which is a different problem that reads as the same one.
  theme,
  defaults,
})

// Formatting reads the APPLICATION's locale, never the browser's. `toLocaleString(undefined, …)`
// formats in whatever language the reader's laptop happens to be set to, so a German workspace
// would render English dates for anyone whose browser is English — and both spellings produce a
// plausible date, so nothing looks wrong.
setLocaleSource(() => i18n.global.locale.value)

// THE SIX THINGS A SHARED CONTROL CANNOT WORK OUT FOR ITSELF.
//
// The package deliberately has no seam for loading or saving data: rows arrive as props and a
// control emits what it wants done, because a default that faked data would render convincing rows
// nobody could tell were fictional. What is left is the short list of things that genuinely differ
// between the hosted platform and an application somebody generated and deployed themselves.
//
// Every seam is optional and defaults to something inert, so this list can be read as "what this
// application has taught the controls about itself" rather than as an interface to satisfy.
const controls = {
  locale: () => i18n.global.locale.value,
  people: loadPeople,
  media: mediaUrl,
  // A RESOLVED reference target — `{ handle, entity, manifest }` — rather than a bare key. Reading
  // it as a key produced links that looked right and went nowhere.
  route: (target, id) => (target?.entity ? recordRoute(target.entity, id) : null),
  loadTableSettings,
  saveTableSettings,
  toast: (message, tone) => toast(message, tone),
}

createApp(App).use(router).use(vuetify).use(i18n).use(CordangoControls, controls).mount('#app')
