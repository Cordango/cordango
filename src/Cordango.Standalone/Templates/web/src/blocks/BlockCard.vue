<script setup>
import { useSurface } from './surface.js'

// A titled container. The outermost one is a card; a nested one is a labelled section, because two
// borders around the same content is not a design, it is a bug that looks like one.

const props = defineProps({
  label: String,
  // 'none' | 'sm' | 'md' | 'lg' — how much room the card gives its contents.
  padding: { type: String, default: 'md' },
  // A hairline instead of Vuetify's elevation. This palette draws borders rather than shadows, so a
  // card in a grid of cards needs the edge to be visible at all.
  bordered: { type: Boolean, default: false },
})

const pads = { none: 'pa-0', sm: 'pa-2', md: 'pa-4', lg: 'pa-6' }

const depth = useSurface()
</script>

<template>
  <v-card v-if="depth === 0" :variant="bordered ? 'outlined' : undefined" class="h-100">
    <v-card-title v-if="label">{{ label }}</v-card-title>
    <div :class="[pads[padding] ?? pads.md, label ? 'pt-0' : '']">
      <div class="d-flex flex-column ga-3">
        <slot />
      </div>
    </div>
  </v-card>

  <section v-else>
    <h3 v-if="label" class="text-subtitle-2 font-weight-bold mb-2">{{ label }}</h3>
    <div class="d-flex flex-column ga-3">
      <slot />
    </div>
  </section>
</template>
