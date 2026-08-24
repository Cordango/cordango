<script setup>
import { useSurface } from './surface.js'

// A titled container. The outermost one is a card; a nested one is a labelled section, because two
// borders around the same content is not a design, it is a bug that looks like one.

defineProps({ label: String })

const depth = useSurface()
</script>

<template>
  <v-card v-if="depth === 0">
    <v-card-title v-if="label">{{ label }}</v-card-title>
    <v-card-text :class="label ? 'pt-0' : ''">
      <div class="d-flex flex-column ga-3">
        <slot />
      </div>
    </v-card-text>
  </v-card>

  <section v-else>
    <h3 v-if="label" class="text-subtitle-2 font-weight-bold mb-2">{{ label }}</h3>
    <div class="d-flex flex-column ga-3">
      <slot />
    </div>
  </section>
</template>
