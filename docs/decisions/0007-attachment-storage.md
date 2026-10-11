# 0007: Attachment storage and latest-only files

Status: accepted (Phase 2). Resolves PRD open decision 2 (attachment storage strategy).

## Context
Attachments exist at section, tab and whole-engagement level. Only the latest file per scope is visible; there is no version history
(non-goal). Viewers must not see attachments at all.

## Decision
- **Metadata in SQL, bytes behind `IFileStore`.** The POC implementation is `LocalFileStore` on a Docker volume (`/data/attachments`).
  Keys are generated (`{engagementId}/{guid}`), never derived from the uploaded name, and paths that escape the root are refused.
  Azure Blob Storage would be another `IFileStore`; it is documented, not built.
- **Latest only, enforced by the database.** `Attachments.ScopeKey` is a computed column (`engagement`, `tab:3`, `section:section-2-1`)
  and a **filtered unique index** `(EngagementId, ScopeKey) WHERE IsCurrent = 1` allows one current row per scope. A replacement marks
  the old row not current and deletes its file after the transaction commits. Uploads to one scope are serialized with an application
  lock (`sp_getapplock`), so concurrent uploads cannot trip the index.
- **Limits and hygiene.** 10 MB, any type. The file name is sanitized and kept as metadata only; the content type is recorded but not
  trusted. Downloads are always `application/octet-stream` with `Content-Disposition: attachment` and `X-Content-Type-Options: nosniff`.
- **Authorization** comes from engagement-service: upload needs `attachment.upload`, list and download need `attachment.view`
  (Viewers have neither; Reviewers only the second; locked engagements lose the first). Section scope is validated by asking
  workflow-service (`/internal/engagements/{id}/sections/{key}`): the section must exist, be visible and accept attachments.
- **Events, not reads.** An upload writes `AttachmentAdded` to the outbox. workflow-service records the scope in `AttachmentPresence`
  so "attachment required" rules work without reading the attachment database; the presence row may arrive before the engagement is
  provisioned and is picked up at provisioning.
- Demo data: attachment-service attaches sample files to seeded Under Review / Approved / Rejected engagements when it sees their
  `EngagementCreated` event, through the same writer and events as a real upload.

## Consequences
- A crash between writing the file and committing the row leaves an orphan file (cleaned up on the error path, not by a sweeper).
- A replaced file is gone; "no version history" is literal.
- Antivirus scanning, content-type verification and per-file encryption are out of scope for the POC.
- Multiple instances would need a shared volume or blob storage.
