<script setup>
const gaps = { none: '0', sm: '8px', md: '16px', lg: '24px', xl: '32px' }
defineProps({ gap: { type: String, default: 'md' } })
</script>

<template>
  <!-- Wraps rather than scrolls: on a narrow screen the row becomes a column, which is what the
       authored layout means at that width. -->
  <div class="d-flex flex-wrap" :style="{ gap: gaps[gap] || gaps.md }">
    <slot />
  </div>
</template>
