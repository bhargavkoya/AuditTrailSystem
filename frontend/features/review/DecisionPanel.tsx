import { useState } from 'react'
import { ApiError } from '@shared/api/client'
import { useDecide } from './api'

/**
 * Approve / reject, rendered only when the backend says the reviewer may decide (canDecide). The reject comment is
 * mandatory on the server (400); an empty one is sent as is so the server's message is what the user reads.
 */
export function DecisionPanel({ engagementId }: { engagementId: string }) {
  const [comment, setComment] = useState('')
  const approve = useDecide(engagementId, 'approve')
  const reject = useDecide(engagementId, 'reject')
  const pending = approve.isPending || reject.isPending
  const error = [approve.error, reject.error].find((e): e is ApiError => e instanceof ApiError) ?? null

  return (
    <section aria-label="Review decision" className="rounded-lg border-2 border-amber-400 bg-white p-4">
      <h2 className="font-semibold">Your decision</h2>
      <label htmlFor="decision-comment" className="mt-3 block text-sm font-medium">Comment <span className="font-normal text-slate-500">(required to reject)</span></label>
      <textarea
        id="decision-comment"
        value={comment}
        onChange={(e) => setComment(e.target.value)}
        rows={3}
        maxLength={2000}
        className="mt-1 w-full rounded border px-3 py-2 text-sm"
      />

      {error && (
        <p role="alert" className="mt-2 rounded bg-red-50 px-3 py-2 text-sm text-red-700">
          {error.status === 409 ? 'A decision has already been recorded for this review.' : error.status === 403 ? 'You cannot decide on this engagement right now.' : error.problem.detail ?? error.message}
        </p>
      )}

      <div className="mt-3 flex gap-3">
        <button type="button" disabled={pending} onClick={() => approve.mutate(comment)} className="rounded bg-emerald-700 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-800 disabled:opacity-40">
          Approve
        </button>
        <button type="button" disabled={pending} onClick={() => reject.mutate(comment)} className="rounded bg-red-700 px-4 py-2 text-sm font-medium text-white hover:bg-red-800 disabled:opacity-40">
          Reject
        </button>
      </div>
    </section>
  )
}
