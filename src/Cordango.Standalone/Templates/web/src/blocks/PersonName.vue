<script setup>
import { ref, watch } from 'vue'
import { api } from '../api.js'

// One directory read for the whole page, shared by every chip on it. Without the cache a table of
// thirty rows would ask for the same six people thirty times.
const cache = new Map()
const props = defineProps({ id: String })
const name = ref('')

async function resolve(id) {
  if (!id) { name.value = ''; return }
  if (cache.has(id)) { name.value = cache.get(id); return }

  const promise = api
    .get(`/api/directory/person/${encodeURIComponent(id)}`)
    .then((p) => p?.full_name || id)
    // Somebody who has left, or a reference to a person this role cannot read. The id is a worse
    // answer than a name and a better one than a blank space.
    .catch(() => id)

  cache.set(id, promise)
  name.value = await promise
  cache.set(id, name.value)
}

watch(() => props.id, resolve, { immediate: true })
</script>

<template>
  <span>{{ name }}</span>
</template>
