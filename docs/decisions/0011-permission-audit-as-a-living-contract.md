# 0011: Permissions are audited black-box against a hand-written matrix, in CI

Status: accepted (Phase 4).

## Context
The permission policy lives in one place (`PermissionPolicy` plus the state machine in engagement-service) and every other service asks it.
Unit tests prove the policy says what the author intended. They do not prove that every route actually asks, that the gateway exposes only
what it should, or that the behaviour a user sees across five services matches the rules in the plan.

## Decision
Two complementary checks, both run on every pull request:
- **Route inventory tests** (`AuditFlow.Security.Tests`, no Docker): map each service's real endpoints onto a host that is built but never
  started, and fail when a route lacks `RequireAuthorization` and is not on a short, explicit, per-service allow-list (login and the two
  discovery documents). `/internal/*` must always require authorization, and the gateway must route no internal path.
- **Permission audit** (`tools/permission-audit`, against the running stack): logs in as the five demo users, drives throwaway engagements
  through every lifecycle status via the real API, and compares the HTTP status of every role x status x operation (16 operations,
  841 checks over two lifecycles) with `matrix.json`. The matrix is written by hand from the plan's permission tables and is deliberately
  not generated from the code, so a regression in the policy shows as a mismatch instead of silently moving the expected value with it.
  A deliberately wrong matrix fails the run with exit code 1 and names the cells.

Changing a permission is therefore two edits in one pull request: the policy and the matrix. Reviewing the matrix diff is reviewing the
policy change in a form a non-author can read.

## Consequences
- The audit is slow (about 45 s) and needs the whole stack, so it runs in the compose smoke job, not with the unit tests.
- It checks which role may act, not what data a response contains; response scoping stays covered by service tests.
- It documents a real property of the design: after a submit the reviewer can act roughly a second later, when review-service and the access
  caches have seen the event (ADR 0006, 0009). The audit waits for the reviewer's own screen to allow a decision before testing reviewer actions.
- `/internal/*` endpoints accept any valid token and rely on the gateway not routing them. Service-to-service credentials are the documented
  step for a public deployment, not part of this POC.
