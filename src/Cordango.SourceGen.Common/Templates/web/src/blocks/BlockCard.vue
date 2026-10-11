<script setup>
import { ref, computed } from 'vue'
import { useSurface } from './surface.js'
import RecordPeek from './RecordPeek.vue'

// A titled container. The outermost one is a card; a nested one is a labelled section, because two
// borders around the same content is not a design, it is a bug that looks like one.

const props = defineProps({
  label: String,
  // 'none' | 'sm' | 'md' | 'lg' — how much room the card gives its contents.
  padding: { type: String, default: 'md' },
  // A hairline instead of Vuetify's elevation. This palette draws borders rather than shadows, so a
  // card in a grid of cards needs the edge to be visible at all.
  bordered: { type: Boolean, default: false },
  // The card is a way to LOOK at its record: a click opens the quick-look panel over the page
  // rather than leaving it. A dashboard's "out of stock" card is a question — which part, how many
  // on order — and the answer should not cost the dashboard.
  openDetail: { type: Boolean, default: false },
  entity: { type: String, default: null },
  record: { type: Object, default: null },
})

const pads = { none: 'pa-0', sm: 'pa-2', md: 'pa-4', lg: 'pa-6' }

const depth = useSurface()

const peekable = computed(() => props.openDetail && Boolean(props.entity && props.record?.id))
const peeking = ref(false)

// A button or a link inside the card owns its own click; the card is only the space around them.
function peek(event) {
  if (event?.target?.closest?.('button, a, input, textarea, select')) return
  peeking.value = true
}

// Bound only when the card IS clickable. A listener on every card would make Vuetify draw each one
// as clickable — the hover wash and the ripple — whether or not anything happens.
const listeners = computed(() => (peekable.value ? { click: peek, keydown: (e) => e.key === 'Enter' && peek(e) } : {}))
</script>

<template>
  <v-card
    v-if="depth === 0"
    :variant="bordered ? 'outlined' : undefined"
    class="h-100"
    :role="peekable ? 'button' : undefined"
    :tabindex="peekable ? 0 : undefined"
    v-on="listeners"
  >
    <v-card-title v-if="label">{{ label }}</v-card-title>
    <div :class="[pads[padding] ?? pads.md, label ? 'pt-0' : '']">
      <div class="d-flex flex-column ga-3">
        <slot />
      </div>
    </div>
  </v-card>

  <section
    v-else
    :class="peekable ? 'cd-card-peek' : ''"
    :role="peekable ? 'button' : undefined"
    :tabindex="peekable ? 0 : undefined"
    v-on="listeners"
  >
    <h3 v-if="label" class="text-subtitle-2 font-weight-bold mb-2">{{ label }}</h3>
    <div class="d-flex flex-column ga-3">
      <slot />
    </div>
  </section>

  <RecordPeek
    v-if="peeking"
    :entity="entity"
    :record="record"
    :model-value="true"
    @update:model-value="peeking = false"
  />
</template>

<style scoped>
.cd-card-peek { cursor: pointer; }
.cd-card-peek:focus-visible { outline: 2px solid rgb(var(--v-theme-primary)); outline-offset: 2px; }
</style>
