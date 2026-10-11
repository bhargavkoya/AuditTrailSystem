import { ago } from './format'
import type { ConflictItem, ConflictState } from './useConflictResolution'

const button = 'rounded border px-3 py-1 text-xs font-medium'

/**
 * Shown after a 409. Nothing was saved; the page already shows the latest server data. For every value the user edited and
 * someone else also changed, it lists yours vs the current value and offers Take theirs / Keep mine.
 */
export function ConflictBanner({
  state,
  onTakeTheirs,
  onKeepMine,
  onDismiss,
  emptyNote = 'Your other edits were kept; save again to apply them.',
}: {
  state: ConflictState
  onTakeTheirs: (item: ConflictItem) => void
  onKeepMine: (item: ConflictItem) => void
  onDismiss: () => void
  /** Sentence shown when no individual value needs a decision. */
  emptyNote?: string
}) {
  const { subject, by, at, items } = state
  const who = by ?? 'Someone else'
  const when = at === null ? '' : ` ${ago(at)}`

  return (
    <div role="alert" className="space-y-3 rounded border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p>
          <strong>{who} changed {subject}{when}.</strong> Nothing was saved
          {items.length > 0 ? '. Choose what to keep for each value below.' : `. ${emptyNote}`}
        </p>
        <button type="button" onClick={onDismiss} className="text-xs underline">Dismiss</button>
      </div>

      {items.length > 0 && (
        <ul className="divide-y divide-amber-200 rounded border border-amber-200 bg-white">
          {items.map((item) => (
            <li key={item.id} className="grid gap-2 px-3 py-2 md:grid-cols-[1fr_1fr_1fr_auto] md:items-center">
              <span className="font-medium">
                {item.label}
                <span className="block text-xs font-normal text-slate-500">{item.group}</span>
              </span>
              <span><span className="block text-xs text-slate-500">Yours</span><span className="whitespace-pre-wrap break-words">{item.mine}</span></span>
              <span><span className="block text-xs text-slate-500">Current</span><span className="whitespace-pre-wrap break-words">{item.theirs}</span></span>
              <span className="flex gap-2">
                <button type="button" className={`${button} border-amber-400 bg-white hover:bg-amber-100`} onClick={() => onTakeTheirs(item)}>Take theirs</button>
                <button type="button" className={`${button} border-slate-400 bg-white hover:bg-slate-100`} onClick={() => onKeepMine(item)}>Keep mine</button>
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
