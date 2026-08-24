<script setup>
defineProps({ text: String, tone: { type: String, default: 'body' } })
</script>

<template>
  <p class="text-body-2 mb-0" :class="tone === 'muted' ? 'text-medium-emphasis' : ''">{{ text }}</p>
</template>
