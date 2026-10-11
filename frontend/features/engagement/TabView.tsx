import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { ApiError } from '@shared/api/client'
import type { Attachment, FieldValue, FieldView, SectionPatch, SectionView, TabView as TabViewModel } from '@shared/api/types'
import { useSaveTab, useTab } from './api'
import { AttachmentPanel } from './AttachmentPanel'
import { FieldRenderer, fieldElementId } from './FieldRenderer'
import { ReconciliationTable } from './ReconciliationTable'

/** Unsaved edits: sectionKey -> fieldKey -> value. Only fields the user changed are kept. */
export type Drafts = Record<string, Record<string, FieldValue>>

export interface FocusTarget {
  tabNo: number
  sectionKey: string | null
  fieldKey: string | null
}

const sameAsServer = (field: FieldView, value: FieldValue) => {
  if (field.type === 'checkbox') return (field.value === true) === (value === true)
  return (field.value ?? '') === (value ?? '')
}

/** The wire value: blank text clears the field; numbers and dates travel as text and are parsed by the backend. */
const toWire = (field: FieldView, value: FieldValue): FieldValue =>
  field.type === 'checkbox' ? value === true : value === '' || value === null ? null : value

function describeRejections(errors: Record<string, string[]>, tab: TabViewModel) {
  return Object.entries(errors).flatMap(([key, messages]) => {
    const [sectionKey, fieldKey] = key.split('.')
    const section = tab.sections.find((s) => s.sectionKey === sectionKey)
    const field = section?.fields.find((f) => f.key === fieldKey)
    const where = [section?.title, field?.label].filter(Boolean).join(' / ')
    return messages.map((m) => (where ? `${where}: ${m}` : m))
  })
}

export function TabView({
  engagementId,
  tabNo,
  drafts,
  onDraftsChange,
  attachments,
  canViewAttachments,
  canUpload,
  focusTarget,
  onFocused,
}: {
  engagementId: string
  tabNo: number
  drafts: Drafts
  onDraftsChange: (next: Drafts) => void
  attachments: Attachment[]
  /** Backend capabilities (attachment.view / attachment.upload); the tab-level slot is hidden without the first. */
  canViewAttachments: boolean
  canUpload: boolean
  focusTarget: FocusTarget | null
  onFocused: () => void
}) {
  const queryClient = useQueryClient()
  const tab = useTab(engagementId, tabNo, true)
  const save = useSaveTab(engagementId, tabNo)
  const [savedAt, setSavedAt] = useState<number | null>(null)
  const [conflict, setConflict] = useState<string | null>(null)
  const [rejections, setRejections] = useState<string[]>([])

  useEffect(() => {
    if (!focusTarget || focusTarget.tabNo !== tabNo || !tab.data) return
    const target =
      (focusTarget.sectionKey && focusTarget.fieldKey && document.getElementById(fieldElementId(focusTarget.sectionKey, focusTarget.fieldKey))) ||
      (focusTarget.sectionKey && document.getElementById(`section-${focusTarget.sectionKey}`))
    if (target) {
      target.scrollIntoView({ block: 'center', behavior: 'smooth' })
      target.focus({ preventScroll: true })
    }
    onFocused()
  }, [focusTarget, tab.data, tabNo, onFocused])

  useEffect(() => {
    if (savedAt === null) return
    const timer = setTimeout(() => setSavedAt(null), 3000)
    return () => clearTimeout(timer)
  }, [savedAt])

  if (tab.isError) {
    const forbidden = tab.error instanceof ApiError && tab.error.status === 403
    return <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{forbidden ? 'You cannot view this tab.' : 'Could not load this tab.'}</p>
  }
  if (!tab.data) return <p className="text-sm text-slate-500">Loading…</p>

  const view = tab.data
  const dirtySections = Object.entries(drafts).filter(([key, fields]) => Object.keys(fields).length > 0 && view.sections.some((s) => s.sectionKey === key))
  const hasDrafts = dirtySections.length > 0

  function changeField(section: SectionView, field: FieldView, value: FieldValue) {
    const sectionDrafts = { ...(drafts[section.sectionKey] ?? {}) }
    if (sameAsServer(field, value)) delete sectionDrafts[field.key]
    else sectionDrafts[field.key] = value

    const next = { ...drafts, [section.sectionKey]: sectionDrafts }
    if (Object.keys(sectionDrafts).length === 0) delete next[section.sectionKey]
    onDraftsChange(next)
  }

  function submit() {
    setConflict(null)
    setRejections([])

    const patches: SectionPatch[] = dirtySections.map(([sectionKey, fields]) => {
      const section = view.sections.find((s) => s.sectionKey === sectionKey)!
      return {
        sectionKey,
        version: section.version,
        fields: Object.fromEntries(Object.entries(fields).map(([key, value]) => [key, toWire(section.fields.find((f) => f.key === key)!, value)])),
      }
    })

    save.mutate(patches, {
      onSuccess: (result) => {
        const saved = new Set(patches.map((p) => p.sectionKey))
        const remaining = Object.fromEntries(Object.entries(drafts).filter(([key]) => !saved.has(key)))
        onDraftsChange(remaining)
        setSavedAt(Date.now())
        if (result.visibilityChanges.length > 0) {
          void queryClient.invalidateQueries({ queryKey: ['attachments', engagementId] })
        }
      },
      onError: (error) => {
        if (!(error instanceof ApiError)) return
        if (error.status === 409) {
          const title = view.sections.find((s) => s.sectionKey === error.problem.resource)?.title
          setConflict(title ?? 'a section on this tab')
        } else if (error.status === 422 && error.problem.errors) {
          setRejections(describeRejections(error.problem.errors, view))
        }
      },
    })
  }

  const failure =
    save.error instanceof ApiError && ![409, 422].includes(save.error.status)
      ? save.error.status === 403
        ? 'You cannot edit this engagement right now.'
        : save.error.message
      : null

  const tabAttachment = attachments.find((a) => a.scope === 'Tab' && a.tabNo === tabNo) ?? null

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-lg font-semibold">{view.title}</h2>
        <div className="flex items-center gap-3">
          {savedAt !== null && <span role="status" className="text-sm text-emerald-700">Saved</span>}
          {!view.editable && <span className="rounded bg-slate-100 px-2 py-1 text-xs text-slate-600">Read-only</span>}
          {view.editable && (
            <button
              type="button"
              onClick={submit}
              disabled={!hasDrafts || save.isPending}
              className="rounded bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-700 disabled:opacity-40"
            >
              {save.isPending ? 'Saving…' : hasDrafts ? 'Save tab' : 'No changes'}
            </button>
          )}
        </div>
      </div>

      {conflict && (
        <div role="alert" className="flex flex-wrap items-center gap-3 rounded border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900">
          <span>
            <strong>Someone else changed {conflict}.</strong> Nothing was saved. Your unsaved edits are still here.
          </span>
          <button
            type="button"
            className="rounded border border-amber-400 bg-white px-3 py-1 text-xs font-medium hover:bg-amber-100"
            onClick={() => {
              setConflict(null)
              save.reset()
              void tab.refetch()
            }}
          >
            Load latest (keep my edits)
          </button>
        </div>
      )}

      {rejections.length > 0 && (
        <div role="alert" className="rounded border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-800">
          <p className="font-medium">Some values were rejected and nothing was saved:</p>
          <ul className="mt-1 list-disc pl-5">{rejections.map((r) => <li key={r}>{r}</li>)}</ul>
        </div>
      )}

      {failure && <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{failure}</p>}

      {view.reconciliation !== null && (
        <section aria-label="Reconciliation" className="space-y-2">
          <h3 className="text-sm font-medium uppercase tracking-wide text-slate-500">Variance overview</h3>
          <ReconciliationTable rows={view.reconciliation} />
        </section>
      )}

      {view.sections.map((section) => {
        const attachment = attachments.find((a) => a.scope === 'Section' && a.sectionKey === section.sectionKey) ?? null
        return (
          <section key={section.sectionKey} id={`section-${section.sectionKey}`} tabIndex={-1} className="rounded-lg border bg-white p-4 outline-none">
            <h3 className="font-medium">{section.title}</h3>
            {section.issues.length > 0 && (
              <ul className="mt-1 space-y-0.5">
                {section.issues.map((issue) => (
                  <li key={issue.code} className={`text-xs ${issue.severity === 'Error' ? 'text-red-700' : issue.severity === 'Warning' ? 'text-amber-700' : 'text-sky-700'}`}>
                    {issue.message}
                  </li>
                ))}
              </ul>
            )}
            {section.fields.length > 0 && (
              <div className="mt-3 grid gap-4 md:grid-cols-2">
                {section.fields.map((field) => (
                  <FieldRenderer
                    key={field.key}
                    sectionKey={section.sectionKey}
                    field={field}
                    draft={drafts[section.sectionKey]?.[field.key]}
                    onChange={(value) => changeField(section, field, value)}
                  />
                ))}
              </div>
            )}
            {section.attachment && (
              <div className="mt-4">
                <AttachmentPanel
                  engagementId={engagementId}
                  title="Section attachment"
                  uploadPath={`/sections/${section.sectionKey}/attachments`}
                  current={attachment}
                  canUpload={section.attachment.canUpload}
                  required={section.attachment.required}
                />
              </div>
            )}
          </section>
        )
      })}

      {canViewAttachments && (
        <AttachmentPanel
          engagementId={engagementId}
          title={`Tab ${tabNo} attachment`}
          uploadPath={`/tabs/${tabNo}/attachments`}
          current={tabAttachment}
          canUpload={canUpload}
        />
      )}
    </div>
  )
}
