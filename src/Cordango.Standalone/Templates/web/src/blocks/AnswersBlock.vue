<script setup>
// What the requester actually said: the questions and answers of the submission this record came
// from, in the form's own order.
//
// Belongs near the TOP of a record filed by a form, because the answers are the request. Renders
// nothing at all for a record somebody typed in by hand — an empty "Answers" heading on every lead a
// rep entered themselves would be a permanent reminder of a thing that did not happen.
import { ref, computed, watch, onMounted } from 'vue'
import { api } from '../api.js'
import { loadRecords } from '../records.js'

const props = defineProps({
  // The reference field on THIS record pointing at the submission.
  via: { type: String, required: true },
  // The submission's own entity, and the answer entity that hangs off it.
  answerEntity: { type: String, required: true },
  answerResponseField: { type: String, required: true },
  answerQuestionField: { type: String, required: true },
  answerValueField: { type: String, required: true },
  questionEntity: { type: String, required: true },
  questionTextField: { type: String, default: '' },
  questionOrderField: { type: String, default: '' },
  label: { type: String, default: 'What they told us' },
  record: { type: Object, default: null },
})

const rows = ref([])
const loaded = ref(false)

const responseId = computed(() => props.record?.[props.via] ?? null)

function textOf(question) {
  return (props.questionTextField ? question?.[props.questionTextField] : null) ?? '(question)'
}

// A json answer can be a string, a number, a boolean or a list of choices. Rendered as text rather
// than through the field formatter: the value's shape came from the QUESTION, not from a column with
// a type the formatter could look up.
function display(value) {
  if (value == null || value === '') return '—'
  if (Array.isArray(value)) return value.join(', ')
  if (typeof value === 'boolean') return value ? 'Yes' : 'No'
  return String(value)
}

async function load() {
  loaded.value = false
  rows.value = []
  if (!responseId.value) { loaded.value = true; return }
  try {
    const [answers, questions] = await Promise.all([
      loadRecords(props.answerEntity, {
        filters: [`${props.answerResponseField}:eq:${responseId.value}`], take: 200,
      }),
      loadRecords(props.questionEntity, { take: 200 }),
    ])
    const byId = new Map((questions?.items ?? []).map((q) => [q.id, q]))
    const list = (answers?.items ?? []).map((a) => ({
      id: a.id,
      question: byId.get(a[props.answerQuestionField]) ?? null,
      value: a[props.answerValueField],
    }))
    // In the FORM's order, not the order the answers happened to be written in.
    if (props.questionOrderField) {
      list.sort((a, b) =>
        (Number(a.question?.[props.questionOrderField]) || 0)
        - (Number(b.question?.[props.questionOrderField]) || 0))
    }
    rows.value = list
  } finally {
    loaded.value = true
  }
}

watch(responseId, load)
onMounted(load)
</script>

<template>
  <v-card v-if="responseId && (!loaded || rows.length)" variant="flat" border class="pa-4 mb-4">
    <div class="text-subtitle-1 font-weight-medium mb-3">{{ label }}</div>
    <div v-if="!loaded" class="text-medium-emphasis">Loading…</div>
    <dl v-else class="an">
      <template v-for="row in rows" :key="row.id">
        <dt>{{ textOf(row.question) }}</dt>
        <dd>{{ display(row.value) }}</dd>
      </template>
    </dl>
  </v-card>
</template>

<style scoped>
.an { display: grid; grid-template-columns: minmax(140px, 32%) 1fr; row-gap: 10px; column-gap: 16px; }
.an dt { font-size: 13px; color: rgba(var(--v-theme-on-surface), .7); }
.an dd { margin: 0; }

@media (max-width: 600px) {
  .an { grid-template-columns: 1fr; row-gap: 2px; }
  .an dd { margin-bottom: 8px; }
}
</style>
