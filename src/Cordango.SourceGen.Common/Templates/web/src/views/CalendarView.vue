<script setup>
import { ref, computed, onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useDisplay } from 'vuetify'
import { api } from '../api.js'
import {
  iso, gridStart, gridEnd, gridDays, weekdayNames, monthLabel, shiftMonth, bucketByDay,
} from '../monthGrid.js'

// YOUR dates, from every entity in this application that has any.
//
// <p><b>Why a screen of its own rather than another calendar block.</b> A block shows one entity's
// records — a month of projects, a month of milestones — and somebody who has both has to open two
// screens and hold the overlap in their head. The question a calendar answers is "what does my week
// look like", and that question does not know which entity the answer came from.</p>
//
// <p><b>The owner filter is not here, and that is the point.</b> The server resolves the person from
// the session; there is no parameter naming one, and nothing this page could send would change whose
// dates come back. A "my calendar" that took an id would be a way to read anybody's week, and the
// response would look entirely ordinary to whoever reviewed it.</p>
//
// <p><b>And the window is a real query.</b> The six weeks on screen are what is asked for, not the
// first fifty rows arranged into six weeks. When more than the server will return falls inside them,
// it REFUSES rather than trimming: nobody can tell an empty Tuesday from a Tuesday whose entries did
// not fit.</p>

const router = useRouter()
const { mobile } = useDisplay()

const cursor = ref(new Date())
const entries = ref([])
const loading = ref(true)
const error = ref(null)

const days = computed(() => gridDays(cursor.value))
const weekdays = weekdayNames()
const label = computed(() => monthLabel(cursor.value))

const byDay = computed(() =>
  bucketByDay(entries.value, (e) => e.start, (e) => e.end))

// A MONTH GRID IS A DESKTOP SHAPE. Seven columns on a phone gives 45px cells, and an entry in one
// is a coloured smudge. So the narrow reading is an agenda: the days that have something, in order,
// and nothing at all for the ones that do not. Same six weeks, same query, same entries — the
// arrangement is what changes, which is the rule everywhere else in this shell.
const agenda = computed(() => days.value
  .filter((day) => (byDay.value[day.key] || []).length > 0)
  .map((day) => ({ ...day, entries: byDay.value[day.key] })))

const dayLabel = (key) =>
  new Date(key).toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long' })

async function load() {
  loading.value = true
  error.value = null

  try {
    const params = new URLSearchParams({
      from: iso(gridStart(cursor.value)),
      until: iso(gridEnd(cursor.value)),
    })
    const window = await api.get(`/api/me/calendar?${params}`)
    entries.value = window?.entries ?? []
  } catch (failure) {
    error.value = failure.message
    entries.value = []
  } finally {
    loading.value = false
  }
}

const move = (by) => { cursor.value = shiftMonth(cursor.value, by) }
const open = (entry) => router.push(entry.link)

// A phase is what makes a proposal LOOK different from something that is going ahead. The colour
// comes from the definition; the phase decides whether the chip is filled or outlined, so the two
// are still told apart by somebody who cannot rely on colour.
const variant = (entry) =>
  entry.state?.phase === 'done' || entry.state?.phase === 'cancelled' ? 'outlined' : 'flat'

onMounted(load)
watch(cursor, load)
</script>

<template>
  <v-container class="py-6">
    <div class="d-flex align-center mb-4">
      <h1 class="text-h5">{{ $t('calendar.title') }}</h1>
      <v-spacer />
      <v-btn icon="mdi-chevron-left" size="small" variant="text" :aria-label="$t('calendar.previous')"
        @click="move(-1)" />
      <span class="text-body-2 mx-2" style="min-width: 9rem; text-align: center">{{ label }}</span>
      <v-btn icon="mdi-chevron-right" size="small" variant="text" :aria-label="$t('calendar.next')"
        @click="move(1)" />
      <v-btn size="small" variant="text" class="ml-2" @click="cursor = new Date()">
        {{ $t('calendar.today') }}
      </v-btn>
    </div>

    <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}</v-alert>
    <v-skeleton-loader v-else-if="loading" type="image" />

    <template v-else-if="mobile">
      <v-list v-if="agenda.length > 0" lines="two" class="py-0">
        <template v-for="day in agenda" :key="day.key">
          <v-list-subheader>{{ dayLabel(day.key) }}</v-list-subheader>
          <v-list-item v-for="entry in day.entries" :key="entry.id" @click="open(entry)">
            <template #prepend>
              <v-avatar :color="entry.color || 'primary'" size="12" class="mr-3" />
            </template>
            <v-list-item-title>{{ entry.title }}</v-list-item-title>
            <v-list-item-subtitle>{{ entry.entityLabel }}</v-list-item-subtitle>
          </v-list-item>
        </template>
      </v-list>
      <v-alert v-else type="info" variant="tonal">{{ $t('calendar.empty') }}</v-alert>

      <p class="text-caption text-medium-emphasis mt-4">{{ $t('calendar.scope') }}</p>
    </template>

    <template v-else>
      <div class="cd-month text-caption text-medium-emphasis mb-1">
        <div v-for="name in weekdays" :key="name" class="pa-1">{{ name }}</div>
      </div>

      <div class="cd-month">
        <v-sheet
          v-for="day in days"
          :key="day.key"
          class="pa-1 d-flex flex-column ga-1"
          :class="{ 'text-disabled': day.outside }"
          border
          rounded
          style="min-height: 6.5rem"
        >
          <div class="d-flex align-center">
            <v-chip v-if="day.today" size="x-small" color="primary" variant="flat">{{ day.day }}</v-chip>
            <span v-else class="text-caption">{{ day.day }}</span>
          </div>

          <v-chip
            v-for="entry in byDay[day.key] || []"
            :key="entry.id"
            size="x-small"
            class="cd-entry"
            :color="entry.color || 'primary'"
            :variant="variant(entry)"
            :title="`${entry.entityLabel} — ${entry.title}`"
            @click="open(entry)"
          >
            {{ entry.title }}
          </v-chip>
        </v-sheet>
      </div>

      <p class="text-caption text-medium-emphasis mt-4">{{ $t('calendar.scope') }}</p>
    </template>
  </v-container>
</template>

<style scoped>
.cd-month { display: grid; grid-template-columns: repeat(7, 1fr); gap: 4px; }

.cd-entry { cursor: pointer; justify-content: flex-start; }
.cd-entry :deep(.v-chip__content) { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
</style>
