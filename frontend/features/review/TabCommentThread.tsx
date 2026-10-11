import { useState, type FormEvent } from 'react'
import { ApiError } from '@shared/api/client'
import type { TabComment } from '@shared/api/types'
import { useAddComment } from './api'

const when = (iso: string) => new Date(iso).toLocaleString()

/** Flat, append-only comments for one tab. Earlier review cycles are collapsed; the form exists only when the backend allows commenting. */
export function TabCommentThread({
  engagementId,
  tabNo,
  comments,
  cycle,
  canComment,
}: {
  engagementId: string
  tabNo: number
  comments: TabComment[]
  cycle: number
  canComment: boolean
}) {
  const [body, setBody] = useState('')
  const add = useAddComment(engagementId)
  const forTab = comments.filter((c) => c.tabNo === tabNo)
  const current = forTab.filter((c) => c.cycle === cycle)
  const earlier = forTab.filter((c) => c.cycle !== cycle)
  const error = add.error instanceof ApiError ? add.error : null

  function submit(event: FormEvent) {
    event.preventDefault()
    add.mutate({ tabNo, body }, { onSuccess: () => setBody('') })
  }

  const render = (list: TabComment[]) => (
    <ul className="space-y-2">
      {list.map((c) => (
        <li key={c.commentId} className="rounded border bg-white px-3 py-2 text-sm">
          <p className="whitespace-pre-wrap">{c.body}</p>
          <p className="mt-1 text-xs text-slate-500">{c.authorName} · {when(c.createdAt)}</p>
        </li>
      ))}
    </ul>
  )

  return (
    <section aria-label="Tab comments" className="space-y-3">
      <h3 className="text-sm font-medium uppercase tracking-wide text-slate-500">Reviewer comments on this tab</h3>

      {current.length === 0 && <p className="text-sm text-slate-500">No comments in this review cycle.</p>}
      {current.length > 0 && render(current)}

      {earlier.length > 0 && (
        <details className="rounded border bg-slate-50 px-3 py-2">
          <summary className="cursor-pointer text-sm text-slate-600">Earlier review cycles ({earlier.length})</summary>
          <div className="mt-2">{render(earlier)}</div>
        </details>
      )}

      {canComment && (
        <form onSubmit={submit} className="space-y-2">
          <label className="block text-sm font-medium" htmlFor={`comment-${tabNo}`}>Add a comment</label>
          <textarea
            id={`comment-${tabNo}`}
            value={body}
            onChange={(e) => setBody(e.target.value)}
            rows={3}
            maxLength={2000}
            className="w-full rounded border px-3 py-2 text-sm"
          />
          {error && (
            <p role="alert" className="text-sm text-red-700">
              {error.problem.errors ? Object.values(error.problem.errors).flat().join(' ') : error.message}
            </p>
          )}
          <button type="submit" disabled={add.isPending || body.trim() === ''} className="rounded bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-700 disabled:opacity-40">
            {add.isPending ? 'Adding…' : 'Add comment'}
          </button>
        </form>
      )}
    </section>
  )
}
