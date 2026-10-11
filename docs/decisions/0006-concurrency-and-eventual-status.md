# 0006: Section-level optimistic concurrency and event-driven status

Status: accepted (Phase 2).

## Context
Several people can edit the same engagement. Edits must never be lost silently, a 409 must be clean, and the engagement status
(Draft -> In Progress, Rejected -> In Progress) belongs to engagement-service while the edit happens in workflow-service.

## Decision
**Concurrency unit = the section.** `WorkflowSections.RowVer` (SQL Server `rowversion`) moves on every save and every visibility flip.
The client sends the version it loaded (`If-Match` for a single section, per-section `version` in a tab PATCH). A stale version
returns **409** with `currentVersion` and, for a tab save, `resource` (the conflicting section). A tab PATCH is an **atomic batch**:
one stale section rolls everything back.

**Per-engagement write lock.** Every save, and every consumer that recomputes, takes `UPDLOCK, HOLDLOCK` on the engagement's
`EngagementConfigurations` row at the start of its transaction. Writes to one engagement are therefore serialized, so the whole-engagement
re-evaluation (visibility, summary hash) never works from a snapshot another save is changing. The section `RowVer` check inside the
`UPDATE ... WHERE RowVer = @v` remains the guard that decides who loses. Parallel saves with the same version: exactly one wins
(integration test with 2 and with 8 parallel writers).

**No-op saves** (no field actually changed) write nothing, bump nothing and emit nothing.

**Locked engagements return 403, not 409.** Under Review and Approved remove `content.edit` / `attachment.upload` from the access answer,
so the capability is absent. 409 is reserved for version conflicts.

**Status is eventually consistent.** workflow-service publishes `SectionUpdated` / `ConfigurationChanged`; engagement-service consumes
them and drives `StartWork` / `StartRework` through its state machine (`UPDATE ... WHERE Status = @from`), then publishes
`EngagementStatusChanged`, which also evicts the 30 s access cache in workflow and attachment. The alternative, a synchronous call from
workflow to engagement inside the save, would couple the two services' availability.

## Consequences
- After the first edit of a Draft the header can show "Draft" for a moment (about one outbox poll plus a broker hop). The UI refetches
  the engagement after a save and again two seconds later; SSE in Phase 3 removes the lag.
- Saves to one engagement are serialized. Fine for about 100 users; contention per engagement is low.
- Moving a section's version on a visibility flip means a user editing a section that Tab 1 just hid or revealed gets a 409 on the
  next save, which is the correct outcome.
- Events for the same engagement can be handled out of order by concurrent consumers; the handlers are written so order does not matter
  (a guarded transition, "touch activity").
