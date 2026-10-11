<script setup>
import { ref, computed } from 'vue'
import { commandsOf, recordsChanged, resolveValue, loadRecords, createRecord, toast } from '../records.js'
import { session } from '../session.js'
import { useScopes } from '../scopes.js'
import CommandButton from './CommandButton.vue'

// An `action` block: one named command, placed on the screen rather than in the record's own
// toolbar. The button itself is CommandButton, which already knows how to ask for input, confirm,
// and hide itself when the record's state does not allow the move.
const props = defineProps({
  entity: String,
  record: Object,
  command: String,
  // The record this button acts on, NAMED rather than taken from where it sits: `{ order: '{{po.id}}',
  // item: '{{scanned.id}}' }` is "the line for this part on this order". Without it the button ran
  // the command on whatever record surrounded it — the scanned PART, against the order-line
  // endpoint — which the server refused, or worse, found.
  keys: { type: Object, default: null },
  state: { type: Object, default: null },
})

const definition = computed(() => commandsOf(props.entity).find((c) => c.key === props.command) || null)

const scopes = useScopes()
const anchored = computed(() => Boolean(props.keys))

const identity = computed(() => {
  const context = { ...session, state: props.state ?? {}, record: props.record ?? {}, scopes: scopes.value }
  return Object.fromEntries(
    Object.entries(props.keys ?? {}).map(([field, value]) => [field, resolveValue(value, context)]))
})

// A key that resolves to nothing means the button has no identity yet — nothing scanned, nothing
// selected. Disabled rather than allowed to match, or worse CREATE, a record keyed on blanks.
const identified = computed(() =>
  Object.values(identity.value).every((value) => value !== '' && value !== null && value !== undefined))

const target = ref(null)
const created = ref(false)
const busy = ref(false)

// Find the record, and create it when there is none: the line for a part nobody ordered does not
// exist until somebody counts it. Two requests and not atomic, the same bargain the platform makes —
// a command cancelled afterwards leaves the record behind, because the command needs the record to
// run on. The create goes through the ordinary endpoint, so who may add one is the server's answer.
async function anchor() {
  if (!identified.value || busy.value) return
  busy.value = true
  try {
    const filters = Object.entries(identity.value).map(([field, value]) => ({ field, operator: 'eq', value }))
    const page = await loadRecords(props.entity, { filters, take: 1 })
    const found = page?.items?.[0] ?? null
    created.value = !found
    target.value = found ?? await createRecord(props.entity, { ...identity.value })
  } catch (failure) {
    toast(failure.message, 'error')
  } finally {
    busy.value = false
  }
}

function settle(changed) {
  if (changed || created.value) recordsChanged(props.entity)
  target.value = null
  created.value = false
}

const colors = { primary: 'primary', danger: 'error', neutral: undefined }
</script>

<template>
  <template v-if="definition && anchored">
    <v-btn
      :color="colors[definition.style]"
      variant="tonal"
      size="small"
      class="ml-1"
      :disabled="!identified"
      :loading="busy"
      @click="anchor"
    >
      {{ definition.label }}
    </v-btn>
    <CommandButton
      v-if="target"
      auto
      :entity="entity"
      :record="target"
      :command="definition"
      @done="settle(true)"
      @cancelled="settle(false)"
    />
  </template>
  <CommandButton
    v-else-if="definition"
    :entity="entity"
    :record="record"
    :command="definition"
    @done="recordsChanged(entity)"
  />
</template>
