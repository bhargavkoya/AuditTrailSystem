import type { TabSummary } from '@shared/api/types'

/** Tab strip with issue badges taken from the backend. A dot marks tabs holding unsaved edits. */
export function TabNav({
  tabs,
  selected,
  unsaved,
  onSelect,
}: {
  tabs: TabSummary[]
  selected: number
  unsaved: Set<number>
  onSelect: (tabNo: number) => void
}) {
  return (
    <nav role="tablist" aria-label="Engagement tabs" className="flex flex-wrap gap-1 border-b">
      {tabs.map((tab) => {
        const active = tab.tabNo === selected
        const { errors, warnings } = tab.issueCounts
        return (
          <button
            key={tab.tabNo}
            role="tab"
            aria-selected={active}
            onClick={() => onSelect(tab.tabNo)}
            className={`-mb-px flex items-center gap-2 rounded-t border px-3 py-2 text-sm ${
              active ? 'border-b-white bg-white font-medium' : 'border-transparent text-slate-600 hover:bg-slate-100'
            }`}
          >
            <span className="text-xs text-slate-400">{tab.tabNo}</span>
            {tab.title}
            {errors > 0 && <span title={`${errors} error(s)`} className="rounded-full bg-red-600 px-1.5 text-xs font-semibold text-white">{errors}</span>}
            {warnings > 0 && <span title={`${warnings} warning(s)`} className="rounded-full bg-amber-500 px-1.5 text-xs font-semibold text-white">{warnings}</span>}
            {unsaved.has(tab.tabNo) && <span title="Unsaved changes" className="h-2 w-2 rounded-full bg-blue-600" />}
          </button>
        )
      })}
    </nav>
  )
}
