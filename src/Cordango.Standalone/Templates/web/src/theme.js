// What this application looks like, in one file.
//
// Two things live here and they do different jobs. The PALETTES say what a colour means — what a
// surface is, what a border is, what "primary" is — and the DEFAULTS say how every component uses
// them. Vuetify ships a complete Material theme, so an application that sets neither is not
// unstyled; it looks like the framework's demo, which is a different problem and reads as the same
// one.
//
// Change the palettes to rebrand. Change the defaults to change the feel — density, corners,
// borders, whether cards float or sit flat. Both are ordinary objects and neither is generated, so
// regenerating this application will not overwrite what you decide here.

/** Where the reader's choice is kept. Per browser, because it is a preference and not a setting. */
export const THEME_STORAGE_KEY = '{{AppKey}}.theme'

/**
 * The three things a reader can pick.
 *
 * `system` is the default and is not a synonym for `light`: Vuetify resolves it against
 * prefers-color-scheme at run time and follows the reader's machine when it changes. An application
 * that defaulted to `light` would look wrong on every dark laptop until somebody found the toggle.
 */
export const THEME_CHOICES = ['system', 'light', 'dark']

// Borders, not shadows.
//
// Elevation reads as Material; a hairline reads as an application. The whole surface system below
// is built on it, which is why `border-opacity` is set explicitly in both palettes rather than left
// at Vuetify's — the framework's default is tuned for a design that also has shadows.

export const light = {
  dark: false,
  colors: {
    background: '#F4F6F9',
    surface: '#FFFFFF',
    'surface-bright': '#FFFFFF',
    'surface-light': '#EDF0F5',
    'surface-variant': '#3B4453',
    'on-surface-variant': '#EDF0F5',
    primary: '#2C5CE6',
    'primary-darken-1': '#2049C4',
    secondary: '#5B6577',
    'secondary-darken-1': '#464F5E',
    success: '#14804A',
    warning: '#B45309',
    error: '#C02626',
    info: '#0B6BB5',
  },
  variables: {
    'border-color': '#0B1220',
    'border-opacity': 0.14,
    'high-emphasis-opacity': 0.9,
    'medium-emphasis-opacity': 0.62,
    'disabled-opacity': 0.38,
    'idle-opacity': 0.04,
    'hover-opacity': 0.05,
    'focus-opacity': 0.1,
    'selected-opacity': 0.08,
    'activated-opacity': 0.1,
    'pressed-opacity': 0.12,
    'dragged-opacity': 0.08,
  },
}

export const dark = {
  dark: true,
  colors: {
    background: '#0E1117',
    surface: '#161B23',
    'surface-bright': '#242C38',
    'surface-light': '#1D242E',
    'surface-variant': '#AEB8C7',
    'on-surface-variant': '#161B23',
    // Lighter than the light theme's blue on purpose. The same hex on a dark surface fails contrast
    // and reads as a disabled control, which is the one thing a primary action must never look like.
    primary: '#6C93FF',
    'primary-darken-1': '#4E79F0',
    secondary: '#9AA5B6',
    'secondary-darken-1': '#7C8899',
    success: '#3DD68C',
    warning: '#F2A13C',
    error: '#FF6B6B',
    info: '#4CA9F0',
  },
  variables: {
    'border-color': '#FFFFFF',
    'border-opacity': 0.14,
    'high-emphasis-opacity': 0.94,
    'medium-emphasis-opacity': 0.66,
    'disabled-opacity': 0.4,
    'idle-opacity': 0.06,
    'hover-opacity': 0.07,
    'focus-opacity': 0.12,
    'selected-opacity': 0.1,
    'activated-opacity': 0.12,
    'pressed-opacity': 0.14,
    'dragged-opacity': 0.1,
  },
}

/**
 * How every component uses the palette.
 *
 * <p>This is the half that does the most work, and the half that is easiest not to write. Vuetify's
 * own defaults are Material's: elevated cards, filled inputs, comfortable-to-loose density. They are
 * fine, they are also instantly recognisable, and mixing them with per-component overrides scattered
 * through forty templates is how an application ends up with three densities of text field on one
 * screen.</p>
 *
 * <p>Anything a component passes explicitly still wins. These are defaults, not rules.</p>
 */
export const defaults = {
  global: {
    // The ripple is Material's touch affordance and it makes a desktop application feel like a
    // phone. Buttons and list items keep their hover and focus states either way.
    ripple: false,
  },

  VCard: { elevation: 0, rounded: 'lg', border: true },
  VCardTitle: { class: 'text-subtitle-1 font-weight-medium' },
  VSheet: { rounded: 'lg' },

  VBtn: { rounded: 'md', variant: 'text' },
  // A button inside a card's title row is a secondary action, and an elevated one there competes
  // with the page. This is the contextual-defaults mechanism doing what forty `variant="tonal"`
  // attributes were doing by hand.
  VCardActions: { VBtn: { variant: 'text' } },
  VToolbar: { VBtn: { variant: 'text' } },

  VTextField: { variant: 'outlined', density: 'comfortable', hideDetails: 'auto' },
  VTextarea: { variant: 'outlined', density: 'comfortable', hideDetails: 'auto' },
  VSelect: { variant: 'outlined', density: 'comfortable', hideDetails: 'auto' },
  VAutocomplete: { variant: 'outlined', density: 'comfortable', hideDetails: 'auto' },
  VCombobox: { variant: 'outlined', density: 'comfortable', hideDetails: 'auto' },
  VFileInput: { variant: 'outlined', density: 'comfortable', hideDetails: 'auto' },
  VCheckbox: { density: 'comfortable', hideDetails: 'auto', color: 'primary' },
  VSwitch: { density: 'comfortable', hideDetails: 'auto', color: 'primary', inset: true },

  VList: { density: 'comfortable' },
  VTable: { density: 'comfortable' },
  VChip: { rounded: 'md', size: 'small' },
  VAlert: { variant: 'tonal', rounded: 'lg' },
  VTabs: { density: 'comfortable', color: 'primary' },
  VAppBar: { flat: true, border: 'b' },
  VNavigationDrawer: { border: 'e' },
  VProgressLinear: { color: 'primary', rounded: true },
  VDialog: {
    VCard: { rounded: 'lg', border: false, elevation: 8 },
  },
  VMenu: {
    VList: { density: 'compact' },
  },
  VTooltip: { location: 'bottom' },
}

/** The reader's stored choice, or `system` when there is none or the browser will not say. */
export function storedTheme() {
  try {
    const stored = localStorage.getItem(THEME_STORAGE_KEY)
    return THEME_CHOICES.includes(stored) ? stored : 'system'
  } catch {
    // Private windows and blocked site data both throw on read. A reader who cannot store a
    // preference should still get a working application on their system setting.
    return 'system'
  }
}

/** Remember it. Failing to store a preference is not worth an error anybody sees. */
export function rememberTheme(choice) {
  try {
    localStorage.setItem(THEME_STORAGE_KEY, choice)
  } catch {
    /* nothing to do and nothing worth saying */
  }
}

/** The whole theme block for `createVuetify`. */
export const theme = {
  defaultTheme: storedTheme(),
  themes: { light, dark },
}
