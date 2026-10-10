import { useState, type FormEvent } from 'react'
import { Navigate, useLocation } from 'react-router-dom'
import { ApiError } from '../api/client'
import { useAuth } from './AuthProvider'

// Demo convenience only: the seeded users from the backend (password is documented in the README).
const DEMO_USERS = [
  { email: 'alice@auditflow.test', label: 'Alice (Auditor)' },
  { email: 'bob@auditflow.test', label: 'Bob (Co-Auditor)' },
  { email: 'rachel@auditflow.test', label: 'Rachel (Reviewer)' },
  { email: 'rohan@auditflow.test', label: 'Rohan (Reviewer)' },
  { email: 'victor@auditflow.test', label: 'Victor (Viewer)' },
]

export function LoginPage() {
  const { user, login } = useAuth()
  const location = useLocation()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  if (user) return <Navigate to={(location.state as { from?: string } | null)?.from ?? '/'} replace />

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await login(email, password)
    } catch (err) {
      setError(err instanceof ApiError && err.status === 401 ? 'Invalid email or password.' : 'Could not sign in. Is the stack running?')
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="mx-auto mt-24 max-w-sm rounded-lg border bg-white p-6 shadow-sm">
      <h1 className="text-xl font-semibold">AuditFlow</h1>
      <p className="mt-1 text-sm text-slate-500">Sign in to continue</p>

      <form onSubmit={submit} className="mt-5 space-y-3">
        <label className="block text-sm">
          Email
          <input className="mt-1 w-full rounded border px-3 py-2" type="email" value={email} onChange={(e) => setEmail(e.target.value)} required autoFocus />
        </label>
        <label className="block text-sm">
          Password
          <input className="mt-1 w-full rounded border px-3 py-2" type="password" value={password} onChange={(e) => setPassword(e.target.value)} required />
        </label>
        {error && <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>}
        <button disabled={busy} className="w-full rounded bg-slate-900 px-3 py-2 text-sm font-medium text-white hover:bg-slate-700 disabled:opacity-50">
          {busy ? 'Signing in…' : 'Sign in'}
        </button>
      </form>

      <div className="mt-6 border-t pt-4">
        <p className="text-xs font-medium uppercase tracking-wide text-slate-400">Demo users (password: Passw0rd!)</p>
        <div className="mt-2 flex flex-wrap gap-2">
          {DEMO_USERS.map((u) => (
            <button
              key={u.email}
              type="button"
              onClick={() => { setEmail(u.email); setPassword('Passw0rd!') }}
              className="rounded border px-2 py-1 text-xs hover:bg-slate-50"
            >
              {u.label}
            </button>
          ))}
        </div>
      </div>
    </main>
  )
}
