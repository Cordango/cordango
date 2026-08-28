// The one way this application talks to its server.
//
// Everything goes through here so that three things are decided once instead of at every call site:
// the session cookie is sent, the antiforgery token is echoed on anything that changes state, and an
// error comes back as an object with a stable `code` rather than as a thrown string.

/** Read a cookie by name. The antiforgery token arrives as one, deliberately readable by script on
 *  this origin and by nothing else — which is the entire mechanism. */
function cookie(name) {
  const match = document.cookie.match(new RegExp('(^|; )' + name + '=([^;]*)'))
  return match ? decodeURIComponent(match[2]) : null
}

/** What the server refused, in a shape a caller can act on. */
export class ApiError extends Error {
  constructor(status, body) {
    super(body?.error || `Request failed (${status})`)
    this.name = 'ApiError'
    this.status = status
    this.code = body?.code || 'server.error'
    this.fields = body?.fields || []
  }
}

const UNSAFE = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])

async function request(method, path, body, options = {}) {
  const headers = { Accept: 'application/json', ...options.headers }

  // Every state-changing request carries the token. The server enforces this globally rather than
  // per endpoint, so forgetting it here fails loudly on the first attempt instead of leaving one
  // quiet hole in an otherwise protected surface.
  if (UNSAFE.has(method)) {
    const token = cookie('XSRF-TOKEN')
    if (token) headers['X-XSRF-TOKEN'] = token
  }

  let payload
  if (body instanceof FormData) {
    // Let the browser set the multipart boundary; naming the content type here breaks the upload.
    payload = body
  } else if (body !== undefined) {
    headers['Content-Type'] = 'application/json'
    payload = JSON.stringify(body)
  }

  const response = await fetch(path, {
    method,
    headers,
    body: payload,
    // The session lives in a cookie, so it has to be sent. Same origin, so this is not a
    // cross-origin credential grant.
    credentials: 'same-origin',
  })

  if (response.status === 204) return null

  const text = await response.text()
  let parsed = null
  if (text) {
    try {
      parsed = JSON.parse(text)
    } catch {
      // A non-JSON body from an API route means something upstream answered instead of the
      // application — a proxy error page, most often. Say that, rather than reporting a parse
      // failure at character 0 and sending the reader looking at their own payload.
      throw new ApiError(response.status, {
        code: 'server.unexpected_response',
        error: `The server answered ${response.status} with something that was not JSON.`,
      })
    }
  }

  if (!response.ok) throw new ApiError(response.status, parsed)
  return parsed
}

export const api = {
  get: (path, options) => request('GET', path, undefined, options),
  post: (path, body, options) => request('POST', path, body, options),
  put: (path, body, options) => request('PUT', path, body, options),
  patch: (path, body, options) => request('PATCH', path, body, options),
  delete: (path, options) => request('DELETE', path, undefined, options),

  /** Upload a file and get back the reference an attachment field stores. */
  upload(file) {
    const form = new FormData()
    form.append('file', file)
    return request('POST', '/api/media', form)
  },
}

/** Where a stored file lives — what `@cordango/web-controls` needs for its media seam. */
export const mediaUrl = (reference) => (reference ? `/api/media/${reference}` : null)

/**
 * One person's saved layout for one table — which columns, in what order, how dense.
 *
 * The other half of `@cordango/web-controls`'s table-settings seam. Both halves are deliberately
 * quiet, and for different reasons.
 *
 * READING never throws. Having no saved layout is the ordinary state of every table the first time
 * anybody opens it, and the endpoint answers 401 rather than 200 once a session has expired — so a
 * client that treated either as an error would turn "you have been signed out" into a table that
 * renders nothing at all, on every table on the page at once.
 *
 * WRITING is fire-and-forget, which is the seam's own contract: a preference is a convenience, and
 * a failed convenience must never block a render or surface a message about column widths.
 */
export async function loadTableSettings(handle, tableKey) {
  try {
    return await api.get(`/api/settings/table/${encodeURIComponent(handle)}/${encodeURIComponent(tableKey)}`)
  } catch {
    return null
  }
}

export function saveTableSettings(handle, tableKey, settings) {
  api.put(`/api/settings/table/${encodeURIComponent(handle)}/${encodeURIComponent(tableKey)}`, settings)
    .catch(() => {})
}

/**
 * One person's name, asked for once however many things on the page want it.
 *
 * The cache is keyed on the id and holds the PROMISE, not the answer: a table of thirty rows
 * mentioning the same six people renders in one tick, so caching only on resolve would still send
 * thirty requests before the first one came back.
 */
const names = new Map()

export function personName(id) {
  if (!id) return Promise.resolve('')
  if (names.has(id)) return Promise.resolve(names.get(id))

  const pending = api
    .get(`/api/directory/person/${encodeURIComponent(id)}`)
    .then((p) => p?.full_name || id)
    // Somebody who has left, or a reference this role cannot read. The id is a worse answer than a
    // name and a better one than a blank space.
    .catch(() => id)
    .then((name) => {
      names.set(id, name)
      return name
    })

  names.set(id, pending)
  return pending
}

/** The directory of people, for reference pickers and person chips. */
export async function loadPeople() {
  const page = await api.get('/api/directory/person?take=500')
  const items = page?.items ?? []
  return { items, byId: Object.fromEntries(items.map((p) => [p.id, p])) }
}
