<script setup>
defineProps({ text: String, tone: { type: String, default: 'body' } })
</script>

<template>
  <p :class="tone === 'muted' ? 'text-medium-emphasis' : ''">{{ text }}</p>
</template>
