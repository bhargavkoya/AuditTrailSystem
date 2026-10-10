import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { api, ApiError } from '@shared/api/client'
import type { EngagementDetail, Participant } from '@shared/api/types'
import { StatusBadge, roleLabel } from '@shared/ui/labels'

/** Phase 1 placeholder: the header and participants come from the backend. The five-tab workflow arrives in Phase 2. */
export function EngagementPage() {
  const { id = '' } = useParams()
  const engagement = useQuery({ queryKey: ['engagement', id], queryFn: () => api<EngagementDetail>(`/engagements/${id}`), retry: false })
  const participants = useQuery({ queryKey: ['participants', id], queryFn: () => api<Participant[]>(`/engagements/${id}/participants`), retry: false, enabled: engagement.isSuccess })

  if (engagement.isError) {
    const status = engagement.error instanceof ApiError ? engagement.error.status : 0
    return (
      <Shell>
        <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">
          {status === 403 ? 'You are not a participant of this engagement.' : status === 404 ? 'Engagement not found.' : 'Could not load the engagement.'}
        </p>
      </Shell>
    )
  }
  if (!engagement.data) return <Shell><p className="text-sm text-slate-500">Loading…</p></Shell>

  const e = engagement.data
  return (
    <Shell>
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold">{e.name}</h1>
        <StatusBadge status={e.status} />
        <span className="rounded border px-2 py-0.5 text-xs">My role: {roleLabel[e.myRole]}</span>
      </div>
      <p className="mt-1 text-sm text-slate-500">
        <span className="font-mono">{e.engagementId}</span> · {e.region} · {e.periodType} · Reviewer: {e.reviewer.displayName}
      </p>

      {e.allowedActions.includes('review') && (
        <Link to={`/engagements/${e.engagementId}/review`} className="mt-4 inline-block rounded bg-amber-500 px-4 py-2 text-sm font-medium text-white hover:bg-amber-600">
          Go to review screen
        </Link>
      )}

      <section className="mt-6">
        <h2 className="text-sm font-medium uppercase tracking-wide text-slate-500">Participants</h2>
        <ul className="mt-2 divide-y rounded-lg border bg-white">
          {participants.data?.map((p) => (
            <li key={p.userId} className="flex justify-between px-4 py-2 text-sm">
              <span>{p.displayName}</span>
              <span className="text-slate-500">{roleLabel[p.role]}</span>
            </li>
          ))}
        </ul>
      </section>

      <p className="mt-8 rounded-lg border border-dashed p-6 text-center text-sm text-slate-400">
        The five-tab workflow (Input Configuration → Reconciliation &amp; Submission) arrives in Phase 2.
      </p>
    </Shell>
  )
}

function Shell({ children }: { children: React.ReactNode }) {
  return (
    <div className="mx-auto max-w-4xl p-6">
      <Link to="/" className="text-sm text-slate-500 hover:underline">← Back to dashboard</Link>
      <div className="mt-3">{children}</div>
    </div>
  )
}
