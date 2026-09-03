<script setup>
defineProps({ label: String, help: String })
</script>

<template>
  <section>
    <h2 v-if="label" class="text-subtitle-1 font-weight-bold mb-1">{{ label }}</h2>
    <p v-if="help" class="text-body-2 text-medium-emphasis mb-3">{{ help }}</p>
    <div class="d-flex flex-column ga-4">
      <slot />
    </div>
  </section>
</template>
