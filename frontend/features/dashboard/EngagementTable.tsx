import { Link } from 'react-router-dom'
import type { EngagementSummary } from '@shared/api/types'
import { StatusBadge, roleLabel } from '@shared/ui/labels'

// Quick-action labels and targets. WHICH actions appear for a row is decided by the backend (allowedActions);
// the client only maps an action to a route. Actions without a screen yet are simply not rendered.
const ACTIONS: Record<string, { label: string; to: (id: string) => string }> = {
  review: { label: 'Review', to: (id) => `/engagements/${id}/review` },
  edit: { label: 'Open', to: (id) => `/engagements/${id}` },
  open: { label: 'View', to: (id) => `/engagements/${id}` },
}

function primaryAction(actions: string[]) {
  return ['review', 'edit', 'open'].find((a) => actions.includes(a))
}

export function EngagementTable({ items }: { items: EngagementSummary[] }) {
  if (items.length === 0) {
    return <p className="rounded-lg border bg-white p-8 text-center text-sm text-slate-500">No engagements match.</p>
  }

  return (
    <div className="overflow-x-auto rounded-lg border bg-white">
      <table className="w-full text-left text-sm">
        <thead className="border-b bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
          <tr>
            {['ID', 'Name', 'Region', 'Period', 'Reviewer', 'Status', 'Last updated', 'My role', ''].map((h) => (
              <th key={h} className="px-4 py-2 font-medium">{h}</th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y">
          {items.map((e) => {
            const action = primaryAction(e.allowedActions)
            return (
              <tr key={e.engagementId} className="hover:bg-slate-50">
                <td className="px-4 py-3 font-mono text-xs">{e.engagementId}</td>
                <td className="px-4 py-3 font-medium">{e.name}</td>
                <td className="px-4 py-3">{e.region}</td>
                <td className="px-4 py-3">{e.periodType}</td>
                <td className="px-4 py-3">{e.reviewer.displayName}</td>
                <td className="px-4 py-3"><StatusBadge status={e.status} /></td>
                <td className="px-4 py-3 whitespace-nowrap text-slate-500">{new Date(e.lastUpdatedAt).toLocaleString()}</td>
                <td className="px-4 py-3">{roleLabel[e.myRole]}</td>
                <td className="px-4 py-3 text-right">
                  {action && (
                    <Link
                      to={ACTIONS[action].to(e.engagementId)}
                      className={`rounded px-3 py-1 text-xs font-medium ${action === 'review' ? 'bg-amber-500 text-white hover:bg-amber-600' : 'border hover:bg-slate-100'}`}
                    >
                      {ACTIONS[action].label}
                    </Link>
                  )}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
