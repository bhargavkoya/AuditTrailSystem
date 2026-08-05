# AuditTrailSystem

# Stack
- ASP.NET Core Web API (.NET 8), microservices split by audit type/domain
- PostgreSQL (per-service or shared DB — shared is fine for a base demo, note in README it'd split in prod)
- Azure Service Bus (or local emulator — Azure Service Bus emulator / or swap to RabbitMQ for local dev if the Azure emulator is painful) for - async inter-service updates
- Redis for caching initial/lookup data
- JWT auth (OAuth 2.0 flow can be simplified to just JWT issuance for the demo)

# Core scope (base-level)
Core domain: "Matter" (your real term) — an audit instance with:
Matter details (metadata: type, owner, dates)
4 sequential substeps (model this as a state machine: Step1 → Step2 → Step3 → Step4, each with its own status)
Workflow states: Draft → Submitted → Under Review → Closed (per your confirmed answer) — track this at the Matter level and optionally per substep
Microservices (pick 2, not a huge fleet):
Matter Service — owns Matter + MatterDetails + substep state, core CRUD + workflow transitions
Notification/Downstream Service — listens on Service Bus for "Matter substep completed" events, does something simple downstream (e.g. writes an audit log entry, or "notifies" via email/log)
Service Bus flow: Matter Service publishes an event when a substep completes → Notification Service consumes it asynchronously → confirms decoupling
Redis caching: Cache "lookup data" — e.g. audit type definitions, static reference data needed when loading a Matter — so the UI loads instantly without hitting the DB every time
UI: List of Matters with status, detail view showing the 4 substeps and their state, ability to advance a substep (draft→submitted→reviewed→closed)

# Explicitly out of scope for base version
Full OAuth 2.0 authorization server (JWT issuance is enough)
Azure Functions (mention in README as "in the real system, Azure Functions handled X" — don't need to build serverless infra for a demo)
More than 2 services — a 3rd service is a "if I have time" stretch
