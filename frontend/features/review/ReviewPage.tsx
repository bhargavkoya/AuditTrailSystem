import { Link, useParams } from 'react-router-dom'

/** Phase 1 placeholder. The distinct review screen (read-only tabs, comments, approve/reject) arrives in Phase 3. */
export function ReviewPage() {
  const { id = '' } = useParams()
  return (
    <div className="mx-auto max-w-4xl p-6">
      <Link to={`/engagements/${id}`} className="text-sm text-slate-500 hover:underline">← Back to engagement</Link>
      <div className="mt-3 rounded-lg border-2 border-amber-400 bg-amber-50 p-6">
        <h1 className="text-xl font-semibold">Review · <span className="font-mono">{id}</span></h1>
        <p className="mt-2 text-sm text-slate-600">The review screen (comments, approve, reject) arrives in Phase 3.</p>
      </div>
    </div>
  )
}
