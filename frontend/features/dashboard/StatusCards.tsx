import type { EngagementStatus } from '@shared/api/types'
import { STATUS_ORDER, statusLabel } from '@shared/ui/labels'

interface Props {
  counts: Record<EngagementStatus, number> | undefined
  selected: EngagementStatus | null
  onSelect: (status: EngagementStatus | null) => void
}

/** One card per lifecycle status. The counts come from the backend; clicking a card filters the list. */
export function StatusCards({ counts, selected, onSelect }: Props) {
  return (
    <div className="grid grid-cols-2 gap-3 sm:grid-cols-5">
      {STATUS_ORDER.map((status) => {
        const active = selected === status
        return (
          <button
            key={status}
            onClick={() => onSelect(active ? null : status)}
            aria-pressed={active}
            className={`rounded-lg border p-4 text-left transition hover:border-slate-400 ${active ? 'border-slate-900 bg-slate-900 text-white' : 'bg-white'}`}
          >
            <div className="text-2xl font-semibold">{counts?.[status] ?? '–'}</div>
            <div className={`text-xs ${active ? 'text-slate-200' : 'text-slate-500'}`}>{statusLabel[status]}</div>
          </button>
        )
      })}
    </div>
  )
}
