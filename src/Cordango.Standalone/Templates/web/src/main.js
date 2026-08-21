import { createApp } from 'vue'
import { createVuetify } from 'vuetify'
import * as components from 'vuetify/components'
import * as directives from 'vuetify/directives'
import { createI18n } from 'vue-i18n'
import 'vuetify/styles'
import '@mdi/font/css/materialdesignicons.css'

import App from './App.vue'
import { router } from './router.js'
import { messages } from './i18n.js'
import { setLocaleSource } from './records.js'

const i18n = createI18n({
  legacy: false,
  locale: localStorage.getItem('{{AppKey}}.locale') || 'en',
  fallbackLocale: 'en',
  messages,
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
})

// Formatting reads the APPLICATION's locale, never the browser's. `toLocaleString(undefined, …)`
// formats in whatever language the reader's laptop happens to be set to, so a German workspace
// would render English dates for anyone whose browser is English — and both spellings produce a
// plausible date, so nothing looks wrong.
setLocaleSource(() => i18n.global.locale.value)

createApp(App).use(router).use(vuetify).use(i18n).mount('#app')
