import type { FieldValue, FieldView, Issue } from '@shared/api/types'

const severityText: Record<Issue['severity'], string> = {
  Error: 'text-red-700',
  Warning: 'text-amber-700',
  Information: 'text-sky-700',
}

export function fieldElementId(sectionKey: string, fieldKey: string) {
  return `field-${sectionKey}-${fieldKey}`
}

/** What the input shows: the user's unsaved edit if there is one, otherwise the value from the backend. */
export function displayValue(field: FieldView, draft: FieldValue | undefined): FieldValue {
  return draft !== undefined ? draft : field.value
}

/**
 * One renderer for every field kind. It draws exactly what the backend returned: label, kind, required marker,
 * editability and issues. It never decides validity or permissions.
 */
export function FieldRenderer({
  sectionKey,
  field,
  draft,
  onChange,
}: {
  sectionKey: string
  field: FieldView
  draft: FieldValue | undefined
  onChange: (value: FieldValue) => void
}) {
  const id = fieldElementId(sectionKey, field.key)
  const value = displayValue(field, draft)
  const worst = field.issues[0]?.severity
  const border = worst === 'Error' ? 'border-red-400' : worst === 'Warning' ? 'border-amber-400' : ''
  const input = `mt-1 w-full rounded border px-3 py-2 text-sm disabled:bg-slate-50 disabled:text-slate-600 ${border}`
  const describedBy = field.issues.length > 0 ? `${id}-issues` : undefined

  const label = (
    <span className="text-sm font-medium">
      {field.label}
      {field.required && <span aria-hidden="true" className="ml-0.5 text-red-600">*</span>}
    </span>
  )

  let control
  switch (field.type) {
    case 'checkbox':
      control = (
        <label className="flex items-center gap-2">
          <input
            id={id}
            type="checkbox"
            checked={value === true}
            disabled={!field.editable}
            aria-required={field.required}
            aria-describedby={describedBy}
            onChange={(e) => onChange(e.target.checked)}
          />
          {label}
        </label>
      )
      break
    case 'documentation':
      control = (
        <label className="block">
          {label}
          <textarea
            id={id}
            rows={4}
            className={input}
            value={typeof value === 'string' ? value : ''}
            disabled={!field.editable}
            aria-required={field.required}
            aria-describedby={describedBy}
            onChange={(e) => onChange(e.target.value)}
          />
        </label>
      )
      break
    default:
      control = (
        <label className="block">
          {label}
          <input
            id={id}
            className={input}
            type={field.type === 'number' ? 'number' : field.type === 'date' ? 'date' : 'text'}
            step={field.type === 'number' ? 'any' : undefined}
            value={value === null || typeof value === 'boolean' ? '' : String(value)}
            disabled={!field.editable}
            aria-required={field.required}
            aria-describedby={describedBy}
            onChange={(e) => onChange(e.target.value)}
          />
        </label>
      )
  }

  return (
    <div className={field.type === 'documentation' ? 'md:col-span-2' : ''}>
      {control}
      {field.issues.length > 0 && (
        <ul id={`${id}-issues`} className="mt-1 space-y-0.5">
          {field.issues.map((issue) => (
            <li key={issue.code} className={`text-xs ${severityText[issue.severity]}`}>{issue.message}</li>
          ))}
        </ul>
      )}
    </div>
  )
}
