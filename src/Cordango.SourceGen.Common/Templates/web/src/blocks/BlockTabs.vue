<script setup>
import { ref } from 'vue'

// The tab keys, so a tab can be linked to. Content arrives as one slot per key.
const props = defineProps({ tabs: { type: Array, default: () => [] } })
const current = ref(props.tabs[0]?.key)
</script>

<template>
  <div>
    <v-tabs v-model="current" class="mb-4">
      <v-tab v-for="tab in tabs" :key="tab.key" :value="tab.key">{{ tab.label }}</v-tab>
    </v-tabs>
    <v-window v-model="current">
      <v-window-item v-for="tab in tabs" :key="tab.key" :value="tab.key">
        <slot :name="tab.key" />
      </v-window-item>
    </v-window>
  </div>
</template>
