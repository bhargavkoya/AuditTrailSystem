import type { Attachment, ReviewTab } from '@shared/api/types'
import { AttachmentPanel } from '@features/engagement/AttachmentPanel'
import { FieldRenderer } from '@features/engagement/FieldRenderer'
import { ReconciliationTable } from '@features/engagement/ReconciliationTable'

const noop = () => {}

/**
 * The tab exactly as the backend returned it, with nothing editable. The same field renderer is used, so the reviewer
 * sees what the auditor entered; the backend marks every field read-only for a reviewer.
 */
export function ReadOnlyTabView({
  engagementId,
  tab,
  attachments,
  canViewAttachments,
}: {
  engagementId: string
  tab: ReviewTab
  attachments: Attachment[]
  canViewAttachments: boolean
}) {
  if (!tab.available || !tab.content) {
    return (
      <p role="alert" className="rounded border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900">
        This tab could not be loaded right now. The rest of the review screen still works; try again shortly.
      </p>
    )
  }

  const content = tab.content
  const tabAttachment = attachments.find((a) => a.scope === 'Tab' && a.tabNo === tab.tabNo) ?? null

  return (
    <div className="space-y-4">
      {content.reconciliation !== null && (
        <section aria-label="Reconciliation" className="space-y-2">
          <h3 className="text-sm font-medium uppercase tracking-wide text-slate-500">Variance overview</h3>
          <ReconciliationTable rows={content.reconciliation} />
        </section>
      )}

      {content.sections.map((section) => {
        const current = attachments.find((a) => a.scope === 'Section' && a.sectionKey === section.sectionKey) ?? null
        return (
          <section key={section.sectionKey} className="rounded-lg border bg-white p-4">
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
                  <FieldRenderer key={field.key} sectionKey={section.sectionKey} field={field} draft={undefined} onChange={noop} />
                ))}
              </div>
            )}
            {section.attachment && canViewAttachments && (
              <div className="mt-4">
                <AttachmentPanel
                  engagementId={engagementId}
                  title="Section attachment"
                  uploadPath={`/sections/${section.sectionKey}/attachments`}
                  current={current}
                  canUpload={false}
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
          title={`Tab ${tab.tabNo} attachment`}
          uploadPath={`/tabs/${tab.tabNo}/attachments`}
          current={tabAttachment}
          canUpload={false}
        />
      )}
    </div>
  )
}
