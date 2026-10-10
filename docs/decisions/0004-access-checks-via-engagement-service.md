# 0004: Identity in the token, per-engagement permissions from engagement-service

Status: accepted (Phase 1)

## Context
Roles apply **per engagement** (`EngagementParticipant`): the same user can be Auditor on one engagement and Reviewer on
another. A JWT issued at login cannot carry that, and permissions change with the engagement's status.

## Decision
- The JWT (RS256, signed by auth-service, validated by every service through OIDC discovery + JWKS) carries only identity and
  the user's **home role**: `sub`, `name`, `email`, `role`. The home role answers global questions only (may this user create
  engagements? which users can be picked as reviewers?).
- **engagement-service is the only authority for per-engagement permissions.** It owns `PermissionPolicy` (role x status ->
  capabilities) and `EngagementStateMachine` (legal transitions). Other services ask
  `GET /internal/engagements/{id}/access` (forwarding the caller's token) and receive
  `{role, status, version, capabilities[]}`; none of them re-implement the matrix.
- The same answer drives the UI: `allowedActions` / `capabilities` are returned on engagement reads and the client renders
  them without role logic.
- A participant's engagement role is their home role (Reviewers only via `reviewerUserId` at creation).
- Non-participants receive 403 (missing engagements 404).

## Consequences
- One place to change and test the matrix (154 unit tests cover it, including every invalid transition).
- A network call per authorized request in other services. Phase 2 adds a short cache (30 s) invalidated by
  `EngagementStatusChanged`; the write path still relies on the state machine's SQL guard
  (`WHERE Status = @from AND RowVer = @ver`).
- The token is valid for 60 minutes with no refresh in the POC; the client signs the user out on expiry or 401.
- Local issuer is not a full OAuth server. Services depend only on the metadata address and fixed issuer/audience, so Entra ID
  can replace it by configuration.
