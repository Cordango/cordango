<script setup>
defineProps({ kind: String, reason: String })
</script>

<template>
  <!--
    A block this build could not generate. Shown rather than omitted on purpose: a screen that
    quietly leaves something out looks finished, and the person reading it has no way to tell that
    the definition asked for more than they are seeing.
  -->
  <v-alert type="info" variant="tonal" density="comfortable">
    <div class="font-weight-medium">This part of the screen was not generated</div>
    <div class="text-caption">
      The definition asks for a <code>{{ kind }}</code> block.
      <template v-if="reason"> {{ reason }}</template>
      This application was built with <code>--allow-partial-ui</code>; see the Partial build section
      of the README.
    </div>
  </v-alert>
</template>
