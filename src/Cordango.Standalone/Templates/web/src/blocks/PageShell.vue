<script setup>
defineProps({ title: String, subtitle: String })
</script>

<template>
  <v-container fluid class="pa-6">
    <div class="mb-4">
      <h1 class="text-h5">{{ title }}</h1>
      <p v-if="subtitle" class="text-medium-emphasis mb-0">{{ subtitle }}</p>
    </div>
    <slot />
  </v-container>
</template>
