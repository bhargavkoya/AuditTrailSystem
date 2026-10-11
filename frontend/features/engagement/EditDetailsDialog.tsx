import { useState, type FormEvent } from 'react'
import { ApiError } from '@shared/api/client'
import type { EngagementDetail, UpdateEngagementRequest } from '@shared/api/types'
import { useCreationOptions, useUsers } from '../dashboard/api'
import { useUpdateEngagement } from './api'

/** Offered only when the backend lists the editDetails action (Draft engagements). Sends only what changed. */
export function EditDetailsDialog({ engagement, onClose }: { engagement: EngagementDetail; onClose: () => void }) {
  const options = useCreationOptions(true)
  const reviewers = useUsers(true, 'Reviewer')
  const update = useUpdateEngagement(engagement.engagementId)

  const [name, setName] = useState(engagement.name)
  const [periodType, setPeriodType] = useState(engagement.periodType)
  const [region, setRegion] = useState(engagement.region)
  const [reviewerUserId, setReviewerUserId] = useState(engagement.reviewer.userId)

  const error = update.error instanceof ApiError ? update.error : null
  const conflict = error?.status === 409
  const fieldErrors = error?.problem.errors ?? {}
  const general = error && !error.problem.errors && !conflict ? error.message : null

  async function submit(e: FormEvent) {
    e.preventDefault()
    const request: UpdateEngagementRequest = {}
    if (name.trim() !== engagement.name) request.name = name.trim()
    if (periodType !== engagement.periodType) request.periodType = periodType
    if (region !== engagement.region) request.region = region
    if (reviewerUserId !== engagement.reviewer.userId) request.reviewerUserId = reviewerUserId
    if (Object.keys(request).length === 0) return onClose()

    try {
      await update.mutateAsync({ request, version: engagement.version })
      onClose()
    } catch {
      /* shown through update.error */
    }
  }

  const input = 'mt-1 w-full rounded border px-3 py-2 text-sm'
  const Err = ({ name: key }: { name: string }) => (fieldErrors[key] ? <p className="mt-1 text-xs text-red-600">{fieldErrors[key].join(' ')}</p> : null)

  return (
    <div className="fixed inset-0 z-10 flex items-start justify-center overflow-y-auto bg-black/40 p-4" role="dialog" aria-modal="true" aria-label="Edit engagement details">
      <form onSubmit={submit} className="mt-8 w-full max-w-lg space-y-4 rounded-lg bg-white p-6 shadow-xl">
        <h2 className="text-lg font-semibold">Edit details</h2>
        <p className="text-xs text-slate-500">Details can only be changed while the engagement is a Draft.</p>

        <label className="block text-sm font-medium">
          Name
          <input className={input} value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} />
          <Err name="name" />
        </label>

        <div className="grid grid-cols-2 gap-3">
          <label className="block text-sm font-medium">
            Period type
            <select className={input} value={periodType} onChange={(e) => setPeriodType(e.target.value)}>
              {options.data?.periodTypes.map((p) => <option key={p}>{p}</option>)}
            </select>
            <Err name="periodType" />
          </label>
          <label className="block text-sm font-medium">
            Region
            <select className={input} value={region} onChange={(e) => setRegion(e.target.value)}>
              {options.data?.regions.map((r) => <option key={r}>{r}</option>)}
            </select>
            <Err name="region" />
          </label>
        </div>

        <label className="block text-sm font-medium">
          Reviewer
          <select className={input} value={reviewerUserId} onChange={(e) => setReviewerUserId(e.target.value)}>
            {reviewers.data?.map((u) => <option key={u.userId} value={u.userId}>{u.displayName}</option>)}
          </select>
          <Err name="reviewerUserId" />
        </label>

        {conflict && (
          <p role="alert" className="rounded bg-amber-50 px-3 py-2 text-sm text-amber-900">
            Someone else changed these details. Close this dialog to see the latest values, then edit again.
          </p>
        )}
        {general && <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{general}</p>}

        <div className="flex justify-end gap-2 pt-2">
          <button type="button" onClick={onClose} className="rounded border px-4 py-2 text-sm hover:bg-slate-50">Cancel</button>
          <button disabled={update.isPending} className="rounded bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-700 disabled:opacity-50">
            {update.isPending ? 'Saving…' : 'Save'}
          </button>
        </div>
      </form>
    </div>
  )
}
