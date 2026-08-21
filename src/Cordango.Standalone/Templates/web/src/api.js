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

/** The directory of people, for reference pickers and person chips. */
export async function loadPeople() {
  const page = await api.get('/api/directory/person?take=500')
  const items = page?.items ?? []
  return { items, byId: Object.fromEntries(items.map((p) => [p.id, p])) }
}
