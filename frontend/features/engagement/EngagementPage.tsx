import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { ApiError } from '@shared/api/client'
import { useAuth } from '@shared/auth/AuthProvider'
import { useRemoteChanges } from '@shared/conflict/useRemoteChanges'
import type { Issue } from '@shared/api/types'
import { useEngagementStream, type ExternalChange } from '@shared/stream/useEngagementStream'
import { StatusBadge, roleLabel } from '@shared/ui/labels'
import { useAttachments, useEngagement, useParticipants, useTabs, useValidationSummary } from './api'
import { AttachmentPanel } from './AttachmentPanel'
import { EditDetailsDialog } from './EditDetailsDialog'
import { SubmitBar } from './SubmitBar'
import { TabNav } from './TabNav'
import { TabView, type Drafts, type FocusTarget } from './TabView'
import { ValidationBanners } from './ValidationBanners'

// Capability names are issued by the backend (contracts/api Capabilities); the client only checks for their presence.
const CAP_ATTACH_VIEW = 'attachment.view'
const CAP_ATTACH_UPLOAD = 'attachment.upload'

/**
 * The engagement workspace: header, backend validation banners, five tabs, attachments and participants.
 * Everything shown (tabs, sections, editability, required flags, issues, upload rights) is decided by the backend.
 */
export function EngagementPage() {
  const { id = '' } = useParams()
  const [search, setSearch] = useSearchParams()
  const [drafts, setDrafts] = useState<Record<number, Drafts>>({})
  const [focus, setFocus] = useState<FocusTarget | null>(null)
  const [editing, setEditing] = useState(false)
  const [external, setExternal] = useState<ExternalChange[]>([])
  const queryClient = useQueryClient()
  const { user } = useAuth()
  const remoteChanges = useRemoteChanges()

  const engagement = useEngagement(id)
  const tabs = useTabs(id, engagement.isSuccess)
  const ready = tabs.data?.ready === true
  const summary = useValidationSummary(id, ready)
  const canViewAttachments = engagement.data?.capabilities.includes(CAP_ATTACH_VIEW) ?? false
  const attachments = useAttachments(id, ready && canViewAttachments)
  const participants = useParticipants(id, engagement.isSuccess)

  // Live updates: other users' changes arrive as events and are reconciled against what this page already shows.
  const draftsRef = useRef(drafts)
  draftsRef.current = drafts
  const live = useEngagementStream(id, {
    onRemoteChange: remoteChanges.record,
    selfUserId: user?.userId,
    hasDraft: (tabNo, sectionKey) => {
      const tabDrafts = draftsRef.current[tabNo] ?? {}
      return sectionKey ? Object.keys(tabDrafts[sectionKey] ?? {}).length > 0 : Object.values(tabDrafts).some((fields) => Object.keys(fields).length > 0)
    },
    onExternalChange: (change) => setExternal((all) => (all.some((c) => c.tabNo === change.tabNo && c.sectionKey === change.sectionKey) ? all : [...all, change])),
  })

  function reloadExternal(change: ExternalChange) {
    setDrafts((all) => {
      const tabDrafts = { ...(all[change.tabNo] ?? {}) }
      if (change.sectionKey) delete tabDrafts[change.sectionKey]
      return { ...all, [change.tabNo]: change.sectionKey ? tabDrafts : {} }
    })
    setExternal((all) => all.filter((c) => c.id !== change.id))
    void queryClient.invalidateQueries({ queryKey: ['tab', id, change.tabNo] })
  }

  const selected = Number(search.get('tab')) || 1
  const unsaved = useMemo(
    () => new Set(Object.entries(drafts).filter(([, d]) => Object.keys(d).length > 0).map(([tab]) => Number(tab))),
    [drafts],
  )

  useEffect(() => {
    if (unsaved.size === 0) return
    const warn = (e: BeforeUnloadEvent) => e.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [unsaved])

  const onFocused = useCallback(() => setFocus(null), [])
  const selectTab = (tabNo: number) => setSearch({ tab: String(tabNo) }, { replace: true })

  function navigateToIssue(issue: Issue) {
    if (issue.tabNo === null) return
    selectTab(issue.tabNo)
    setFocus({ tabNo: issue.tabNo, sectionKey: issue.sectionKey, fieldKey: issue.fieldKey })
  }

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
  const tabTitles = Object.fromEntries((tabs.data?.tabs ?? []).map((t) => [t.tabNo, t.title]))
  const current = attachments.data ?? []
  const engagementAttachment = current.find((a) => a.scope === 'Engagement') ?? null

  return (
    <Shell>
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-semibold">{e.name}</h1>
        <StatusBadge status={e.status} />
        <span className="rounded border px-2 py-0.5 text-xs">My role: {roleLabel[e.myRole]}</span>
        <span
          role="status"
          title={live === 'live' ? 'Receiving live updates' : live === 'connecting' ? 'Connecting to live updates…' : 'Live updates are off; reload to reconnect'}
          className={`flex items-center gap-1 text-xs ${live === 'live' ? 'text-emerald-700' : 'text-slate-400'}`}
        >
          <span className={`h-2 w-2 rounded-full ${live === 'live' ? 'bg-emerald-500' : 'bg-slate-300'}`} />
          {live === 'live' ? 'Live' : live === 'connecting' ? 'Connecting…' : 'Offline'}
        </span>
        {e.allowedActions.includes('editDetails') && (
          <button type="button" onClick={() => setEditing(true)} className="rounded border px-3 py-1 text-xs font-medium hover:bg-slate-100">
            Edit details
          </button>
        )}
        {e.allowedActions.includes('review') && (
          <Link to={`/engagements/${e.engagementId}/review`} className="rounded bg-amber-500 px-3 py-1 text-xs font-medium text-white hover:bg-amber-600">
            Go to review screen
          </Link>
        )}
        {e.allowedActions.includes('viewReview') && (
          <Link to={`/engagements/${e.engagementId}/review`} className="rounded border border-amber-400 px-3 py-1 text-xs font-medium text-amber-800 hover:bg-amber-50">
            View review
          </Link>
        )}
      </div>
      <p className="mt-1 text-sm text-slate-500">
        <span className="font-mono">{e.engagementId}</span> · {e.region} · {e.periodType} · Reviewer: {e.reviewer.displayName}
      </p>

      {tabs.isError && <p role="alert" className="mt-4 rounded bg-red-50 px-3 py-2 text-sm text-red-700">Could not load the workflow.</p>}

      {tabs.data && !ready && (
        <p role="status" className="mt-6 rounded-lg border border-dashed p-6 text-center text-sm text-slate-500">
          Setting up the workflow for this engagement… this page updates automatically.
        </p>
      )}

      {external.length > 0 && (
        <div className="mt-4 space-y-2">
          {external.map((change) => (
            <div key={change.id} role="alert" className="flex flex-wrap items-center gap-3 rounded border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900">
              <span>
                <strong>{change.by}</strong> changed {change.sectionKey ? 'a section you are editing' : 'the configuration while you have unsaved edits on that tab'}.
                Saving your edits now will report a conflict.
              </span>
              <button type="button" className="rounded border border-amber-400 bg-white px-3 py-1 text-xs font-medium hover:bg-amber-100" onClick={() => reloadExternal(change)}>
                Reload (discard my edits)
              </button>
              <button type="button" className="text-xs underline" onClick={() => setExternal((all) => all.filter((c) => c.id !== change.id))}>
                Keep my edits
              </button>
            </div>
          ))}
        </div>
      )}

      {ready && (
        <div className="mt-5 space-y-5">
          <SubmitBar engagement={e} summary={summary.data} hasUnsavedEdits={unsaved.size > 0} latestChange={remoteChanges.latest} />

          {summary.data && (
            <>
              <p className="text-sm">
                <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${summary.data.readyToSubmit ? 'bg-emerald-100 text-emerald-800' : 'bg-red-100 text-red-800'}`}>
                  {summary.data.readyToSubmit ? 'Ready to submit' : 'Not ready to submit'}
                </span>
                <span className="ml-2 text-slate-500">
                  {summary.data.counts.errors} errors · {summary.data.counts.warnings} warnings · {summary.data.counts.infos} information
                </span>
              </p>
              <ValidationBanners summary={summary.data} tabTitles={tabTitles} onNavigate={navigateToIssue} />
            </>
          )}

          {canViewAttachments && (
            <AttachmentPanel
              engagementId={id}
              title="Engagement attachment"
              uploadPath="/attachments"
              current={engagementAttachment}
              canUpload={e.capabilities.includes(CAP_ATTACH_UPLOAD)}
            />
          )}

          <div>
            <TabNav tabs={tabs.data?.tabs ?? []} selected={selected} unsaved={unsaved} onSelect={selectTab} />
            <div className="rounded-b-lg border border-t-0 bg-slate-50 p-4">
              <TabView
                key={selected}
                engagementId={id}
                tabNo={selected}
                drafts={drafts[selected] ?? {}}
                onDraftsChange={(next) => setDrafts((all) => ({ ...all, [selected]: next }))}
                attachments={current}
                canViewAttachments={canViewAttachments}
                canUpload={e.capabilities.includes(CAP_ATTACH_UPLOAD)}
                focusTarget={focus}
                onFocused={onFocused}
                latestChange={remoteChanges.latest}
              />
            </div>
          </div>
        </div>
      )}

      <section className="mt-8">
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

      {editing && <EditDetailsDialog engagement={e} onClose={() => setEditing(false)} latestChange={remoteChanges.latest} />}
    </Shell>
  )
}

function Shell({ children }: { children: React.ReactNode }) {
  return (
    <div className="mx-auto max-w-5xl p-6">
      <Link to="/" className="text-sm text-slate-500 hover:underline">← Back to dashboard</Link>
      <div className="mt-3">{children}</div>
    </div>
  )
}
