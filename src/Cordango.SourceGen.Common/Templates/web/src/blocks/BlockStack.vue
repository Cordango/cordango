<script setup>
const gaps = { none: '0', sm: '8px', md: '16px', lg: '24px', xl: '32px' }
defineProps({ gap: { type: String, default: 'md' } })
</script>

<template>
  <div class="d-flex flex-column" :style="{ gap: gaps[gap] || gaps.md }">
    <slot />
  </div>
</template>
