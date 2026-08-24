<script setup>
// The frame every screen sits in: a title, room for the actions that belong to the whole page, and
// a consistent rhythm down the page.
//
// The `actions` slot is the part that was missing. Without somewhere for a page-level action to go,
// every "New" button ended up inside the first card on the screen — so a page with three lists had
// three New buttons and no obvious primary action, and a page with none had nothing to do at all.

defineProps({
  title: String,
  subtitle: String,
})
</script>

<template>
  <v-container fluid class="cd-page pa-4 pa-md-6">
    <header class="d-flex flex-wrap align-start ga-4 mb-5">
      <div class="flex-grow-1" style="min-width: 0">
        <h1 class="text-h5 font-weight-bold" style="letter-spacing: -0.015em">{{ title }}</h1>
        <p v-if="subtitle" class="text-body-2 text-medium-emphasis mb-0 mt-1">{{ subtitle }}</p>
      </div>
      <div class="d-flex align-center ga-2 flex-shrink-0">
        <slot name="actions" />
      </div>
    </header>

    <!-- One gap, decided here. Blocks used to space themselves and a page of four different block
         kinds had four different gaps down it. -->
    <div class="d-flex flex-column ga-5">
      <slot />
    </div>
  </v-container>
</template>
