import { useCallback, useState } from 'react'

/** One value the user edited that someone else changed too. Both sides are display strings. */
export interface ConflictItem {
  id: string
  group: string
  label: string
  mine: string
  theirs: string
}

export interface ConflictState {
  /** What conflicted, e.g. "Source data". */
  subject: string
  by: string | null
  at: number | null
  items: ConflictItem[]
}

/**
 * State of an open 409. `open` is called once the current server data has been re-read; `resolve` removes a row after the
 * user chose (the call site performs the actual take-theirs / keep-mine on its own draft).
 */
export function useConflictResolution() {
  const [state, setState] = useState<ConflictState | null>(null)

  const open = useCallback((next: ConflictState) => setState(next), [])
  const resolve = useCallback(
    (id: string) => setState((current) => (current ? { ...current, items: current.items.filter((item) => item.id !== id) } : current)),
    [],
  )
  const dismiss = useCallback(() => setState(null), [])

  return { state, open, resolve, dismiss }
}
