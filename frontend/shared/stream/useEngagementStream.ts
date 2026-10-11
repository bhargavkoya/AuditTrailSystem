import { useEffect, useRef, useState } from 'react'
import { fetchEventSource } from '@microsoft/fetch-event-source'
import { useQueryClient, type QueryClient } from '@tanstack/react-query'
import { getAccessToken } from '../api/client'
import type { StreamEnvelope, TabView } from '../api/types'
import type { RemoteChange } from '../conflict/useRemoteChanges'

export type StreamState = 'connecting' | 'live' | 'offline'

/** Another user changed something the current user has unsaved edits on. The page decides what to offer. */
export interface ExternalChange {
  id: string
  tabNo: number
  /** null when the whole tab is affected (configuration changes). */
  sectionKey: string | null
  by: string
}

export interface StreamOptions {
  /** True when the user has unsaved edits in the given tab (and section, when given). */
  hasDraft: (tabNo: number, sectionKey?: string) => boolean
  onExternalChange: (change: ExternalChange) => void
  /** Optional: told about other users' changes, so a later 409 can say who changed what. Own events are never reported. */
  onRemoteChange?: (change: RemoteChange) => void
  selfUserId?: string
}

class FatalStreamError extends Error {}

/** Row versions are opaque base64 of 8 big-endian bytes; compare them as such. Returns <0, 0 or >0. */
export function compareVersions(a: string, b: string): number {
  const bytes = (v: string) => {
    try { return atob(v) } catch { return v }
  }
  const x = bytes(a)
  const y = bytes(b)
  if (x.length !== y.length) return x.length - y.length
  return x < y ? -1 : x > y ? 1 : 0
}

function invalidate(queryClient: QueryClient, ...keys: unknown[][]) {
  for (const queryKey of keys) void queryClient.invalidateQueries({ queryKey })
}

function refreshEverything(queryClient: QueryClient, id: string) {
  invalidate(queryClient, ['engagement', id], ['tabs', id], ['tab', id], ['validation-summary', id], ['attachments', id], ['review', id], ['participants', id], ['engagements'])
}

/**
 * Reconciles one stream event with what the page already shows. The event only names what changed; the data is always
 * re-read through the normal API, so authorization stays on the backend. Events our own save already produced are
 * recognised by version and ignored.
 */
export function applyStreamEvent(queryClient: QueryClient, id: string, envelope: StreamEnvelope, options: StreamOptions) {
  const by = envelope.actor?.name ?? 'Someone'
  const payload = envelope.payload
  const remote = (kind: RemoteChange['kind'], tabNo: number | null, sectionKey: string | null) => {
    if (envelope.actor?.userId && envelope.actor.userId === options.selfUserId) return
    options.onRemoteChange?.({ kind, by, at: Date.now(), tabNo, sectionKey })
  }

  switch (envelope.eventType) {
    case 'SectionUpdated': {
      const tabNo = Number(payload.tabNo)
      const sectionKey = String(payload.sectionKey)
      const version = String(payload.version)
      const cached = queryClient.getQueryData<TabView>(['tab', id, tabNo])?.sections.find((s) => s.sectionKey === sectionKey)
      if (cached && compareVersions(cached.version, version) >= 0) return // we already have this version

      remote('section', tabNo, sectionKey)
      invalidate(queryClient, ['tabs', id], ['validation-summary', id], ['engagement', id])
      if (options.hasDraft(tabNo, sectionKey)) options.onExternalChange({ id: envelope.eventId, tabNo, sectionKey, by })
      else invalidate(queryClient, ['tab', id, tabNo])
      return
    }
    case 'ConfigurationChanged': {
      remote('configuration', 1, null)
      invalidate(queryClient, ['tabs', id], ['validation-summary', id], ['engagement', id], ['attachments', id])
      for (const tabNo of [2, 3, 4, 5]) invalidate(queryClient, ['tab', id, tabNo])
      if (options.hasDraft(1)) options.onExternalChange({ id: envelope.eventId, tabNo: 1, sectionKey: null, by })
      else invalidate(queryClient, ['tab', id, 1])
      return
    }
    case 'ValidationSummaryChanged':
      return invalidate(queryClient, ['validation-summary', id], ['tabs', id])
    case 'AttachmentAdded':
      return invalidate(queryClient, ['attachments', id], ['tab', id], ['tabs', id], ['validation-summary', id])
    case 'EngagementStatusChanged':
      return invalidate(queryClient, ['engagement', id], ['tabs', id], ['tab', id], ['validation-summary', id], ['attachments', id], ['review', id], ['engagements'])
    case 'EngagementUpdated':
      remote('engagement', null, null)
      return invalidate(queryClient, ['engagement', id], ['tabs', id], ['tab', id], ['validation-summary', id], ['participants', id], ['engagements'])
    case 'EngagementSubmitted':
    case 'ReviewApproved':
    case 'ReviewRejected':
      remote(envelope.eventType === 'EngagementSubmitted' ? 'engagement' : 'decision', null, null)
      return invalidate(queryClient, ['engagement', id], ['review', id], ['engagements'])
    case 'ReviewerCommentAdded':
      return invalidate(queryClient, ['review', id])
    default:
      return
  }
}

/**
 * One SSE stream for the engagement being viewed (fetch-based, so the Bearer token travels in a header). Reconnects with
 * Last-Event-ID automatically; `ready` and `resync` mean "you may have missed something", so everything is refetched.
 */
export function useEngagementStream(engagementId: string, options: StreamOptions): StreamState {
  const queryClient = useQueryClient()
  const [state, setState] = useState<StreamState>('connecting')
  const optionsRef = useRef(options)
  optionsRef.current = options

  useEffect(() => {
    if (!engagementId) return
    const controller = new AbortController()
    setState('connecting')

    fetchEventSource(`/engagements/${engagementId}/events/stream`, {
      signal: controller.signal,
      openWhenHidden: true,
      headers: { Authorization: `Bearer ${getAccessToken() ?? ''}`, 'X-Correlation-Id': crypto.randomUUID() },
      async onopen(response) {
        if (response.ok && response.headers.get('content-type')?.includes('text/event-stream')) {
          setState('live')
          return
        }
        // 4xx other than a rate limit will not fix itself by retrying (e.g. 401 expired, 403 not a participant).
        if (response.status >= 400 && response.status < 500 && response.status !== 429) throw new FatalStreamError(String(response.status))
        throw new Error(`Unexpected stream response ${response.status}`)
      },
      onmessage(event) {
        if (event.event === 'ready' || event.event === 'resync') return refreshEverything(queryClient, engagementId)
        if (event.event === 'reauth') {
          controller.abort()
          setState('offline')
          return
        }
        if (!event.data) return
        try {
          applyStreamEvent(queryClient, engagementId, JSON.parse(event.data) as StreamEnvelope, optionsRef.current)
        } catch {
          refreshEverything(queryClient, engagementId) // an unreadable frame: fall back to a plain refetch
        }
      },
      onclose() {
        throw new Error('Stream closed') // reconnect (the library resends Last-Event-ID)
      },
      onerror(error) {
        if (error instanceof FatalStreamError) {
          setState('offline')
          throw error // stop retrying
        }
        setState('connecting')
        return 2000
      },
    }).catch(() => setState('offline'))

    return () => controller.abort()
  }, [engagementId, queryClient])

  return state
}
