// Filling in a form, as pure functions.
//
// Two callers share this and have nothing else in common: the in-app intake block, which reads its
// questions through the authenticated API, and the public page, whose questions arrive already
// projected through the field roles. Both normalise to the same question shape and post the same
// answer payload — a public submission assembled differently from an internal one would be a second,
// unreviewed contract with an endpoint that validates them both the same way.
//
// The answer shapes below are the ones FormSubmissionService.Fits accepts. Coercion in particular is
// not cosmetic: a number question bound to a text input yields a STRING, and the server checks the
// JSON value kind rather than whether the text happens to parse.

// Map a question's declared answer type to a renderer kind. Loose, because the author names the
// option values and "Yes / No" is as likely a spelling as "yes_no".
export function answerKind(typeValue) {
  const t = String(typeValue || '').toLowerCase()
  if (/multi/.test(t)) return 'multi'
  if (/single|choice|select|radio|dropdown/.test(t)) return 'single'
  if (/yes|no|bool|toggle/.test(t)) return 'boolean'
  if (/scale|rating|nps|star|likert/.test(t)) return 'scale'
  if (/long|paragraph|comment|textarea/.test(t)) return 'longtext'
  if (/number|numeric|int|decimal/.test(t)) return 'number'
  if (/date|time/.test(t)) return 'date'
  return 'text'
}

// Options as authored -> [{value, label}]. Three shapes arrive here and all three are legitimate: a
// json array of plain strings, an array of {value,label} objects, and the newline/comma text
// somebody typed into a choices box.
export function choicesOf(options) {
  const list = Array.isArray(options)
    ? options.map((o) => (o && typeof o === 'object')
      ? { value: o.value ?? o.label, label: o.label ?? o.value }
      : { value: o, label: o })
    : typeof options === 'string' && options.trim()
      ? options.split(/[\n,]/).map((s) => s.trim()).filter(Boolean).map((v) => ({ value: v, label: v }))
      : []
  return list.filter((c) => c.value != null && c.value !== '')
}

export function isBlank(v) {
  return v == null || v === '' || (Array.isArray(v) && v.length === 0)
}

// A multi-choice question needs an array before v-model touches it, or the first box ticked replaces
// the value instead of joining it.
export function initialAnswers(questions) {
  const answers = {}
  for (const q of questions || []) if (answerKind(q.answerType) === 'multi') answers[q.id] = []
  return answers
}

export function coerce(kind, value) {
  if (isBlank(value)) return value
  if (kind === 'number' || kind === 'scale') {
    if (typeof value === 'number') return value
    const n = Number(String(value).trim())
    // An unparseable number is passed through untouched: the server names the question that is
    // wrong, which beats this guessing and sending something plausible instead.
    return Number.isFinite(n) ? n : value
  }
  if (kind === 'multi') return Array.isArray(value) ? value : [value]
  return value
}

export function firstUnanswered(questions, answers) {
  return (questions || []).find((q) => q.required && isBlank(answers?.[q.id])) || null
}

// Blank answers are DROPPED rather than sent as null: an unanswered optional question should leave
// no answer row behind, and the server treats a present-but-empty answer as an answer.
export function buildAnswers(questions, answers) {
  const payload = {}
  for (const q of questions || []) {
    const v = answers?.[q.id]
    if (isBlank(v)) continue
    payload[q.id] = coerce(answerKind(q.answerType), v)
  }
  return payload
}

// Rows of the question ENTITY, as the in-app block reads them, mapped onto the shape the public
// endpoint already returns. One shape reaches the inputs, whichever door the questions came in.
export function normalizeQuestions(rows, keys) {
  const source = keys?.order
    ? [...(rows || [])].sort((a, b) => (Number(a?.[keys.order]) || 0) - (Number(b?.[keys.order]) || 0))
    : [...(rows || [])]
  return source.map((r) => ({
    id: r?.id,
    text: keys?.text ? r?.[keys.text] : null,
    answerType: keys?.type ? r?.[keys.type] : null,
    required: keys?.required ? !!r?.[keys.required] : false,
    options: keys?.options ? r?.[keys.options] : null,
  }))
}

// --- the proof-of-work challenge's two clocks ---
//
// A challenge lives fifteen minutes and must be at least three seconds OLD when it is spent: a
// submission arriving in the same breath as its challenge is a script. So the page mints one on load
// and lets it age while somebody reads the questions. Two questions rather than one "is it usable",
// because the answers differ — an expired challenge is replaced, a young one is waited out.
//
// 3200 rather than 3000 because the client can only start counting once the response has ARRIVED,
// which is strictly after the server minted it. Measuring the shorter interval and clearing the
// threshold by a margin is what makes this safe without a synchronised clock.
export const CHALLENGE_MIN_AGE_MS = 3200
export const CHALLENGE_RENEW_BEFORE_MS = 60000

export function challengeExpired(challenge, now = Date.now()) {
  if (!challenge?.expiresAt) return true
  const expires = Date.parse(challenge.expiresAt)
  return !Number.isFinite(expires) || expires - now < CHALLENGE_RENEW_BEFORE_MS
}

export function challengeWaitMs(challenge, now = Date.now()) {
  if (!challenge?.gotAt) return 0
  return Math.max(0, CHALLENGE_MIN_AGE_MS - (now - challenge.gotAt))
}
