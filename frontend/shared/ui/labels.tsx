import type { EngagementRole, EngagementStatus } from '../api/types'

// Presentation only: display names and colours. Which statuses/roles/actions apply is decided by the backend.
export const STATUS_ORDER: EngagementStatus[] = ['Draft', 'InProgress', 'UnderReview', 'Approved', 'Rejected']

export const statusLabel: Record<EngagementStatus, string> = {
  Draft: 'Draft',
  InProgress: 'In Progress',
  UnderReview: 'Under Review',
  Approved: 'Approved',
  Rejected: 'Rejected',
}

export const roleLabel: Record<EngagementRole, string> = {
  Auditor: 'Auditor',
  CoAuditor: 'Co-Auditor',
  Reviewer: 'Reviewer',
  Viewer: 'Viewer',
}

const statusStyle: Record<EngagementStatus, string> = {
  Draft: 'bg-slate-100 text-slate-700',
  InProgress: 'bg-blue-100 text-blue-800',
  UnderReview: 'bg-amber-100 text-amber-800',
  Approved: 'bg-emerald-100 text-emerald-800',
  Rejected: 'bg-red-100 text-red-800',
}

export function StatusBadge({ status }: { status: EngagementStatus }) {
  return <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${statusStyle[status]}`}>{statusLabel[status]}</span>
}
