# API reference (Phases 1 and 2)

All routes below go through the gateway (`http://localhost:5000`). Each service also serves Swagger at `/swagger`
(auth :5001, engagement :5002, ...). Authenticated routes take `Authorization: Bearer <accessToken>`.
Errors are RFC 7807 problem details (`422` carries an `errors` map). Every response carries `X-Correlation-Id`.

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

## Roles at a glance (Phase 2 behaviour)

| | View tabs | Edit / upload | See attachments | Edit details |
|---|---|---|---|---|
| Auditor, Co-Auditor | yes | yes (not Under Review / Approved) | yes | Draft only |
| Reviewer | yes, read-only | no | yes, download only | no |
| Viewer | yes, read-only | no | **no** (403, and no attachment info in the tab view) | no |

## Internal (not routed by the gateway)

| Method | Path | Service | Purpose |
|---|---|---|---|
| POST | `/internal/users/lookup` | auth | batch user lookup for validation and display-name snapshots |
| GET | `/internal/engagements/{id}/access` | engagement | `{role, status, version, capabilities[]}` for the calling user (cached 30 s by consumers, evicted on `EngagementStatusChanged`) |
| GET | `/internal/engagements/{id}/submission-readiness` | workflow | `{readyToSubmit, errors[]}`; consumed by submit in Phase 3 |
| GET | `/internal/engagements/{id}/sections/{sectionKey}` | workflow | `{exists, visible, tabNo, attachmentAllowed}` for attachment-service |

## Dev only

| Method | Path | Service | Notes |
|---|---|---|---|
| GET | `/dev/events/recent?engagementId=&take=` | notification (:5006, Development / compose) | events received so far; removed when SSE arrives (Phase 3) |

## Demo users (password `Passw0rd!`, dev only)

alice (Auditor), bob (Co-Auditor), rachel and rohan (Reviewers), victor (Viewer), all `@auditflow.test`.
Seeded engagements (each with a different Tab 1 configuration, so different sections show):
ENG-1001 Draft (Equities), 1002 In Progress (FX + Derivatives, reconciliation), 1003 Under Review (Derivatives + Equities),
1004 Approved (Fixed Income, legal review), 1005 Rejected (Equities + FX).
