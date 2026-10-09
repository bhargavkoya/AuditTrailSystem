import { useServiceHealth, type ServiceHealth } from './useServiceHealth'

const badge: Record<ServiceHealth['status'], string> = {
  Healthy: 'bg-emerald-100 text-emerald-800',
  Degraded: 'bg-amber-100 text-amber-800',
  Unhealthy: 'bg-red-100 text-red-800',
  Unreachable: 'bg-red-100 text-red-800',
  Checking: 'bg-slate-100 text-slate-600',
}

export function ServiceHealthPanel() {
  const { results, refresh } = useServiceHealth()

  return (
    <section className="mt-6">
      <div className="flex items-center justify-between">
        <h2 className="text-lg font-medium">Services</h2>
        <button onClick={() => void refresh()} className="rounded border px-3 py-1 text-sm hover:bg-slate-50">
          Refresh
        </button>
      </div>
      <ul className="mt-3 divide-y rounded border">
        {results.map((r) => (
          <li key={r.service} className="flex items-center justify-between px-4 py-3">
            <span className="font-mono text-sm">{r.service}-service</span>
            <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${badge[r.status]}`}>{r.status}</span>
          </li>
        ))}
      </ul>
    </section>
  )
}
