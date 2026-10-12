# API reference (Phases 1 to 3)

All routes below go through the gateway (`http://localhost:5000`). Each service also serves Swagger at `/swagger`
(auth :5001, engagement :5002, ...). Authenticated routes take `Authorization: Bearer <accessToken>`.
Errors are RFC 7807 problem details (`422` carries an `errors` map; `409` carries `currentVersion` and, for multi-section saves, `resource`). Every response carries `X-Correlation-Id`, and
error bodies add `traceId` (Phase 4) so a failing call can be found in the trace backend. Unexpected errors return a generic 500 with no message, type or stack trace; every response
carries `X-Content-Type-Options: nosniff`.

## Auth (auth-service)

| Method | Path | Rule | Notes |
|---|---|---|---|
| POST | `/auth/login` | anonymous | `{email, password}` -> `{accessToken, expiresAt, user}`. Unknown email and wrong password both return 401 |
| GET | `/auth/me` | Auth | the caller |
| GET | `/auth/permissions` | Auth | `{canCreateEngagement}` (global capability; Auditor / Co-Auditor only) |
| GET | `/auth/users?homeRole=` | Auth | pickers for reviewer / participants |
| GET | `/.well-known/openid-configuration`, `/.well-known/jwks.json` | anonymous | how services validate tokens |

## Engagements (engagement-service)

| Method | Path | Rule | Notes |
|---|---|---|---|
| GET | `/engagements/creation-options` | Auth | period types, regions, config types, rule options (from backend config) |
| POST | `/engagements` | home role Auditor or Co-Auditor | 201 + `Location` + `ETag`; 403 / 422 otherwise. Creates the engagement, its participants and an `EngagementCreated` outbox event in one transaction |
| GET | `/engagements?scope=&status=&q=&page=&pageSize=` | Auth, own participations only | `scope`: `participating` (default), `owned`, `assigned`, `reviewing`; `status`: any lifecycle status; both case-insensitive. Returns `items`, `counts` for every status (ignoring the status filter), `total` |
| GET | `/engagements/{id}` | participant (else 403; unknown id 404) | `status`, `myRole`, `allowedActions[]`, `capabilities[]`, `version` (also `ETag`) |
| GET | `/engagements/{id}/participants` | participant | names and roles |
| POST | `/engagements/{id}/submit` | Auditor / Co-Auditor participant, status In Progress | **Phase 3.** No body. Asks workflow-service for readiness: **422** with `issues[]` (the blocking errors) when it fails; **403** from any other status or role; 409 only if `If-Match` is sent and stale (optional, see ADR 0009). Success: `200` with the engagement (status `UnderReview`), emits `EngagementSubmitted` and `EngagementStatusChanged` in one transaction |

Create body:
```json
{ "name": "...", "periodType": "Quarterly", "region": "EMEA", "reviewerUserId": "guid",
  "participantUserIds": ["guid"], "configuration": { "configTypes": ["Equities"], "options": { "includeReconciliation": true } } }
```
Rules: the reviewer must be a user whose home role is Reviewer and not the creator; participants' engagement role is their home
role; reviewers cannot also be participants; catalogue values only.

## Engagement details (engagement-service, Phase 2)

| Method | Path | Rule | Notes |
|---|---|---|---|
| PATCH | `/engagements/{id}` | Auditor / Co-Auditor participant, **Draft only** | body `{name?, periodType?, region?, reviewerUserId?}` (null = unchanged); `If-Match` required; 409 with `currentVersion` when stale; 403 once the engagement left Draft. Changing the reviewer swaps the participant row. Emits `EngagementUpdated`. The backend lists the `editDetails` action only in Draft |

## Workflow (workflow-service, Phase 2)

Writes need the editor capability (`content.edit`: Auditor / Co-Auditor while Draft, In Progress or Rejected; absent when Under Review or
Approved, so those return 403). Versions are opaque tokens (also the `ETag`).

| Method | Path | Rule | Notes |
|---|---|---|---|
| GET | `/engagements/{id}/tabs` | `engagement.view` | `{ready, tabs[{tabNo, title, issueCounts}]}`; `ready: false` while the workflow is still provisioned after creation (poll) |
| GET | `/engagements/{id}/tabs/{tabNo}` | `engagement.view` | full view model: visible sections only; per field `type, label, value, required, editable, issues`; per section `version, editable, attachment, issues`; tab 5 adds `reconciliation` rows |
| PATCH | `/engagements/{id}/sections/{sectionKey}` | `content.edit` + `If-Match` | body `{fields:{key:value}}`; returns `{section, summary, visibilityChanges}` and the new `ETag`; **409** (stale), **422** (unknown / hidden field, wrong type, bad version) |
| PATCH | `/engagements/{id}/tabs/{tabNo}` | `content.edit` | body `{sections:[{sectionKey, version, fields}]}`; **atomic**: one stale section rejects all (409 carries `resource`); returns `{tab, summary, visibilityChanges}` |
| GET | `/engagements/{id}/validation-summary` | `engagement.view` | `{counts, readyToSubmit, issues[], reconciliation[]}` for the banners |

A save that changes nothing is a no-op (no version bump, no event). Saving Tab 1 can flip other sections; the response lists them in
`visibilityChanges`. Issues have `severity` (Error / Warning / Information), `scope` (Field / Section / Tab / Engagement), a code, a message and
the tab / section / field they point at. `Error` issues block submission.

Field values on the wire: checkbox `true/false`, text and documentation strings, number (a JSON number or numeric text), date `yyyy-MM-dd`;
blank clears a non-checkbox field. Unknown fields and wrong types are rejected with `errors["section-2-2.bookValue"]`.

## Attachments (attachment-service, Phase 2)

| Method | Path | Rule | Notes |
|---|---|---|---|
| POST | `/engagements/{id}/attachments` | `attachment.upload` | multipart field `file`; whole-engagement scope |
| POST | `/engagements/{id}/tabs/{tabNo}/attachments` | `attachment.upload` | tab 1 to 5 |
| POST | `/engagements/{id}/sections/{sectionKey}/attachments` | `attachment.upload` | section must exist, be visible and accept attachments (checked with workflow-service) |
| GET | `/engagements/{id}/attachments` | `attachment.view` (**Viewer: 403**) | current files only |
| GET | `/engagements/{id}/attachments/{attachmentId}/download` | `attachment.view` | `application/octet-stream`, `attachment` disposition, `nosniff` |

Max 10 MB, any type. A new upload at the same scope **replaces** the previous file (only the latest is kept). 422 for empty / oversized files or
invalid scope; the file name is sanitized and stored as metadata only.

## Review (review-service, Phase 3)

The review screen is a distinct view over the same engagement. Content is read-only; the backend decides `canComment` and `canDecide`.

| Method | Path | Rule | Notes |
|---|---|---|---|
| GET | `/reviews/{engagementId}` | participant except **Viewer (403)** | `{engagement, cycle, canComment, canDecide, tabs[{tabNo, title, available, content}], validation, comments[], history[]}`. Composed live from engagement-service and workflow-service; a failing tab comes back `available: false` and the rest still loads. `cycle` is 0 before the first submission (then nothing can be commented or decided). Auditors and Co-Auditors get the same view with both flags false |
| POST | `/reviews/{engagementId}/comments` | `comment.add` (assigned Reviewer, Under Review) | `{tabNo 1-5, body (max 2000)}` -> 201 comment. **422** invalid tab or body; **403** otherwise; **409** if the review cycle is not open yet. Emits `ReviewerCommentAdded` (ids only) |
| POST | `/reviews/{engagementId}/approve` | `review.decide` | optional `{comment}` -> **202** `{decisionId, cycle, decision}`. The status change follows from the `ReviewApproved` event (about a second) |
| POST | `/reviews/{engagementId}/reject` | `review.decide` | `{comment}` **mandatory: 400** when missing or blank. 202 as above; emits `ReviewRejected`. **409** if the cycle already has a decision |

Comments are flat, append-only, per tab and per review cycle. Each submission opens a cycle (1, 2, ...); one decision per cycle (unique constraint).

## Live updates (notification-service, Phase 3)

`GET /engagements/{id}/events/stream` (`text/event-stream`, Bearer header, so use a fetch-based client). Requires `engagement.view`.

| Frame | Meaning |
|---|---|
| `event: ready` (`id` = current position) | stream open; refetch what you show to close the gap since your last read |
| `id: <seq>` `event: <EventType>` `data: <envelope>` | a domain event for this engagement. `data` carries names, ids and versions, never field values; refetch the changed section through the normal API |
| `event: resync` | you missed too much (gap over 500 events or a slow consumer); refetch everything |
| `event: reauth` | the token expired; the stream closes |
| `: heartbeat` | every 15 s |

Reconnect with `Last-Event-ID` (or `?lastEventId=`) to replay what was missed for that engagement. Callers without `attachment.view` never
receive `AttachmentAdded`; callers without `comment.view` never receive `ReviewerCommentAdded` (so Viewers get neither).

## Roles at a glance

| | View tabs | Edit / upload | See attachments | Edit details |
|---|---|---|---|---|
| Auditor, Co-Auditor | yes | yes (not Under Review / Approved) | yes | Draft only |
| Reviewer | yes, read-only | no (comments and decisions on the review screen only, while Under Review) | yes, download only | no |
| Viewer | yes, read-only | no | **no** (403, and no attachment info in the tab view) | no | 


## Internal (not routed by the gateway)

| Method | Path | Service | Purpose |
|---|---|---|---|
| POST | `/internal/users/lookup` | auth | batch user lookup for validation and display-name snapshots |
| GET | `/internal/engagements/{id}/access` | engagement | `{role, status, version, capabilities[]}` for the calling user (cached 30 s by consumers, evicted on `EngagementStatusChanged`) |
| GET | `/internal/engagements/{id}/submission-readiness` | workflow | `{readyToSubmit, errors[]}`; consumed by `POST /engagements/{id}/submit` |
| GET | `/internal/engagements/{id}/sections/{sectionKey}` | workflow | `{exists, visible, tabNo, attachmentAllowed}` for attachment-service |

## Demo users (password `Passw0rd!`, dev only)

alice (Auditor), bob (Co-Auditor), rachel and rohan (Reviewers), victor (Viewer), all `@auditflow.test`.
Seeded engagements (each with a different Tab 1 configuration, so different sections show):
ENG-1001 Draft (Equities), 1002 In Progress (FX + Derivatives, reconciliation), 1003 Under Review (Derivatives + Equities),
1004 Approved (Fixed Income, legal review), 1005 Rejected (Equities + FX).
