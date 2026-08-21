<script setup>
defineProps({ label: String })
</script>

<template>
  <v-card>
    <v-card-title v-if="label">{{ label }}</v-card-title>
    <v-card-text>
      <slot />
    </v-card-text>
  </v-card>
</template>
