# 0001: YARP gateway as infrastructure only

Status: accepted (Phase 0/1)

## Context
The PRD's API paths overlap across services (`/engagements/{id}/...` is served by engagement, workflow, attachment and
notification). The browser should see one origin, and the SSE stream needs one stable URL.

## Decision
Run a YARP reverse proxy (`services/gateway`) that routes by path to the owning service. It contains **no business logic**:
no authorization decisions, no aggregation. Services validate JWTs themselves.

- Routes today: `/auth/**` and `/.well-known/**` to auth-service, `/engagements/**` to engagement-service, `/health/{service}`.
- Routes are config-driven (`ReverseProxy` section), so more specific workflow/attachment routes can be added in Phase 2.
- **`/internal/**` is deliberately not routed.** Service-to-service endpoints are reachable only on the Docker network.
- The correlation id header is preserved and generated when absent.

## Consequences
- The frontend needs one base URL; no CORS in dev (Vite proxies to the gateway).
- One more hop, one more deployable. Acceptable for a POC and easy to remove.
- The gateway must not buffer responses once SSE arrives (Phase 3); to be verified then.
