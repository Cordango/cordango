// Talking to the API as NOBODY.
//
// Every public call goes through this module and none of them touch the shared `api` client: that
// one carries the session cookie and the antiforgery token, and a page anyone can open must not send
// either. Raw fetch with `credentials: 'omit'` cannot do it by accident.
async function call(path, init = {}) {
  const res = await fetch(path, {
    ...init,
    credentials: 'omit',
    headers: { Accept: 'application/json', ...(init.headers || {}) },
  })
  const text = await res.text()
  let body = null
  try { body = text ? JSON.parse(text) : null } catch { /* a non-JSON error page stays null */ }
  if (!res.ok) {
    const error = new Error(body?.error || `Request failed (${res.status})`)
    error.status = res.status
    // The parsed body, for the caller that needs more than one sentence: `code` is the stable reason
    // a form retries its proof of work on, `errors` the per-question list.
    error.body = body
    throw error
  }
  return body
}

const at = (token) => `/api/public/forms/${encodeURIComponent(token)}`

/** The form itself: its name, and its questions in the order they are asked. */
export const publicForm = (token) => call(at(token))

export const formChallenge = (token) => call(`${at(token)}/challenge`)

export const submitForm = (token, payload) => call(at(token), {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(payload),
})
