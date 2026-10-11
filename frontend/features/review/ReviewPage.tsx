import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { ApiError } from '@shared/api/client'
import { ConflictBanner } from '@shared/conflict/ConflictBanner'
import { useEngagementStream } from '@shared/stream/useEngagementStream'
import { StatusBadge, roleLabel } from '@shared/ui/labels'
import { useAttachments } from '@features/engagement/api'
import { ValidationBanners } from '@features/engagement/ValidationBanners'
import { useReview } from './api'
import { DecisionPanel } from './DecisionPanel'
import { ReadOnlyTabView } from './ReadOnlyTabView'
import { ReviewHistory } from './ReviewHistory'
import { TabCommentThread } from './TabCommentThread'

const CAP_ATTACH_VIEW = 'attachment.view'
const noDrafts = () => false

/**
 * The review screen: a distinct view (own colour scheme) over the same engagement. Content is read-only, comments are
 * per tab, and the decision panel appears only when the backend returns canDecide. All state comes from GET /reviews/{id}.
 */
export function ReviewPage() {
  const { id = '' } = useParams()
  const [selected, setSelected] = useState(1)
  const [alreadyDecided, setAlreadyDecided] = useState(false)
  const review = useReview(id)
  const live = useEngagementStream(id, { hasDraft: noDrafts, onExternalChange: () => {} })
  const canViewAttachments = review.data?.engagement.capabilities.includes(CAP_ATTACH_VIEW) ?? false
  const attachments = useAttachments(id, canViewAttachments)

  if (review.isError) {
    const status = review.error instanceof ApiError ? review.error.status : 0
    return (
      <Shell id={id}>
        <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">
          {status === 403 ? 'You do not have access to the review of this engagement.' : status === 404 ? 'Engagement not found.' : 'Could not load the review.'}
        </p>
      </Shell>
    )
  }
  if (!review.data) return <Shell id={id}><p className="text-sm text-slate-500">Loading…</p></Shell>

  const view = review.data
  const e = view.engagement
  const tab = view.tabs.find((t) => t.tabNo === selected) ?? view.tabs[0]
  const tabTitles = Object.fromEntries(view.tabs.map((t) => [t.tabNo, t.title]))
  const currentComments = (tabNo: number) => view.comments.filter((c) => c.tabNo === tabNo && c.cycle === view.cycle).length
  const recorded = view.history.find((d) => d.cycle === view.cycle)
  const decidedThisCycle = recorded !== undefined

  return (
    <Shell id={id}>
      <header className="rounded-lg bg-amber-100 px-5 py-4">
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-2xl font-semibold">Review · {e.name}</h1>
          <StatusBadge status={e.status} />
          <span className="rounded border border-amber-400 px-2 py-0.5 text-xs">My role: {roleLabel[e.myRole]}</span>
          <span role="status" className={`flex items-center gap-1 text-xs ${live === 'live' ? 'text-emerald-700' : 'text-slate-500'}`}>
            <span className={`h-2 w-2 rounded-full ${live === 'live' ? 'bg-emerald-500' : 'bg-slate-300'}`} />
            {live === 'live' ? 'Live' : live === 'connecting' ? 'Connecting…' : 'Offline'}
          </span>
        </div>
        <p className="mt-1 text-sm text-slate-700">
          <span className="font-mono">{e.engagementId}</span> · {e.region} · {e.periodType} · Reviewer: {e.reviewer.displayName}
          {view.cycle > 0 ? ` · Review cycle ${view.cycle}` : ' · Not submitted yet'}
        </p>
        <p className="mt-2 text-xs text-slate-600">Read-only view. Content cannot be changed from here.</p>
      </header>

      {alreadyDecided && recorded && (
        <div className="mt-4">
          <ConflictBanner
            state={{ subject: 'this review', by: recorded.reviewerName, at: Date.parse(recorded.decidedAt), items: [] }}
            onTakeTheirs={() => {}}
            onKeepMine={() => {}}
            onDismiss={() => setAlreadyDecided(false)}
            emptyNote={`It was already decided in this cycle: ${recorded.decision}${recorded.comment ? ` ("${recorded.comment}")` : ''}. Your decision was not applied.`}
          />
        </div>
      )}

      {decidedThisCycle && e.status === 'UnderReview' && (
        <p role="status" className="mt-4 rounded border border-amber-300 bg-amber-50 px-4 py-2 text-sm text-amber-900">
          Decision recorded. The engagement status is being updated…
        </p>
      )}

      <div className="mt-5 grid gap-5 lg:grid-cols-[1fr_20rem]">
        <div className="space-y-5">
          <nav role="tablist" aria-label="Review tabs" className="flex flex-wrap gap-1 border-b border-amber-300">
            {view.tabs.map((t) => {
              const active = t.tabNo === tab?.tabNo
              const count = currentComments(t.tabNo)
              return (
                <button
                  key={t.tabNo}
                  role="tab"
                  aria-selected={active}
                  onClick={() => setSelected(t.tabNo)}
                  className={`-mb-px flex items-center gap-2 rounded-t border px-3 py-2 text-sm ${active ? 'border-amber-300 border-b-white bg-white font-medium' : 'border-transparent text-slate-600 hover:bg-amber-50'}`}
                >
                  <span className="text-xs text-slate-400">{t.tabNo}</span>
                  {t.title}
                  {count > 0 && <span title={`${count} comment(s)`} className="rounded-full bg-amber-500 px-1.5 text-xs font-semibold text-white">{count}</span>}
                  {!t.available && <span title="Unavailable" className="text-xs text-red-600">!</span>}
                </button>
              )
            })}
          </nav>

          {tab && (
            <div className="space-y-5">
              <ReadOnlyTabView engagementId={id} tab={tab} attachments={attachments.data ?? []} canViewAttachments={canViewAttachments} />
              <TabCommentThread engagementId={id} tabNo={tab.tabNo} comments={view.comments} cycle={view.cycle} canComment={view.canComment} />
            </div>
          )}
        </div>

        <aside className="space-y-5">
          {view.canDecide && <DecisionPanel engagementId={id} onAlreadyDecided={() => setAlreadyDecided(true)} />}

          <section aria-label="Validation summary" className="space-y-2 rounded-lg border bg-white p-4">
            <h2 className="font-semibold">Validation summary</h2>
            {view.validation ? (
              <>
                <p className="text-sm text-slate-600">
                  {view.validation.counts.errors} errors · {view.validation.counts.warnings} warnings · {view.validation.counts.infos} information
                </p>
                <ValidationBanners summary={view.validation} tabTitles={tabTitles} onNavigate={(issue) => issue.tabNo !== null && setSelected(issue.tabNo)} />
              </>
            ) : (
              <p className="text-sm text-amber-800">The validation summary is unavailable right now.</p>
            )}
          </section>

          <ReviewHistory history={view.history} />
        </aside>
      </div>
    </Shell>
  )
}

function Shell({ id, children }: { id: string; children: React.ReactNode }) {
  return (
    <div className="min-h-screen bg-amber-50/50">
      <div className="mx-auto max-w-6xl p-6">
        <Link to={`/engagements/${id}`} className="text-sm text-slate-600 hover:underline">← Back to engagement</Link>
        <div className="mt-3">{children}</div>
      </div>
    </div>
  )
}
