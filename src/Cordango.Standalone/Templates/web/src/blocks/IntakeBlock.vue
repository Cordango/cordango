<script setup>
// The front door: the forms somebody can fill in, and the form itself once they pick one.
//
// This is how a person who may NOT create leads still files one — submitting needs only the right to
// respond, because the server owns the whole submission. Which is also why this posts to
// `/api/forms/{id}/submit` rather than writing a response and its answers itself: doing it here would
// need create rights on three entities and would leave orphans halfway through a failure.
import { ref, computed, onMounted } from 'vue'
import { api } from '../api.js'
import { loadRecords, displayOf } from '../records.js'
import { initialAnswers, firstUnanswered, buildAnswers } from '../formFill.js'
import FormFields from './FormFields.vue'
import EmptyState from './EmptyState.vue'

const props = defineProps({
  entity: { type: String, required: true },
  label: { type: String, default: '' },
  // The block's own filters, so retired forms stay off the list.
  filters: { type: Array, default: () => [] },
})

const forms = ref([])
const chosen = ref(null)
const questions = ref([])
const answers = ref({})
const loading = ref(true)
const busy = ref(false)
const error = ref('')
const done = ref(false)

async function load() {
  loading.value = true
  try {
    const page = await loadRecords(props.entity, {
      filters: props.filters.map((f) => `${f.field}:${f.operator ?? 'eq'}:${f.value}`),
      take: 100,
    })
    forms.value = page?.items ?? []
  } catch {
    forms.value = []
  } finally {
    loading.value = false
  }
}

async function pick(form) {
  chosen.value = form
  done.value = false
  error.value = ''
  const { questions: rows } = await api.get(`/api/forms/${encodeURIComponent(form.id)}/questions`)
  // The in-app endpoint hands back the question ROWS. The server projects the public ones through
  // the roles; here the block is talking to its own application, so it maps the same shape itself.
  questions.value = (rows ?? []).map((r) => ({
    id: r.id,
    text: r.text ?? r.question ?? r.label,
    answerType: r.answer_type ?? r.answerType,
    required: !!(r.required ?? r.is_required),
    options: r.choices ?? r.options,
  }))
  answers.value = initialAnswers(questions.value)
}

function back() {
  chosen.value = null
  questions.value = []
  done.value = false
}

async function submit() {
  error.value = ''
  if (firstUnanswered(questions.value, answers.value)) {
    error.value = 'Please answer all required questions.'
    return
  }
  busy.value = true
  try {
    await api.post(`/api/forms/${encodeURIComponent(chosen.value.id)}/submit`,
      { answers: buildAnswers(questions.value, answers.value) })
    done.value = true
  } catch (e) {
    error.value = e?.body?.errors?.join(' ') || e?.message || 'That could not be submitted.'
  } finally {
    busy.value = false
  }
}

const title = computed(() => (chosen.value ? displayOf(props.entity, chosen.value) : props.label))

onMounted(load)
</script>

<template>
  <v-card variant="flat" border class="pa-4">
    <div class="d-flex align-center mb-3">
      <v-btn v-if="chosen" size="small" variant="text" class="text-none mr-2"
        prepend-icon="mdi-chevron-left" @click="back">Back to forms</v-btn>
      <span class="text-subtitle-1 font-weight-medium">{{ title }}</span>
    </div>

    <div v-if="loading" class="text-medium-emphasis">Loading…</div>

    <template v-else-if="!chosen">
      <EmptyState v-if="!forms.length" title="No forms are available right now." />
      <v-list v-else density="compact">
        <v-list-item v-for="form in forms" :key="form.id" @click="pick(form)">
          <v-list-item-title>{{ displayOf(props.entity, form) }}</v-list-item-title>
          <template #append><v-icon icon="mdi-chevron-right" size="small" /></template>
        </v-list-item>
      </v-list>
    </template>

    <div v-else-if="done" class="text-center pa-6">
      <v-icon icon="mdi-check-circle" color="success" size="48" class="mb-2" />
      <div class="text-h6 mb-1">Thank you</div>
      <div class="text-medium-emphasis mb-4">Your response has been recorded.</div>
      <v-btn color="primary" class="text-none" @click="pick(chosen)">Submit another</v-btn>
    </div>

    <template v-else>
      <div v-if="!questions.length" class="text-medium-emphasis">This form has no questions yet.</div>
      <v-alert v-if="error" type="error" variant="tonal" density="compact" class="mb-4">{{ error }}</v-alert>
      <FormFields :questions="questions" :answers="answers" :disabled="busy" />
      <v-btn v-if="questions.length" color="primary" class="text-none mt-2" :loading="busy"
        @click="submit">Submit response</v-btn>
    </template>
  </v-card>
</template>
