<script setup>
import { ref, onMounted, onUnmounted } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '../api.js'

const router = useRouter()
const items = ref([])
const unread = ref(0)
const open = ref(false)

async function load() {
  try {
    const page = await api.get('/api/notifications')
    items.value = page?.items ?? []
    unread.value = page?.unread ?? 0
  } catch {
    // Not signed in, or the server is unreachable. A bell that throws would take the whole shell
    // down with it, and the one thing it is not is important enough for that.
  }
}

async function openItem(item) {
  open.value = false
  if (!item.read_at) {
    await api.post(`/api/notifications/${item.id}/read`).catch(() => {})
    unread.value = Math.max(0, unread.value - 1)
    item.read_at = new Date().toISOString()
  }
  if (item.link) router.push(item.link)
}

async function readAll() {
  await api.post('/api/notifications/read-all').catch(() => {})
  await load()
}

// Polled rather than pushed. A websocket would be better and is a dependency and a deployment
// concern; a minute is soon enough for "your expense was approved" and costs one small query.
let timer = null
onMounted(() => {
  load()
  timer = setInterval(load, 60_000)
})
onUnmounted(() => clearInterval(timer))
</script>

<template>
  <v-menu v-model="open" :close-on-content-click="false" location="bottom end">
    <template #activator="{ props }">
      <v-btn icon v-bind="props" @click="load">
        <v-badge :model-value="unread > 0" :content="unread" color="error">
          <v-icon icon="mdi-bell-outline" />
        </v-badge>
      </v-btn>
    </template>

    <v-card min-width="360" max-width="440">
      <v-card-title class="d-flex align-center text-subtitle-1">
        Notifications
        <v-spacer />
        <v-btn v-if="unread > 0" size="x-small" variant="text" @click="readAll">Mark all read</v-btn>
      </v-card-title>

      <v-list v-if="items.length" density="compact" max-height="400" class="overflow-y-auto">
        <v-list-item
          v-for="item in items"
          :key="item.id"
          :active="!item.read_at"
          @click="openItem(item)"
        >
          <v-list-item-title>{{ item.title }}</v-list-item-title>
          <v-list-item-subtitle>{{ item.message }}</v-list-item-subtitle>
        </v-list-item>
      </v-list>

      <v-card-text v-else class="text-medium-emphasis">Nothing yet.</v-card-text>
    </v-card>
  </v-menu>
</template>
