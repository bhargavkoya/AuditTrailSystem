# Manual testing guide

Scenarios to verify AuditFlow by hand on a local machine. Each one lists the user, the steps and the expected result, so you can tick them off.
Automated checks are in the last section. Rules referenced here come from the PRD, `docs/api/README.md` and the rule set at
`services/workflow-service/AuditFlow.Workflow.Api/RuleSets/ruleset-v1.json`.

> The UI scenarios were written from the code and docs. Tick them off as you run them and note anything that differs.

## 0. Setup

```cmd
docker compose --profile apps up -d --build
cd frontend && npm install && cd apps\web && npm run dev
```
App: http://localhost:5173. Gateway: http://localhost:5000. Full setup and troubleshooting: [local-setup.md](architecture/local-setup.md).

Check the stack is healthy before testing: `curl http://localhost:5000/health/engagement` (and `auth`, `workflow`, `attachment`, `review`, `notification`) returns 200.

Reset to a clean seeded state at any time (wipes all data): `docker compose --profile apps --profile observability down -v`, then start again.

### Test data

Password for every user: `Passw0rd!`

| User | Role |
|---|---|
| alice@auditflow.test | Auditor |
| bob@auditflow.test | Co-Auditor |
| rachel@auditflow.test | Reviewer |
| rohan@auditflow.test | Reviewer |
| victor@auditflow.test | Viewer |

| Engagement | Status | Owner | Reviewer | Other participants | Tab 1 configuration |
|---|---|---|---|---|---|
| ENG-1001 | Draft | Alice | Rachel | Bob, Victor | Equities |
| ENG-1002 | In Progress | Alice | Rohan | none | FX, Derivatives, reconciliation on |
| ENG-1003 | Under Review | Bob | Rachel | Alice, Victor | Derivatives, Equities, reconciliation on |
| ENG-1004 | Approved | Alice | Rachel | none | Fixed Income, reconciliation on, legal review on |
| ENG-1005 | Rejected | Alice | Rohan | Bob, Victor | Equities, FX, reconciliation on |

Use two browser profiles (or one normal and one private window) for the multi-user scenarios, so each has its own login.

---

## 1. Sign-in and dashboard (FR: auth, dashboard)

| # | Steps | Expected |
|---|---|---|
| 1.1 | Sign in as alice | Dashboard loads with summary cards per status (Draft, In Progress, Under Review, Approved, Rejected) |
| 1.2 | Alice: look at the list | Lists ENG-1001 to ENG-1005 with ID, name, region, period type, reviewer, status, last updated, my role |
| 1.3 | Alice: use the scope filters (owned / assigned / reviewing / participating) | List changes accordingly; ENG-1003 is "assigned"/"participating" (Bob owns it). Status counts on the cards ignore the status filter |
| 1.4 | Alice: search by name (e.g. "FX") and by status | List narrows to matching rows |
| 1.5 | Sign in as rachel | Only the engagements she is on (1001, 1003, 1004); "Reviewing" filter works; "Review" quick action appears only on ENG-1003 (Under Review) |
| 1.6 | Sign in as victor | No "New engagement" button; no review actions |
| 1.7 | Wrong password or unknown email | Login rejected with the same generic message for both |
| 1.8 | Open http://localhost:5173 directly in a fresh window | Redirected to sign-in, not an error page |

## 2. Engagement creation (Auditor / Co-Auditor only)

| # | Steps | Expected |
|---|---|---|
| 2.1 | Alice: **New engagement**. Name "Test engagement", period type, region, reviewer Rachel, add Victor as participant, tick Equities and Derivatives, tick reconciliation | Created; appears in the list as **Draft** with "Setting up the workflow..." for about a second, then the five tabs become available |
| 2.2 | Bob does the same | Allowed (Co-Auditor has identical permissions) |
| 2.3 | Try to choose Alice (an Auditor) as reviewer, or the reviewer also as participant | Rejected by the backend with a validation message (reviewer must have the Reviewer role, not be the creator, not also a participant) |
| 2.4 | Leave the name empty | Validation error, nothing created |
| 2.5 | Edit name/period/region/reviewer on the Draft engagement | Allowed while **Draft** only. After the first content edit (status In Progress) the edit-details action disappears |
| 2.6 | Rachel or Victor | Cannot create (no button; `POST /engagements` returns 403) |

## 3. Tab 1 drives everything downstream (backend-controlled UI)

Use **ENG-1002** as Alice.

| # | Steps | Expected |
|---|---|---|
| 3.1 | Open the five tabs | Titles: Input Configuration, Data Collection, Audit Procedures, Documentation & Notes, Reconciliation & Submission. Each tab shows an issue-count badge |
| 3.2 | Tab 2 | Sections shown: Source data extract, Foreign exchange positions, Derivatives positions (not Equities or Fixed Income) |
| 3.3 | Tab 1: untick **Derivatives**, Save | "Derivatives positions" disappears from Tab 2 and "Derivatives model validation" from Tab 3. Their errors vanish from the banners. Saved values are not lost |
| 3.4 | Tick **Derivatives** again, Save | Sections return with their previous values |
| 3.5 | Tab 1: tick **Requires legal review** | Tab 3 gains "Legal review" with a required sign-off date and a required attachment |
| 3.6 | Tab 1: untick all four configuration types | Error banner: "Select at least one configuration type." Submission stays blocked |
| 3.7 | Tab 1: set period end before period start | Field error: "Period end must be on or after the period start." |
| 3.8 | Tab 1: untick **Include reconciliation** | Tab 5 shows the Information message "Reconciliation is not part of this engagement's scope." and ledger values stop being required |
| 3.9 | Compare the UI with `GET /engagements/ENG-1002/tabs/{n}` (section 11) | Section visibility and "required" flags always match the API response |

## 4. Validation banners and summary

| # | Steps | Expected |
|---|---|---|
| 4.1 | ENG-1002, any tab | Top-level banners grouped as **Error / Warning / Information**; clicking one jumps to the field |
| 4.2 | Tab 4, Audit summary: enter fewer than 50 characters | Warning: "The audit summary is very short..." (non-blocking) |
| 4.3 | Tab 2, Source data extract: clear Record count | Information: "A record count helps the reviewer..." |
| 4.4 | Clear a required field (e.g. Source system) | Error appears at field, section and tab level; tab badge count goes up |
| 4.5 | `GET /engagements/ENG-1002/validation-summary` (see section 11) | `counts`, `readyToSubmit`, `issues[]`, `reconciliation[]` match the banners |
| 4.6 | Wrong type, e.g. text in a number field via the API | 422 with an error under the field key |

## 5. Reconciliation (Tab 5)

Use **ENG-1002** (reconciliation on). Thresholds: percent warning 1 / significant 5; value warning 10,000 / significant 100,000.

| # | Steps | Expected |
|---|---|---|
| 5.1 | Tab 2 *Derivatives positions*: book value 1,000,000, ledger value 1,000,000 | Tab 5 row shows no variance |
| 5.2 | Change ledger value to 1,005,000 (0.5%, 5,000) | No variance (below both warning thresholds) |
| 5.3 | Change ledger value to 1,020,000 (2%, 20,000) | **Warning**-level variance; Tab 4 "Exceptions and notes" shows a warning asking you to explain it |
| 5.4 | Change ledger value to 1,080,000 (8%, 80,000) | **Significant** variance on Tab 5; Error "A comment is required while a significant variance exists." until you fill the Reconciliation comment; submission is blocked |
| 5.5 | Fill the Reconciliation comment | Error clears (significant variance remains visible as an indicator) |
| 5.6 | Clear a ledger value while reconciliation is on | Missing value surfaced before submission |

## 6. Attachments (latest file only)

| # | Steps | Expected |
|---|---|---|
| 6.1 | Alice, ENG-1002 Tab 2 *Source data extract*: upload `a.txt` | Shown on the section; the section's required-attachment error clears |
| 6.2 | Upload `b.txt` at the same section | Only `b.txt` is shown; there is no version history |
| 6.3 | Upload to Tab 3 (tab level) and to the engagement (engagement level) | Each scope shows its own latest file |
| 6.4 | Download an attachment | File downloads with its original (sanitized) name |
| 6.5 | Upload an empty file, or one over 10 MB | Rejected (422) with a message |
| 6.6 | Rachel (Reviewer) | Sees and downloads attachments; no upload control |
| 6.7 | Victor (Viewer) on ENG-1001 or ENG-1003 | **No attachment UI at all**; `GET /engagements/ENG-1003/attachments` returns 403 |
| 6.8 | Upload to a section hidden by Tab 1 (via API) | Rejected |

## 7. Optimistic concurrency (409)

| # | Steps | Expected |
|---|---|---|
| 7.1 | Open **ENG-1005** Tab 2 in two browser profiles: Alice in one, Bob in the other (both are editors there) | Both load the same section version |
| 7.2 | Browser A: change *Source system*, Save | Saves; new version returned |
| 7.3 | Browser B (still on the old version): change the same field, Save | **409**: "Someone else changed ..."; your unsaved text is kept and the "yours vs current" values are shown so you can choose |
| 7.4 | Browser B: resolve and save again | Succeeds against the new version |
| 7.5 | Browser A saves with an unchanged value | No-op: version does not bump, no event emitted |
| 7.6 | API: `PATCH` a section with a stale `If-Match` (section 11) | 409 with `currentVersion` |
| 7.7 | API: save a whole tab where one section is stale | Atomic: the entire save is rejected (409 with `resource`) |

## 8. Submit and review lifecycle

The state machine is `Draft -> In Progress -> Under Review -> Approved | Rejected`, and `Rejected -> In Progress` on rework.

| # | Steps | Expected |
|---|---|---|
| 8.1 | Alice on **ENG-1001** (Draft): edit any field | A moment later the status becomes **In Progress** (event-driven, not instant) |
| 8.2 | Alice on **ENG-1005** (Rejected): edit a field | Status becomes **In Progress** |
| 8.3 | Alice on ENG-1002 with blocking errors: **Submit for review** | Refused with the blocking errors listed (422). Status stays In Progress |
| 8.4 | Fix every Error (required fields, attachments, significant-variance comment) and submit again | Status becomes **Under Review**; content is now locked for editors |
| 8.5 | Submit twice quickly (double-click, or two tabs) | Only one submission takes effect |
| 8.6 | Alice or Bob on **ENG-1003** (Under Review) | All tabs read-only; edit attempts via API return 403 |
| 8.7 | Rachel on ENG-1003: open the **review screen** | Distinct view: read-only tabs, validation summary, comment boxes, Approve and Reject, decision history |
| 8.8 | Rachel: add a comment on Tab 2 | Appears in the comment list for that tab and review cycle |
| 8.9 | Rachel: **Reject** with no text | 400, message shown, status unchanged |
| 8.10 | Rachel: Reject with a comment | Status becomes **Rejected** on every open screen without reload; history shows the decision and comment |
| 8.11 | Bob edits ENG-1003 after rejection | Back to **In Progress** (rework) |
| 8.12 | Bob submits again (cycle 2), Rachel approves | **Approved**; everything read-only; history shows both cycles |
| 8.13 | Rohan (a Reviewer, but not on ENG-1003) tries `/reviews/ENG-1003` | 403 |
| 8.14 | Alice/Bob on a reviewed engagement | "View review" shows the feedback, with approve/reject disabled (`canComment`/`canDecide` false) |
| 8.15 | **ENG-1004** (Approved) as Alice | Read-only; edits and uploads return 403 |
| 8.16 | Victor on any review URL | 403: Viewers cannot see the review screen |

## 9. Roles and permissions

| Action | Alice / Bob | Rachel / Rohan (assigned) | Victor |
|---|---|---|---|
| Create engagement | yes | no | no |
| Edit tabs (Draft / In Progress / Rejected) | yes | no | no |
| Upload attachments | yes | no | no |
| View attachments | yes | yes (download) | **no** |
| View comments | yes (read) | yes | **no** |
| Add comment, approve, reject | no | yes, only while Under Review | no |
| Submit | yes | no | no |
| View tabs and engagement details | yes | yes (read-only) | yes (read-only) |

Roles apply **per engagement**: the same user may be Reviewer on one engagement and have no access to another. Confirm that
Rachel gets 403 on ENG-1002 (Rohan is its reviewer) and Rohan gets 403 on ENG-1003.

The black-box audit in section 12 checks every combination of role, status and operation, so use it as the exhaustive version of this table.

## 10. Live updates (SSE)

Two browser profiles, both on **ENG-1002** (Alice) and a third on ENG-1003 if you want to see the isolation.

| # | Steps | Expected |
|---|---|---|
| 10.1 | Look at the header | A **Live** indicator is on once the stream is connected |
| 10.2 | Browser A: save a section | Browser B updates only that section (no full reload), or shows "... changed ..." if B has unsaved edits in it |
| 10.3 | Browser A uploads an attachment | Browser B refreshes the attachment for that scope. Victor's stream never gets this event |
| 10.4 | Rachel rejects ENG-1003 | Status banners on all open ENG-1003 screens change without reload |
| 10.5 | Stop the notification service (`docker compose stop notification-service`), then start it again | Live dot goes off, then returns; the UI catches up (replay via `Last-Event-ID` or a full resync) |
| 10.6 | Raw stream: `curl -N -H "Authorization: Bearer <token>" http://localhost:5000/engagements/ENG-1003/events/stream` | `event: ready`, then events with `id:`; payloads contain ids, names and versions, **never field values or the full engagement**. Heartbeat comment every 15 s |
| 10.7 | Reconnect with `-H "Last-Event-ID: 2"` | Missed events replay in order |
| 10.8 | Stream as Victor | No `AttachmentAdded` and no `ReviewerCommentAdded` events |
| 10.9 | Stream for an engagement you are not on | 403 |

## 11. API checks with curl

Get a token (cmd.exe quoting shown; in PowerShell use `curl.exe` and single-quoted JSON):

```cmd
curl -s -X POST http://localhost:5000/auth/login -H "Content-Type: application/json" -d "{\"email\":\"alice@auditflow.test\",\"password\":\"Passw0rd!\"}"
```
Copy `accessToken` from the response, then:

```cmd
set TOKEN=<paste accessToken>
curl -s http://localhost:5000/auth/me -H "Authorization: Bearer %TOKEN%"
curl -s "http://localhost:5000/engagements?scope=owned" -H "Authorization: Bearer %TOKEN%"
curl -s http://localhost:5000/engagements/ENG-1002 -H "Authorization: Bearer %TOKEN%"
curl -s http://localhost:5000/engagements/ENG-1002/validation-summary -H "Authorization: Bearer %TOKEN%"
curl -s http://localhost:5000/engagements/ENG-1002/tabs/2 -H "Authorization: Bearer %TOKEN%"
```

| # | Request | Expected |
|---|---|---|
| 11.1 | Any protected route without a token | 401 |
| 11.2 | `GET /engagements/ENG-1003` as victor (a participant) | 200; `myRole` is Viewer |
| 11.3 | `GET /engagements/ENG-1002` as victor (not a participant) | 403 |
| 11.4 | `GET /engagements/ENG-9999` | 404 |
| 11.5 | `PATCH /engagements/ENG-1002/sections/section-2-1` with body `{"fields":{"sourceSystem":"SAP"}}` and the `If-Match` set to the section `version` from the tab response | 200, new `ETag` and version in the response |
| 11.6 | Repeat 11.5 with the old `If-Match` | 409 with `currentVersion` |
| 11.7 | Same PATCH with no `If-Match` | Rejected (check the status code in Swagger; not asserted in this guide) |
| 11.8 | Same PATCH with `{"fields":{"recordCount":"abc"}}` | 422 with `errors["section-2-1.recordCount"]` |
| 11.9 | Same PATCH as rachel or victor | 403 |
| 11.10 | `POST /engagements/ENG-1002/submit` as alice with blocking errors | 422 with `issues[]` |
| 11.11 | `POST /reviews/ENG-1003/reject` as rachel with body `{}` | 400 (comment mandatory) |
| 11.12 | `POST /reviews/ENG-1003/approve` twice as rachel | Second call is 409 (one decision per cycle) |
| 11.13 | `PATCH /engagements/ENG-1003/...` content as alice | 403 (Under Review is locked) |
| 11.14 | Look at response headers | `X-Correlation-Id` and `X-Content-Type-Options: nosniff` on every response; error bodies include `traceId` |

Swagger for each service: http://localhost:5001/swagger to http://localhost:5006/swagger (auth, engagement, workflow, attachment, review, notification).

## 12. Automated checks

```powershell
dotnet build AuditFlow.sln
dotnet test AuditFlow.sln --filter "Category!=Integration"   # fast unit tests, no Docker
dotnet test AuditFlow.sln                                    # includes Docker integration tests (SQL Server via Testcontainers)

# against the running stack
./infra/local/permission-audit.ps1    # every role x status x operation vs tools/permission-audit/matrix.json, ~45 s, exit 1 on mismatch
./infra/local/load-test.ps1 -Smoke    # 20-user smoke; omit -Smoke for 100 users + 100 SSE streams (~3.5 min)
```
The frontend is verified manually with the scenarios above; `cd frontend/apps/web && npm run build` type-checks and builds it.

## 13. Observability and resilience

| # | Steps | Expected |
|---|---|---|
| 13.1 | Start with `./infra/local/up.ps1 -Observability` and open http://localhost:18888 | Aspire dashboard shows traces, metrics and logs from all services |
| 13.2 | Save a section, then find the trace | One trace runs from the gateway request through the outbox and Service Bus to the consumers (notification, review, etc.) |
| 13.3 | Send a request with `X-Correlation-Id: my-test-id` | The same id appears in the response header, logs and the outbox/message processing logs |
| 13.4 | `docker compose stop servicebus`, save a section, start it again | The save still succeeds; the outbox publishes the event after the broker returns, and the UI catches up |
| 13.5 | `GET /health/live` and `/health` on a service | `/health` reports dependency readiness (database, broker), `/health/live` only liveness |
| 13.6 | Redeliver the same event (integration tests cover this) | Consumers are idempotent: no duplicate comment, decision or status change |

See [runbook.md](architecture/runbook.md) and [observability.md](architecture/observability.md) for troubleshooting.

## 14. Known limits (not bugs)

- Status changes after a save, submit or review decision arrive via events, so they appear about a second later.
- Only the latest attachment per scope is kept; there is no version history (a PRD non-goal).
- Reconciliation uses simple threshold rules, not production financial formulas.
- There is no admin UI for the rule set; it is a JSON file in the workflow service.
- Local secrets, the SQL password and the demo users are dev defaults.
