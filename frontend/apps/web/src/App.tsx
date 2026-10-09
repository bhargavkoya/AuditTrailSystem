import { ServiceHealthPanel } from '@shared/health/ServiceHealthPanel'

export default function App() {
  return (
    <main className="mx-auto max-w-3xl p-8">
      <h1 className="text-2xl font-semibold">AuditFlow</h1>
      <p className="mt-1 text-sm text-slate-500">Phase 0 scaffold: service health, served through the gateway.</p>
      <ServiceHealthPanel />
    </main>
  )
}
