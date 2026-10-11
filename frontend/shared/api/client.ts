/** ProblemDetails returned by the backend (RFC 7807). `errors` is present for 422 validation failures. */
export interface Problem {
  title?: string
  detail?: string
  status?: number
  errors?: Record<string, string[]>
  correlationId?: string
  /** 409 only: the version currently stored, and (for multi-section saves) which resource conflicted. */
  currentVersion?: string
  resource?: string
}

export class ApiError extends Error {
  constructor(
    public status: number,
    public problem: Problem,
  ) {
    super(problem.detail ?? problem.title ?? `Request failed (${status})`)
  }
}

let tokenProvider: () => string | null = () => null
let onUnauthorized: () => void = () => {}

export function configureApi(options: { getToken: () => string | null; onUnauthorized: () => void }) {
  tokenProvider = options.getToken
  onUnauthorized = options.onUnauthorized
}

function authHeaders(token: string | null): Record<string, string> {
  return { ...(token ? { Authorization: `Bearer ${token}` } : {}), 'X-Correlation-Id': crypto.randomUUID() }
}

async function failure(response: Response, token: string | null): Promise<never> {
  const problem = (await response.json().catch(() => ({}))) as Problem
  // An expired/invalid token (not a failed login attempt) sends the user back to the login page.
  if (response.status === 401 && token) onUnauthorized()
  throw new ApiError(response.status, problem)
}

export interface ApiInit {
  method?: string
  body?: unknown
  /** Opaque version token of the resource being changed; sent as a quoted If-Match header (optimistic concurrency). */
  ifMatch?: string
}

export async function api<T>(path: string, init: ApiInit = {}): Promise<T> {
  const token = tokenProvider()
  const response = await fetch(path, {
    method: init.method ?? (init.body === undefined ? 'GET' : 'POST'),
    headers: {
      Accept: 'application/json',
      ...(init.body === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...(init.ifMatch ? { 'If-Match': `"${init.ifMatch}"` } : {}),
      ...authHeaders(token),
    },
    body: init.body === undefined ? undefined : JSON.stringify(init.body),
  })

  if (response.ok) return (response.status === 204 ? undefined : await response.json()) as T
  return failure(response, token)
}

/** Multipart upload of one file (field name "file"). */
export async function uploadFile<T>(path: string, file: File): Promise<T> {
  const token = tokenProvider()
  const form = new FormData()
  form.append('file', file)

  const response = await fetch(path, { method: 'POST', headers: { Accept: 'application/json', ...authHeaders(token) }, body: form })
  if (response.ok) return (await response.json()) as T
  return failure(response, token)
}

/** Authenticated download: the bearer token cannot ride on a plain link, so fetch the bytes and save them. */
export async function downloadFile(path: string, fileName: string): Promise<void> {
  const token = tokenProvider()
  const response = await fetch(path, { headers: authHeaders(token) })
  if (!response.ok) return failure(response, token)

  const url = URL.createObjectURL(await response.blob())
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  document.body.appendChild(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
}
