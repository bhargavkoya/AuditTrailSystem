import { ApiError } from '@shared/api/client'
import { ConflictBanner } from '@shared/conflict/ConflictBanner'
import { useConflictResolution } from '@shared/conflict/useConflictResolution'
import type { LatestChange } from '@shared/conflict/useRemoteChanges'
import type { EngagementDetail, ValidationSummary } from '@shared/api/types'
import { useSubmit } from './api'

/**
 * Submit for review. Whether the action exists is the backend's allowedActions; whether it succeeds is the backend's
 * readiness decision (a 422 lists the blocking issues, shown verbatim).
 */
export function SubmitBar({
  engagement,
  summary,
  hasUnsavedEdits,
  latestChange,
}: {
  engagement: EngagementDetail
  summary: ValidationSummary | undefined
  hasUnsavedEdits: boolean
  latestChange: LatestChange
}) {
  const submit = useSubmit(engagement.engagementId)
  const conflict = useConflictResolution()
  if (!engagement.allowedActions.includes('submit')) return null

  const error = submit.error instanceof ApiError ? submit.error : null
  const blocking = error?.status === 422 ? error.problem.issues ?? [] : []
  const message =
    error === null ? null
    : error.status === 422 ? error.problem.detail ?? 'Required validations must pass before submission.'
    : error.status === 403 ? 'This engagement can no longer be submitted.'
    : error.message

  return (
    <section aria-label="Submit for review" className="rounded-lg border bg-white p-4">
      <div className="flex flex-wrap items-center gap-3">
        <button
          type="button"
          onClick={() => {
            conflict.dismiss()
            // 409/403: the mutation already re-reads the engagement, so the page shows the latest state; say who changed it.
            submit.mutate(undefined, {
              onError: (e) => {
                if (!(e instanceof ApiError) || e.status !== 409) return
                const change = latestChange((c) => c.kind === 'section' || c.kind === 'configuration' || c.kind === 'engagement' || c.kind === 'decision')
                conflict.open({ subject: 'this engagement', by: change?.by ?? null, at: change?.at ?? null, items: [] })
              },
            })
          }}
          disabled={submit.isPending || hasUnsavedEdits}
          className="rounded bg-emerald-700 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-800 disabled:opacity-40"
        >
          {submit.isPending ? 'Submitting…' : 'Submit for review'}
        </button>
        {hasUnsavedEdits && <span className="text-sm text-slate-500">Save your changes first.</span>}
        {!hasUnsavedEdits && summary && !summary.readyToSubmit && (
          <span className="text-sm text-slate-500">The backend will list anything that still blocks submission.</span>
        )}
      </div>

      {conflict.state && (
        <div className="mt-3">
          <ConflictBanner
            state={conflict.state}
            onTakeTheirs={() => {}}
            onKeepMine={() => {}}
            onDismiss={conflict.dismiss}
            emptyNote="The page now shows its latest state. Check the validation summary, then submit again."
          />
        </div>
      )}

      {message && (
        <div role="alert" className="mt-3 rounded border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-800">
          <p className="font-medium">{message}</p>
          {blocking.length > 0 && (
            <ul className="mt-1 list-disc pl-5">
              {blocking.map((issue, index) => <li key={`${issue.code}-${issue.sectionKey}-${issue.fieldKey}-${index}`}>{issue.message}</li>)}
            </ul>
          )}
        </div>
      )}
    </section>
  )
}
