<script setup>
// Nothing here, said once, in one voice.
//
// "Nothing here yet" was written inline in five components and read as five different things: a
// list with no rows, a filter that matched nothing and a request that failed all looked the same.
// They are not the same, and the difference is the only thing the reader needs.

defineProps({
  icon: { type: String, default: 'mdi-inbox-outline' },
  title: { type: String, default: 'Nothing here yet' },
  body: { type: String, default: '' },
})
</script>

<template>
  <div class="cd-empty">
    <v-icon :icon="icon" size="36" class="text-disabled" />
    <div class="text-body-2 font-weight-medium">{{ title }}</div>
    <div v-if="body" class="text-caption" style="max-width: 42ch">{{ body }}</div>
    <!-- Where the way out goes: "Add the first one", "Clear the filter". An empty state that only
         reports is half an answer. -->
    <div class="mt-2">
      <slot />
    </div>
  </div>
</template>
