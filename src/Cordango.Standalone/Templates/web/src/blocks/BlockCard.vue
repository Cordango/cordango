<script setup>
defineProps({ label: String })
</script>

<template>
  <v-card>
    <v-card-title v-if="label">{{ label }}</v-card-title>
    <v-card-text :class="label ? 'pt-0' : ''">
      <div class="d-flex flex-column ga-3">
        <slot />
      </div>
    </v-card-text>
  </v-card>
</template>
