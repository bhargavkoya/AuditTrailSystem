import type { ReconciliationRow, VarianceIndicator } from '@shared/api/types'

const chip: Record<VarianceIndicator, { label: string; style: string }> = {
  Ok: { label: 'Within tolerance', style: 'bg-emerald-100 text-emerald-800' },
  Warning: { label: 'Variance', style: 'bg-amber-100 text-amber-800' },
  Significant: { label: 'Significant variance', style: 'bg-red-100 text-red-800' },
  Missing: { label: 'Value missing', style: 'bg-slate-200 text-slate-700' },
}

const number = (value: number | null) => (value === null ? '—' : value.toLocaleString(undefined, { maximumFractionDigits: 2 }))

/** Variances are computed by the backend; this only formats them. */
export function ReconciliationTable({ rows }: { rows: ReconciliationRow[] }) {
  if (rows.length === 0) {
    return <p className="text-sm text-slate-500">Nothing to reconcile for the current configuration.</p>
  }

  return (
    <div className="overflow-x-auto rounded border bg-white">
      <table className="w-full text-left text-sm">
        <caption className="sr-only">Reconciliation: expected (book) versus actual (ledger) values</caption>
        <thead className="border-b bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
          <tr>
            <th scope="col" className="px-3 py-2 font-medium">Asset class</th>
            <th scope="col" className="px-3 py-2 text-right font-medium">Expected (book)</th>
            <th scope="col" className="px-3 py-2 text-right font-medium">Actual (ledger)</th>
            <th scope="col" className="px-3 py-2 text-right font-medium">Value variance</th>
            <th scope="col" className="px-3 py-2 text-right font-medium">% variance</th>
            <th scope="col" className="px-3 py-2 font-medium">Indicator</th>
          </tr>
        </thead>
        <tbody className="divide-y">
          {rows.map((row) => (
            <tr key={row.comparisonKey}>
              <th scope="row" className="px-3 py-2 font-medium">{row.label}</th>
              <td className="px-3 py-2 text-right tabular-nums">{number(row.expected)}</td>
              <td className="px-3 py-2 text-right tabular-nums">{number(row.actual)}</td>
              <td className="px-3 py-2 text-right tabular-nums">{number(row.valueVariance)}</td>
              <td className="px-3 py-2 text-right tabular-nums">{row.percentVariance === null ? '—' : `${row.percentVariance.toFixed(2)}%`}</td>
              <td className="px-3 py-2">
                <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${chip[row.indicator].style}`}>{chip[row.indicator].label}</span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
