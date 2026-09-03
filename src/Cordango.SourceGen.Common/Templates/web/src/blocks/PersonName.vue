<script setup>
import { ref, watch } from 'vue'
import { personName } from '../api.js'

// A person, by name rather than by uuid.
//
// The lookup and its one-request-per-person cache live in api.js, because the avatar block needs the
// same answer for the same reason — and two caches would mean two requests for one name.

const props = defineProps({ id: String })
const name = ref('')

watch(() => props.id, async (id) => { name.value = await personName(id) }, { immediate: true })
</script>

<template>
  <span>{{ name }}</span>
</template>
