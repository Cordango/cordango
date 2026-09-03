<script setup>
// The questions of a form, as inputs. Nothing else.
//
// Presentational on purpose, and that is a SECURITY property rather than a tidiness one: this
// component renders both inside the application and on the page a stranger opens, so it must not be
// able to talk to the API at all. The in-app block posts through the session-carrying client and the
// public page through `publicApi.js`; keeping the inputs here and the network in the two callers is
// what makes sending a session cookie to a public endpoint impossible rather than merely avoided.
//
// Questions arrive already normalised to {id, text, answerType, required, options} — the shape the
// public endpoint returns, and the shape `normalizeQuestions` maps entity rows onto.
import { choicesOf, answerKind } from '../formFill.js'

const props = defineProps({
  questions: { type: Array, required: true },
  // The parent's answer object, mutated in place. Vue allows this for an object prop's PROPERTIES,
  // and it is what keeps `v-model="answers[q.id]"` working across a dozen input kinds without a
  // per-question emit that every one of them would have to remember to fire.
  answers: { type: Object, required: true },
  disabled: Boolean,
})

const scale = [1, 2, 3, 4, 5]

function kindOf(q) { return answerKind(q.answerType) }
function optionsOf(q) { return choicesOf(q.options) }
</script>

<template>
  <div>
    <div v-for="(q, i) in props.questions" :key="q.id" class="q-block">
      <div class="q-label">
        {{ i + 1 }}. {{ q.text || '(question)' }}
        <span v-if="q.required" class="req" aria-label="Required">*</span>
      </div>

      <v-radio-group v-if="kindOf(q) === 'boolean'" v-model="props.answers[q.id]" inline hide-details
        density="compact" :disabled="props.disabled">
        <v-radio label="Yes" :value="true" />
        <v-radio label="No" :value="false" />
      </v-radio-group>

      <v-radio-group v-else-if="kindOf(q) === 'single'" v-model="props.answers[q.id]" hide-details
        density="compact" :disabled="props.disabled">
        <v-radio v-for="c in optionsOf(q)" :key="c.value" :label="c.label" :value="c.value" />
        <div v-if="!optionsOf(q).length" class="text-caption text-medium-emphasis">
          No options configured.
        </div>
      </v-radio-group>

      <template v-else-if="kindOf(q) === 'multi'">
        <v-checkbox v-for="c in optionsOf(q)" :key="c.value" v-model="props.answers[q.id]"
          :label="c.label" :value="c.value" hide-details density="compact" :disabled="props.disabled" />
        <div v-if="!optionsOf(q).length" class="text-caption text-medium-emphasis">
          No options configured.
        </div>
      </template>

      <v-btn-toggle v-else-if="kindOf(q) === 'scale'" v-model="props.answers[q.id]" divided
        variant="outlined" color="primary" density="comfortable" :disabled="props.disabled">
        <v-btn v-for="n in scale" :key="n" :value="n">{{ n }}</v-btn>
      </v-btn-toggle>

      <v-textarea v-else-if="kindOf(q) === 'longtext'" v-model="props.answers[q.id]" variant="outlined"
        rows="3" auto-grow hide-details :disabled="props.disabled" />

      <v-text-field v-else-if="kindOf(q) === 'number'" v-model="props.answers[q.id]" type="number"
        variant="outlined" density="comfortable" hide-details :disabled="props.disabled" />

      <v-text-field v-else-if="kindOf(q) === 'date'" v-model="props.answers[q.id]" type="date"
        variant="outlined" density="comfortable" hide-details :disabled="props.disabled" />

      <v-text-field v-else v-model="props.answers[q.id]" variant="outlined" density="comfortable"
        hide-details :disabled="props.disabled" />
    </div>
  </div>
</template>

<style scoped>
.q-block { margin-bottom: 20px; }
.q-label { font-weight: 600; margin-bottom: 6px; }
.req { color: rgb(var(--v-theme-error)); }
</style>
