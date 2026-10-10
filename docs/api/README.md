# API reference (Phase 1)

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

## Internal (not routed by the gateway)

| Method | Path | Service | Purpose |
|---|---|---|---|
| POST | `/internal/users/lookup` | auth | batch user lookup for validation and display-name snapshots |
| GET | `/internal/engagements/{id}/access` | engagement | `{role, status, version, capabilities[]}` for the calling user |

## Dev only

| Method | Path | Service | Notes |
|---|---|---|---|
| GET | `/dev/events/recent?engagementId=&take=` | notification (:5006, Development / compose) | events received so far; removed when SSE arrives (Phase 3) |

## Demo users (password `Passw0rd!`, dev only)

alice (Auditor), bob (Co-Auditor), rachel and rohan (Reviewers), victor (Viewer), all `@auditflow.test`.
Seeded engagements: ENG-1001 Draft, 1002 In Progress, 1003 Under Review, 1004 Approved, 1005 Rejected.
