import { useRef } from 'react'
import { ApiError, downloadFile } from '@shared/api/client'
import type { Attachment } from '@shared/api/types'
import { useUploadAttachment } from './api'

function formatSize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

/**
 * One attachment slot (section, tab or whole engagement). Only the latest file is shown: uploading again replaces it.
 * The caller decides whether to render this at all (not for roles without attachment access) and whether uploads are
 * allowed; both answers come from the backend.
 */
export function AttachmentPanel({
  engagementId,
  title,
  uploadPath,
  current,
  canUpload,
  required = false,
}: {
  engagementId: string
  title: string
  /** Relative to /engagements/{id}, e.g. /sections/section-2-1/attachments */
  uploadPath: string
  current: Attachment | null
  canUpload: boolean
  required?: boolean
}) {
  const upload = useUploadAttachment(engagementId)
  const input = useRef<HTMLInputElement>(null)
  const error = upload.error instanceof ApiError ? (upload.error.problem.errors ? Object.values(upload.error.problem.errors).flat().join(' ') : upload.error.message) : null

  return (
    <div className="rounded border border-dashed bg-slate-50 px-3 py-2 text-sm" aria-label={`${title} attachment`}>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <span className="font-medium">{title}</span>
        {required && !current && <span className="rounded bg-red-100 px-1.5 py-0.5 text-xs font-medium text-red-700">Required</span>}

        {current ? (
          <span className="flex items-center gap-2">
            <button
              type="button"
              className="text-blue-700 underline-offset-2 hover:underline"
              onClick={() => void downloadFile(`/engagements/${engagementId}/attachments/${current.attachmentId}/download`, current.fileName)}
            >
              {current.fileName}
            </button>
            <span className="text-xs text-slate-500">
              {formatSize(current.sizeBytes)} · {current.uploadedByName} · {new Date(current.uploadedAt).toLocaleString()}
            </span>
          </span>
        ) : (
          <span className="text-xs text-slate-500">No file attached</span>
        )}

        {canUpload && (
          <>
            <input
              ref={input}
              type="file"
              className="sr-only"
              aria-label={`${current ? 'Replace' : 'Upload'} ${title} file`}
              onChange={(e) => {
                const file = e.target.files?.[0]
                if (file) upload.mutate({ path: uploadPath, file })
                e.target.value = ''
              }}
            />
            <button
              type="button"
              disabled={upload.isPending}
              onClick={() => input.current?.click()}
              className="ml-auto rounded border bg-white px-2.5 py-1 text-xs font-medium hover:bg-slate-100 disabled:opacity-50"
            >
              {upload.isPending ? 'Uploading…' : current ? 'Replace file' : 'Upload file'}
            </button>
          </>
        )}
      </div>
      {error && <p role="alert" className="mt-1 text-xs text-red-700">{error}</p>}
    </div>
  )
}
