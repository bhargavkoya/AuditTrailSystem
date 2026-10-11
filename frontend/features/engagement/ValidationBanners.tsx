import { useState } from 'react'
import type { Issue, IssueSeverity, ValidationSummary } from '@shared/api/types'

const ORDER: IssueSeverity[] = ['Error', 'Warning', 'Information']
const COLLAPSED_COUNT = 4

const style: Record<IssueSeverity, { box: string; badge: string; label: string }> = {
  Error: { box: 'border-red-200 bg-red-50 text-red-900', badge: 'bg-red-600', label: 'Errors' },
  Warning: { box: 'border-amber-200 bg-amber-50 text-amber-900', badge: 'bg-amber-500', label: 'Warnings' },
  Information: { box: 'border-sky-200 bg-sky-50 text-sky-900', badge: 'bg-sky-600', label: 'Information' },
}

/**
 * Top-level validation banners (Error / Warning / Information) straight from the backend validation summary.
 * Selecting a message jumps to the tab and field it is about; nothing here decides what is valid.
 */
export function ValidationBanners({
  summary,
  tabTitles,
  onNavigate,
}: {
  summary: ValidationSummary
  tabTitles: Record<number, string>
  onNavigate: (issue: Issue) => void
}) {
  const [expanded, setExpanded] = useState<Record<IssueSeverity, boolean>>({ Error: false, Warning: false, Information: false })

  if (summary.issues.length === 0) {
    return (
      <p role="status" className="rounded border border-emerald-200 bg-emerald-50 px-4 py-2 text-sm text-emerald-800">
        No validation messages.
      </p>
    )
  }

  return (
    <div className="space-y-2" aria-label="Validation summary">
      {ORDER.map((severity) => {
        const issues = summary.issues.filter((i) => i.severity === severity)
        if (issues.length === 0) return null
        const shown = expanded[severity] ? issues : issues.slice(0, COLLAPSED_COUNT)
        const s = style[severity]

        return (
          <section key={severity} role={severity === 'Error' ? 'alert' : 'status'} className={`rounded border px-4 py-3 text-sm ${s.box}`}>
            <h3 className="flex items-center gap-2 font-medium">
              <span className={`rounded-full px-2 py-0.5 text-xs font-semibold text-white ${s.badge}`}>{issues.length}</span>
              {s.label}
            </h3>
            <ul className="mt-2 space-y-1">
              {shown.map((issue, index) => (
                <li key={`${issue.code}-${issue.sectionKey}-${issue.fieldKey}-${index}`}>
                  {issue.tabNo !== null ? (
                    <button type="button" onClick={() => onNavigate(issue)} className="text-left underline-offset-2 hover:underline">
                      <span className="font-medium">{tabTitles[issue.tabNo] ?? `Tab ${issue.tabNo}`}:</span> {issue.message}
                    </button>
                  ) : (
                    <span>{issue.message}</span>
                  )}
                </li>
              ))}
            </ul>
            {issues.length > COLLAPSED_COUNT && (
              <button
                type="button"
                onClick={() => setExpanded({ ...expanded, [severity]: !expanded[severity] })}
                className="mt-2 text-xs font-medium underline"
              >
                {expanded[severity] ? 'Show fewer' : `Show all ${issues.length}`}
              </button>
            )}
          </section>
        )
      })}
    </div>
  )
}
