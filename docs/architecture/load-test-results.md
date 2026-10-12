# Load check (~100 users): method, first results, what was tuned

`./infra/local/load-test.ps1` (script: `tools/load-test/load.mjs`, Node 20+, no dependencies) drives the running stack through the gateway.
Numbers below come from one developer laptop running the whole system in Docker (12 CPUs and 8 GB given to the Docker VM, with SQL Server shared by the
six databases **and the Service Bus emulator**, and the load generator on the same machine). They guard against regressions and show where the
bottlenecks are; they are not capacity planning for Azure.

## What the test does

1. **Setup:** one throwaway engagement per virtual user (70 for the editors) plus one shared engagement for the contention group; waits until
   workflow-service has provisioned each.
2. **Streams:** every virtual user holds an SSE stream (100 in total: editors on their own engagement, reviewers on an editor's engagement).
3. **Run:** 30 s ramp, then 120 s steady. Each user repeats a random scenario: dashboard list (25 %), open an engagement (4 reads, 30 %), save a
   section on their own engagement (editors, 35 %), upload a small file (editors, 10 %), review screen (reviewers). Ten users additionally save one
   shared section using stale versions on purpose, to measure the 409 rate.
4. **Probe:** every successful save is timed until its `SectionUpdated` event reaches the open streams (delivery latency).
5. **Drain:** after the run, waits for the outbox tables to empty (uses `docker exec`; skipped with `-NoDocker`).

Users pause between actions. The profile is set with `--think min-max` (milliseconds): the default `2000-5000` is a plausible pace for people filling in
forms; `400-1200` is a **stress** profile (about 115 requests per second from 100 users), harder than 100 real users.

## Targets (stated before the first run)

| Target | Value |
|---|---|
| Simple reads (dashboard, open engagement, tabs, tab, validation) p95 | < 300 ms |
| Writes (save section, upload) p95 | < 800 ms |
| Save to SSE event delivery p95 | < 2 s |
| 5xx responses | 0 |
| All 100 SSE streams open, none dropped | yes |
| Outbox empty after the run | within 10 s |

The review screen is not a simple read: it composes the engagement header, five tabs and the validation summary behind one response. It is reported
on its own line and is not part of the "simple reads" target.

## First full run (before tuning), stress profile

100 users, 100 streams, `--think 400-1200` (the then-default), 150 s, observability dashboard running (exporting every trace, metric and log).

| Operation | n | p50 ms | p95 ms | p99 ms |
|---|---|---|---|---|
| dashboard list | 2140 | 170 | 557 | 939 |
| open: engagement | 2612 | 111 | 328 | 654 |
| open: tabs | 2612 | 254 | 674 | 1186 |
| open: tab 1 | 2610 | 232 | 618 | 1061 |
| open: validation | 2612 | 231 | 619 | 984 |
| review screen | 1052 | 811 | 2021 | 2696 |
| save section | 2189 | 963 | 2033 | 2693 |
| upload | 668 | 257 | 1576 | 2693 |
| contention: save | 560 | 1640 | 3973 | 5082 |

Throughput 116 req/s, **0 server errors (5xx)**, 462 of 560 contended saves returned 409 (83 %, as designed), 0 streams dropped, 0 resyncs.

Missed targets:

| Target | Result |
|---|---|
| Simple reads p95 < 300 ms | 842 ms |
| Writes p95 < 800 ms | 1978 ms |
| SSE delivery p95 < 2 s | **95.9 s** (p50 84.8 s) |
| 100 streams open | 91 opened, 9 failed after a long stall (see below) |
| Outbox drained within 10 s | **1418 rows pending right after the run, draining at about 25 rows/s** |

## What the first run showed

- **The outbox dispatcher was the bottleneck, not the request path.** It published one message per broker round trip, which capped it near 25
  messages per second. The stress profile produced events faster than that, so a backlog built up and every event (and every status change that
  depends on one) arrived tens of seconds late, even though each HTTP request was answered quickly. Delivery latency was the backlog.
- **CPU was saturated, not SQL or any single query.** SQL Server container ~300 %, workflow-service ~150 %, engagement-service ~90 %, and the
  observability dashboard ~60 % of a core. The hottest SQL statements cost 0.2 to 0.7 ms each (`SectionFields`, `WorkflowSections`,
  `ValidationSummaries`, the dashboard list at about 7 ms); the Service Bus emulator's own inserts are in the same SQL Server. No missing index was
  found, so none was added.
- **Expected outcomes were expensive to log.** The framework logs an Error with a stack trace for every exception, including the ones turned into
  403/404/409 responses, and four Information lines per outgoing HttpClient call. With hundreds of 409s that is real CPU and drowns real errors.
- **Nine streams failed to open** during setup while 71 engagements were still settling. A 100-stream open on an idle stack takes 0.7 s, so this was
  load-related; the script now records the reason for each failed open, times each open and retries once (a real browser reconnects
  automatically).

## What was changed because of it

| Change | Where |
|---|---|
| Outbox publishes each poll's rows (up to 50) as **one broker batch**, in order, all-or-nothing; on failure the head row carries the attempt count and backoff, so ordering is preserved | `OutboxDispatcher`, `IEventBus.PublishBatchAsync`, `ServiceBusEventBus` |
| Stop logging expected outcomes as errors and per-call HttpClient chatter | `TelemetryExtensions` log filters |
| Realistic default pacing; the old pacing remains available as a stress profile | `load.mjs --think` |
| Failed SSE opens are explained and retried once | `load.mjs` |
| Outbox drain measured with one query via `docker exec` (was four slow `compose exec` calls) | `load.mjs` |

## After tuning

**Not measured yet.** The Docker engine stopped while the images with these changes were being rebuilt (seven parallel .NET publishes exceeded the VM's
memory), so the post-tuning numbers could not be taken in this session. The batching change is covered by integration tests (one broker round trip
per poll, order kept, head-of-line backoff) but its effect on delivery latency is **unverified**.

To fill this section: restart Docker Desktop, then

```powershell
$env:COMPOSE_PARALLEL_LIMIT = 2          # build two images at a time on an 8 GB VM
./infra/local/up.ps1                     # without -Observability for a clean baseline
./infra/local/load-test.ps1              # realistic pacing; add -Steady 60 for a shorter run
./infra/local/load-test.ps1 -Users 100 -Ramp 30 -Steady 120   # same, explicit
node tools/load-test/load.mjs --think 400-1200                 # the stress profile of the first run
```

and paste the printed table, throughput, contention rate, SSE latency and target lines here.

## CI

The compose smoke job runs `load.mjs --smoke --targets-scale 3` (20 users, about 30 s, targets three times looser for shared runners) after the permission
audit. It catches errors, dropped streams and a stuck outbox, not capacity.
