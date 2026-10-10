import { useState } from 'react'
import { useAuth } from '@shared/auth/AuthProvider'
import type { EngagementStatus, ListScope } from '@shared/api/types'
import { roleLabel } from '@shared/ui/labels'
import { usePermissions, useEngagements } from './api'
import { CreateEngagementDialog } from './CreateEngagementDialog'
import { EngagementFilters } from './EngagementFilters'
import { EngagementTable } from './EngagementTable'
import { StatusCards } from './StatusCards'

export function DashboardPage() {
  const { user, logout } = useAuth()
  const [scope, setScope] = useState<ListScope>('participating')
  const [status, setStatus] = useState<EngagementStatus | null>(null)
  const [search, setSearch] = useState('')
  const [creating, setCreating] = useState(false)

  const engagements = useEngagements({ scope, status, search })
  const permissions = usePermissions()

  return (
    <div className="mx-auto max-w-6xl space-y-6 p-6">
      <header className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-semibold">Engagements</h1>
          <p className="text-sm text-slate-500">
            {user?.displayName} · {user && roleLabel[user.homeRole]}
          </p>
        </div>
        <div className="flex items-center gap-3">
          {/* Whether this user may create is a backend answer (GET /auth/permissions). */}
          {permissions.data?.canCreateEngagement && (
            <button onClick={() => setCreating(true)} className="rounded bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-700">
              New engagement
            </button>
          )}
          <button onClick={logout} className="rounded border px-3 py-2 text-sm hover:bg-slate-50">Sign out</button>
        </div>
      </header>

      <StatusCards counts={engagements.data?.counts} selected={status} onSelect={setStatus} />
      <EngagementFilters scope={scope} search={search} onScope={setScope} onSearch={setSearch} />

      {engagements.isError && <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">Could not load engagements.</p>}
      {engagements.data && <EngagementTable items={engagements.data.items} />}
      {engagements.isLoading && <p className="text-sm text-slate-500">Loading…</p>}

      {creating && <CreateEngagementDialog onClose={() => setCreating(false)} />}
    </div>
  )
}
