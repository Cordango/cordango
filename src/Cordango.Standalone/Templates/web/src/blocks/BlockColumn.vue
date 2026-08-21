<script setup>
import { computed } from 'vue'

const props = defineProps({
  // How many columns the row has. The width has to be a NUMBER: Vuetify's `md` accepts a span or
  // "auto", and binding it to `true` — which is what a boolean attribute looks like when it comes
  // from a binding — silently leaves every column full width. The authored two-column layout then
  // renders as two stacked rows, which looks like a deliberate design rather than a bug.
  count: { type: Number, default: 1 },
})

const span = computed(() => Math.max(1, Math.floor(12 / Math.max(1, props.count))))
</script>

<template>
  <!-- Full width on a phone, sharing the row from medium up. The authored layout is the WIDEST
       one; a narrow screen gets it stacked. -->
  <v-col cols="12" :md="span">
    <div class="d-flex flex-column ga-4">
      <slot />
    </div>
  </v-col>
</template>
