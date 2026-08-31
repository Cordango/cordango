<script setup>
// The form a stranger opens from a shared link. No account, no shell, no navigation out of it.
//
// Everything it knows comes from one anonymous endpoint, and everything it shows is what that
// endpoint chose to say — there is no app model here, no entity key, and nothing about the record the
// submission files. The page cannot name a field even if it wanted to: it posts answers keyed by
// question id, and the server derives the rest from the template the token resolved to.
import { ref, computed, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { publicForm, formChallenge, submitForm } from '../publicApi.js'
import { solve } from '../pow.js'
import {
  initialAnswers, firstUnanswered, buildAnswers, challengeExpired, challengeWaitMs,
} from '../formFill.js'
import FormFields from '../blocks/FormFields.vue'

const route = useRoute()
const token = computed(() => String(route.params.token || ''))

const form = ref(null)
const loading = ref(true)
const notFound = ref(false)
const answers = ref({})
const submitting = ref(false)
const error = ref('')
const done = ref(false)

// The honeypot. Positioned off-screen, never announced, and filled in only by something that reads
// the DOM and answers every input it finds. Not a security control — anything that reads the CSS
// skips it — but it costs a real visitor nothing and turns away the drive-by spam a public form
// actually receives. Named `website` because that is the name naive bots look for.
const website = ref('')

const questions = computed(() => form.value?.questions ?? [])

// Minted on LOAD rather than at submit, and that ordering is load-bearing: this endpoint refuses a
// challenge less than three seconds old, so one fetched at the moment somebody clicks Submit would
// be rejected as a script every single time. Fetched here, it ages while they read the questions.
const challenge = ref(null)

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

async function mint() {
  try {
    const c = await formChallenge(token.value)
    challenge.value = { ...c, gotAt: Date.now() }
  } catch {
    // Not shown: submit mints one itself if this failed, and a visitor who has filled nothing in
    // should not be told about a request they did not make.
    challenge.value = null
  }
}

async function load() {
  loading.value = true
  notFound.value = false
  try {
    form.value = await publicForm(token.value)
    answers.value = initialAnswers(form.value.questions ?? [])
  } catch {
    // Says nothing about WHICH way of not existing it was, because the API says nothing either: an
    // unknown token and an unpublished form are one constant 404 by design.
    notFound.value = true
  } finally {
    loading.value = false
  }
  if (!notFound.value) await mint()
}

// A challenge that is usable: not near expiry, and old enough to spend. Two separate problems with
// separate answers — an expired one is replaced, a young one is waited out — and after a replacement
// the wait applies again, which is why this reads the clock twice.
async function usable() {
  if (challengeExpired(challenge.value)) await mint()
  if (!challenge.value) throw new Error('This form could not be prepared. Please try again.')
  const wait = challengeWaitMs(challenge.value)
  if (wait > 0) await sleep(wait)
  return challenge.value
}

async function post() {
  const c = await usable()
  // Solved after the visitor has committed to submitting: doing it on page load would burn a
  // stranger's battery for a page they may only be reading.
  const solution = await solve(c.token, c.difficulty)
  return submitForm(token.value, {
    answers: buildAnswers(questions.value, answers.value),
    challengeToken: c.token,
    solution,
    website: website.value || null,
  })
}

async function submit() {
  error.value = ''
  if (firstUnanswered(questions.value, answers.value)) {
    error.value = 'Please answer all required questions.'
    return
  }
  submitting.value = true
  try {
    try {
      await post()
    } catch (e) {
      // ONE retry, and only on the code that means the challenge itself was refused — a tab left
      // open past the fifteen minute life is the ordinary way to get here, and making somebody
      // retype a form because of it would be the page's fault. Any other failure is reported: a
      // rejected answer does not become valid by being sent twice.
      if (e?.body?.code !== 'forms.challenge_invalid') throw e
      challenge.value = null
      await post()
    }
    done.value = true
  } catch (e) {
    error.value = e?.body?.errors?.join(' ') || e?.message || 'Your answers could not be sent.'
  } finally {
    submitting.value = false
  }
}

onMounted(load)
</script>

<template>
  <v-main>
    <div class="pf">
      <div v-if="loading" class="pf-center">
        <v-progress-circular indeterminate color="primary" />
      </div>

      <v-card v-else-if="notFound" variant="tonal" class="pa-8 text-center">
        <div class="text-h6 mb-2">This link doesn't work</div>
        <div class="text-medium-emphasis">
          It may have been withdrawn, or the address may be mistyped.
        </div>
      </v-card>

      <v-card v-else-if="done" variant="tonal" color="success" class="pa-8 text-center">
        <v-icon icon="mdi-check-circle-outline" size="42" class="mb-3" />
        <div class="text-h6 mb-2">Thank you</div>
        <div>Your answers have been received.</div>
      </v-card>

      <v-card v-else rounded="lg" elevation="2" class="pa-8">
        <h1 class="pf-title">{{ form.name || 'Form' }}</h1>

        <div v-if="!questions.length" class="text-medium-emphasis py-4">
          This form has no questions yet.
        </div>

        <v-form v-else @submit.prevent="submit">
          <FormFields :questions="questions" :answers="answers" :disabled="submitting" />

          <!-- Off-screen rather than display:none, and never announced. A person cannot reach it by
               keyboard or by pointer; a script filling every input it finds walks straight into it. -->
          <v-text-field v-model="website" label="Website" name="website" autocomplete="off"
            tabindex="-1" aria-hidden="true" hide-details class="pf-hp" />

          <v-alert v-if="error" type="error" variant="tonal" density="compact" class="mb-4">
            {{ error }}
          </v-alert>

          <v-btn type="submit" color="primary" block size="large" class="text-none"
            :loading="submitting">Submit</v-btn>
        </v-form>
      </v-card>
    </div>
  </v-main>
</template>

<style scoped>
.pf { max-width: 720px; margin: 0 auto; padding: 32px 16px; }
.pf-center { display: flex; justify-content: center; padding: 80px 0; }
.pf-title { font-size: 24px; font-weight: 650; line-height: 1.25; margin: 0 0 20px; }
.pf-hp { position: absolute; left: -9999px; width: 1px; height: 1px; overflow: hidden; }

@media (max-width: 600px) {
  .pf { padding: 16px 12px; }
}
</style>
