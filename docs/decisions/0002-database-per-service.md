# 0002: One database per service on a shared SQL Server instance

Status: accepted (Phase 1)

## Context
The PRD requires real microservices with no modular-monolith fallback. The Service Bus emulator already needs a SQL Server
container, so one instance is the cheapest local setup.

## Decision
Each service owns a separate **database** (`AuditFlow_Auth`, `_Engagement`, `_Workflow`, `_Attachment`, `_Review`,
`_Notification`) on the same instance. No service reads another service's database. Data that another service needs travels
by API call (`/internal/users/lookup`, `/internal/engagements/{id}/access`) or by event.

- Migrations are embedded SQL scripts applied by DbUp at startup (`MigrationHostedService`), including database creation.
- Seeders run after migrations and are idempotent.
- Snapshots instead of joins: engagement-service stores participants' display names at creation time rather than reading auth-service's tables.
- Row versions (`rowversion`) live on the aggregates that need optimistic concurrency (`Engagements` now; configurations, tabs and
  sections in Phase 2).

## Consequences
- Separate databases (not just schemas) make "no cross-service reads" enforceable by connection string.
- Display names can go stale after a rename; acceptable for the POC.
- Production could move each database to its own server without code changes.
