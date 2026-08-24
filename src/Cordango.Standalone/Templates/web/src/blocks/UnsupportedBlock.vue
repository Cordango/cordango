<script setup>
// A block this build could not generate.
//
// Shown rather than omitted, still: a screen that quietly leaves something out looks finished, and
// the person reading it has no way to tell that the definition asked for more than they can see.
//
// But shown SMALL. This was a four-line info alert, and a card layout that repeats per record put
// one of them on every row — twenty-four paragraphs about a missing avatar, each of them larger
// than the record it was attached to. A gap should be visible, not louder than the screen it is on.
defineProps({ kind: String, reason: String })
</script>

<template>
  <v-tooltip>
    <template #activator="{ props }">
      <v-chip
        v-bind="props"
        size="x-small"
        variant="tonal"
        color="info"
        prepend-icon="mdi-puzzle-outline"
      >
        {{ kind }}
      </v-chip>
    </template>
    <div style="max-width: 34ch">
      This part of the screen was not generated: the definition asks for a
      <code>{{ kind }}</code> block.
      <template v-if="reason"> {{ reason }}</template>
      Built with <code>--allow-incomplete</code>; see the Partial build section of the README.
    </div>
  </v-tooltip>
</template>
