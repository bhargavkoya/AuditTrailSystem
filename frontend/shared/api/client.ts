/** ProblemDetails returned by the backend (RFC 7807). `errors` is present for 422 validation failures. */
export interface Problem {
  title?: string
  detail?: string
  status?: number
  errors?: Record<string, string[]>
  correlationId?: string
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

export async function api<T>(path: string, init: { method?: string; body?: unknown } = {}): Promise<T> {
  const token = tokenProvider()
  const response = await fetch(path, {
    method: init.method ?? (init.body === undefined ? 'GET' : 'POST'),
    headers: {
      Accept: 'application/json',
      ...(init.body === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      'X-Correlation-Id': crypto.randomUUID(),
    },
    body: init.body === undefined ? undefined : JSON.stringify(init.body),
  })

  if (response.ok) return (response.status === 204 ? undefined : await response.json()) as T

  const problem = (await response.json().catch(() => ({}))) as Problem
  // An expired/invalid token (not a failed login attempt) sends the user back to the login page.
  if (response.status === 401 && token) onUnauthorized()
  throw new ApiError(response.status, problem)
}
