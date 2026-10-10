import type { ListScope } from '@shared/api/types'

const SCOPES: { value: ListScope; label: string }[] = [
  { value: 'participating', label: 'All mine' },
  { value: 'owned', label: 'Owned' },
  { value: 'assigned', label: 'Assigned' },
  { value: 'reviewing', label: 'Reviewing' },
]

interface Props {
  scope: ListScope
  search: string
  onScope: (scope: ListScope) => void
  onSearch: (search: string) => void
}

export function EngagementFilters({ scope, search, onScope, onSearch }: Props) {
  return (
    <div className="flex flex-wrap items-center gap-3">
      <div role="tablist" aria-label="Engagement scope" className="inline-flex rounded-lg border bg-white p-0.5">
        {SCOPES.map((s) => (
          <button
            key={s.value}
            role="tab"
            aria-selected={scope === s.value}
            onClick={() => onScope(s.value)}
            className={`rounded-md px-3 py-1.5 text-sm ${scope === s.value ? 'bg-slate-900 text-white' : 'text-slate-600 hover:bg-slate-50'}`}
          >
            {s.label}
          </button>
        ))}
      </div>
      <input
        type="search"
        value={search}
        onChange={(e) => onSearch(e.target.value)}
        placeholder="Search name or ID"
        aria-label="Search engagements"
        className="w-64 rounded-lg border px-3 py-1.5 text-sm"
      />
    </div>
  )
}
