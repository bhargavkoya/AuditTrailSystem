import type { FieldType, FieldValue } from '../api/types'

/** Display only. The client compares what it shows; it never decides whether a value is valid or how to merge. */
export function formatFieldValue(type: FieldType, value: FieldValue): string {
  if (type === 'checkbox') return value === true ? 'Yes' : 'No'
  if (value === null || value === '') return '(empty)'
  return String(value)
}

export function sameFieldValue(type: FieldType, a: FieldValue, b: FieldValue): boolean {
  if (type === 'checkbox') return (a === true) === (b === true)
  return (a ?? '') === (b ?? '')
}

export function ago(at: number, now = Date.now()): string {
  const seconds = Math.max(0, Math.round((now - at) / 1000))
  if (seconds < 5) return 'just now'
  if (seconds < 60) return `${seconds} s ago`
  const minutes = Math.round(seconds / 60)
  return minutes < 60 ? `${minutes} min ago` : `${Math.round(minutes / 60)} h ago`
}
