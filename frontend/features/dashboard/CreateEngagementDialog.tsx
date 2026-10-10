import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { ApiError } from '@shared/api/client'
import type { CreateEngagementRequest } from '@shared/api/types'
import { useAuth } from '@shared/auth/AuthProvider'
import { roleLabel } from '@shared/ui/labels'
import { useCreateEngagement, useCreationOptions, useUsers } from './api'

function toggle<T>(list: T[], item: T): T[] {
  return list.includes(item) ? list.filter((x) => x !== item) : [...list, item]
}

export function CreateEngagementDialog({ onClose }: { onClose: () => void }) {
  const navigate = useNavigate()
  const { user } = useAuth()
  const options = useCreationOptions(true)
  const reviewers = useUsers(true, 'Reviewer')
  const everyone = useUsers(true)
  const create = useCreateEngagement()

  const [name, setName] = useState('')
  const [periodType, setPeriodType] = useState('')
  const [region, setRegion] = useState('')
  const [reviewerUserId, setReviewerUserId] = useState('')
  const [configTypes, setConfigTypes] = useState<string[]>([])
  const [flags, setFlags] = useState<Record<string, boolean>>({})
  const [participants, setParticipants] = useState<string[]>([])

  const candidates = (everyone.data ?? []).filter((u) => u.homeRole !== 'Reviewer' && u.userId !== user?.userId)
  const errors = create.error instanceof ApiError ? create.error.problem.errors ?? {} : {}
  const fieldError = (...keys: string[]) => keys.flatMap((k) => errors[k] ?? []).join(' ')
  const generalError = create.error instanceof ApiError && !create.error.problem.errors ? create.error.message : null

  async function submit(e: FormEvent) {
    e.preventDefault()
    const request: CreateEngagementRequest = {
      name, periodType, region, reviewerUserId, participantUserIds: participants,
      configuration: { configTypes, options: flags },
    }
    try {
      const created = await create.mutateAsync(request)
      onClose()
      navigate(`/engagements/${created.engagementId}`)
    } catch {
      /* shown through create.error */
    }
  }

  const select = 'mt-1 w-full rounded border px-3 py-2 text-sm'
  const Err = ({ keys }: { keys: string[] }) => (fieldError(...keys) ? <p className="mt-1 text-xs text-red-600">{fieldError(...keys)}</p> : null)

  return (
    <div className="fixed inset-0 z-10 flex items-start justify-center overflow-y-auto bg-black/40 p-4" role="dialog" aria-modal="true" aria-label="New engagement">
      <form onSubmit={submit} className="mt-8 w-full max-w-lg space-y-4 rounded-lg bg-white p-6 shadow-xl">
        <h2 className="text-lg font-semibold">New engagement</h2>

        {options.isLoading && <p className="text-sm text-slate-500">Loading options…</p>}
        {options.isError && <p className="text-sm text-red-600">Could not load options.</p>}

        <label className="block text-sm font-medium">
          Name
          <input className={select} value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} />
          <Err keys={['name']} />
        </label>

        <div className="grid grid-cols-2 gap-3">
          <label className="block text-sm font-medium">
            Period type
            <select className={select} value={periodType} onChange={(e) => setPeriodType(e.target.value)} required>
              <option value="">Select…</option>
              {options.data?.periodTypes.map((p) => <option key={p}>{p}</option>)}
            </select>
            <Err keys={['periodType']} />
          </label>
          <label className="block text-sm font-medium">
            Region
            <select className={select} value={region} onChange={(e) => setRegion(e.target.value)} required>
              <option value="">Select…</option>
              {options.data?.regions.map((r) => <option key={r}>{r}</option>)}
            </select>
            <Err keys={['region']} />
          </label>
        </div>

        <label className="block text-sm font-medium">
          Reviewer
          <select className={select} value={reviewerUserId} onChange={(e) => setReviewerUserId(e.target.value)} required>
            <option value="">Select…</option>
            {reviewers.data?.map((u) => <option key={u.userId} value={u.userId}>{u.displayName}</option>)}
          </select>
          <Err keys={['reviewerUserId']} />
        </label>

        <fieldset>
          <legend className="text-sm font-medium">Configuration types</legend>
          <div className="mt-1 flex flex-wrap gap-3">
            {options.data?.configTypes.map((t) => (
              <label key={t} className="flex items-center gap-1.5 text-sm">
                <input type="checkbox" checked={configTypes.includes(t)} onChange={() => setConfigTypes(toggle(configTypes, t))} />
                {t}
              </label>
            ))}
          </div>
          <Err keys={['configuration.configTypes']} />
        </fieldset>

        {(options.data?.options.length ?? 0) > 0 && (
          <fieldset>
            <legend className="text-sm font-medium">Rule-based setup</legend>
            <div className="mt-1 space-y-1">
              {options.data?.options.map((o) => (
                <label key={o.key} className="flex items-center gap-1.5 text-sm">
                  <input type="checkbox" checked={!!flags[o.key]} onChange={(e) => setFlags({ ...flags, [o.key]: e.target.checked })} />
                  {o.label}
                </label>
              ))}
            </div>
            <Err keys={['configuration.options']} />
          </fieldset>
        )}

        <fieldset>
          <legend className="text-sm font-medium">Other participants (optional)</legend>
          <div className="mt-1 space-y-1">
            {candidates.map((u) => (
              <label key={u.userId} className="flex items-center gap-1.5 text-sm">
                <input type="checkbox" checked={participants.includes(u.userId)} onChange={() => setParticipants(toggle(participants, u.userId))} />
                {u.displayName} <span className="text-xs text-slate-400">({roleLabel[u.homeRole]})</span>
              </label>
            ))}
          </div>
          <Err keys={['participantUserIds']} />
        </fieldset>

        {generalError && <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{generalError}</p>}

        <div className="flex justify-end gap-2 pt-2">
          <button type="button" onClick={onClose} className="rounded border px-4 py-2 text-sm hover:bg-slate-50">Cancel</button>
          <button disabled={create.isPending} className="rounded bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-700 disabled:opacity-50">
            {create.isPending ? 'Creating…' : 'Create'}
          </button>
        </div>
      </form>
    </div>
  )
}
