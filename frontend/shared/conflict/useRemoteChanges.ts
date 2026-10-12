import { useCallback, useRef } from 'react'

/** Something another user did, learned from the live stream. Used only to say who changed what when a save conflicts. */
export interface RemoteChange {
  kind: 'section' | 'configuration' | 'engagement' | 'decision'
  by: string
  at: number
  tabNo: number | null
  sectionKey: string | null
}

const KEEP = 50

/** Remembers the most recent changes by other users (in memory, no re-render). */
export function useRemoteChanges() {
  const changes = useRef<RemoteChange[]>([])

  const record = useCallback((change: RemoteChange) => {
    changes.current = [...changes.current.slice(-(KEEP - 1)), change]
  }, [])

  /** Newest change matching the predicate, if any. */
  const latest = useCallback((match: (change: RemoteChange) => boolean) => [...changes.current].reverse().find(match), [])

  return { record, latest }
}

export type LatestChange = ReturnType<typeof useRemoteChanges>['latest']
