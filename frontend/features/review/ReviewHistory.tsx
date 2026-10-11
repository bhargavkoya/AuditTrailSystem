import type { ReviewDecision } from '@shared/api/types'

/** Review-decision history, one row per review cycle (newest first). */
export function ReviewHistory({ history }: { history: ReviewDecision[] }) {
  return (
    <section aria-label="Review history" className="rounded-lg border bg-white p-4">
      <h2 className="font-semibold">Decision history</h2>
      {history.length === 0 ? (
        <p className="mt-2 text-sm text-slate-500">No decisions yet.</p>
      ) : (
        <ol className="mt-2 space-y-3">
          {[...history].reverse().map((d) => (
            <li key={d.decisionId} className="text-sm">
              <p>
                <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${d.decision === 'Approved' ? 'bg-emerald-100 text-emerald-800' : 'bg-red-100 text-red-800'}`}>{d.decision}</span>
                <span className="ml-2 text-slate-500">cycle {d.cycle} · {d.reviewerName} · {new Date(d.decidedAt).toLocaleString()}</span>
              </p>
              {d.comment && <p className="mt-1 whitespace-pre-wrap text-slate-700">{d.comment}</p>}
            </li>
          ))}
        </ol>
      )}
    </section>
  )
}
