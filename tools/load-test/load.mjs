#!/usr/bin/env node
// ~100-user load check against the running stack (through the gateway). Dependency-free (Node 20+).
//
//   node tools/load-test/load.mjs [--users 100] [--ramp 30] [--steady 120] [--contention 10] [--base http://localhost:5000]
//                                 [--think 2000-5000] [--targets-scale 1] [--smoke] [--no-docker]
//
//   --think min-max   pause between a user's actions in ms. Default 2000-5000, a plausible pace for people working in a form;
//                     --think 400-1200 is a stress profile (about 100 req/s from 100 users), not a realistic one.
//
// What it does
//   1. setup:   creates one throwaway engagement per virtual user (VU) and waits until workflow-service has provisioned it
//   2. streams: every VU holds an SSE stream (editors on their own engagement, reviewers on an editor's engagement)
//   3. run:     ramp, then steady state. Each iteration is a random scenario: dashboard list, open engagement (4 reads), save a
//               section on the VU's own engagement, upload a small file, review screen (reviewers). A contention group
//               deliberately saves the same section with stale versions to measure the 409 rate.
//   4. probe:   every successful save is timed until its SectionUpdated event reaches the open streams (delivery latency)
//   5. drain:   waits for the outbox tables to empty (needs Docker; skipped with --no-docker)
// Exits 1 when a target is missed. Numbers are from whatever machine runs this; they guard against regressions, not capacity.

import { execFile } from 'node:child_process'
import { promisify } from 'node:util'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const run = promisify(execFile)
const args = process.argv.slice(2)
const flag = (name) => args.includes(`--${name}`)
const option = (name, fallback) => {
  const i = args.indexOf(`--${name}`)
  return i >= 0 && args[i + 1] && !args[i + 1].startsWith('--') ? args[i + 1] : fallback
}

const smoke = flag('smoke')
const USERS = Number(option('users', smoke ? 20 : 100))
const RAMP_S = Number(option('ramp', smoke ? 5 : 30))
const STEADY_S = Number(option('steady', smoke ? 20 : 120))
const CONTENTION = Math.min(Number(option('contention', smoke ? 4 : 10)), USERS)
const SCALE = Number(option('targets-scale', 1))
const [THINK_MIN, THINK_MAX] = option('think', smoke ? '1000-2500' : '2000-5000').split('-').map(Number)
const BASE = option('base', 'http://localhost:5000').replace(/\/$/, '')
const USE_DOCKER = !flag('no-docker')
const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..', '..')
const PASSWORD = 'Passw0rd!'

// Targets (stated up front). Scale them for slower machines such as CI runners.
const TARGETS = {
  readP95: 300 * SCALE,
  writeP95: 800 * SCALE,
  sseP95: 2000 * SCALE,
  max5xx: 0,
  outboxDrainSeconds: 10,
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const rand = (min, max) => min + Math.random() * (max - min)
const log = (text) => console.log(text)

// ---- HTTP with timing -------------------------------------------------------------------------------------------

const samples = new Map() // operation -> { ms: number[], errors: number, status: Map<number, number> }

function stat(op) {
  if (!samples.has(op)) samples.set(op, { ms: [], errors: 0, status: new Map() })
  return samples.get(op)
}

async function timed(op, method, path, { token, body, form, ifMatch, expect = [200, 201, 202] } = {}) {
  const init = { method, headers: { Accept: 'application/json', 'X-Correlation-Id': `load-${crypto.randomUUID()}` } }
  if (token) init.headers.Authorization = `Bearer ${token}`
  if (ifMatch) init.headers['If-Match'] = `"${ifMatch}"`
  if (form) init.body = form
  else if (body !== undefined) {
    init.headers['Content-Type'] = 'application/json'
    init.body = JSON.stringify(body)
  }
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), 20000)
  init.signal = controller.signal
  const started = performance.now()
  let status = 0
  let json = null
  try {
    const response = await fetch(BASE + path, init)
    status = response.status
    const text = await response.text()
    try { json = text ? JSON.parse(text) : null } catch { /* not JSON */ }
  } catch {
    status = 0 // timeout or connection failure
  } finally {
    clearTimeout(timer)
  }
  const ms = performance.now() - started
  const s = stat(op)
  s.status.set(status, (s.status.get(status) ?? 0) + 1)
  if (expect.includes(status)) s.ms.push(ms)
  else s.errors++
  return { status, json, ms, at: performance.now() }
}

const percentile = (sorted, p) => (sorted.length === 0 ? NaN : sorted[Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1)])

// ---- Setup ------------------------------------------------------------------------------------------------------

const tokens = {}
const userIds = {}
async function login(name) {
  const r = await timed('login', 'POST', '/auth/login', { body: { email: `${name}@auditflow.test`, password: PASSWORD } })
  if (r.status !== 200) throw new Error(`Login as ${name} failed (${r.status}). Is the stack up?`)
  tokens[name] = r.json.accessToken
  userIds[name] = r.json.user.userId
}

const tinyFile = () => {
  const form = new FormData()
  form.append('file', new Blob(['load test file '.repeat(64)], { type: 'text/plain' }), 'load.txt')
  return form
}

async function createEngagement(owner, label, participants = []) {
  const options = (await timed('setup', 'GET', '/engagements/creation-options', { token: tokens[owner] })).json
  const created = await timed('setup', 'POST', '/engagements', {
    token: tokens[owner],
    body: {
      name: `Load test ${label}`, periodType: options.periodTypes[0], region: options.regions[0], reviewerUserId: userIds.rachel,
      participantUserIds: participants, configuration: { configTypes: [options.configTypes[0]], options: {} },
    },
  })
  if (created.status !== 201) throw new Error(`Could not create engagement ${label}: ${created.status}`)
  return created.json.engagementId
}

async function findField(id, owner) {
  for (let tabNo = 1; tabNo <= 5; tabNo++) {
    const tab = (await timed('setup', 'GET', `/engagements/${id}/tabs/${tabNo}`, { token: tokens[owner] })).json
    for (const section of tab?.sections ?? []) {
      const field = section.fields.find((f) => (f.type === 'text' || f.type === 'documentation') && f.editable)
      if (field) return { tabNo, sectionKey: section.sectionKey, fieldKey: field.key, version: section.version }
    }
  }
  throw new Error(`No editable field on ${id}`)
}

async function mapLimit(items, limit, fn) {
  const results = new Array(items.length)
  let next = 0
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (next < items.length) {
      const i = next++
      results[i] = await fn(items[i], i)
    }
  }))
  return results
}

// ---- SSE streams ------------------------------------------------------------------------------------------------

const sse = { open: 0, failed: 0, events: 0, resyncs: 0, closed: 0, openMs: [], failures: new Map(), retried: 0 }
const pendingSaves = new Map() // `${engagementId}:${version}` -> response time (performance.now())
const deliveryMs = []
let stopping = false

async function openStream(user, engagementId) {
  const controller = new AbortController()
  let response
  for (let attempt = 1; attempt <= 2; attempt++) {
    const started = performance.now()
    const timer = setTimeout(() => controller.abort(), 15000)
    let reason = null
    try {
      response = await fetch(`${BASE}/engagements/${engagementId}/events/stream`, {
        headers: { Authorization: `Bearer ${tokens[user]}`, Accept: 'text/event-stream' },
        signal: controller.signal,
      })
      if (response.status === 200) { sse.openMs.push(performance.now() - started); clearTimeout(timer); break }
      reason = `HTTP ${response.status}`
      await response.body?.cancel()
    } catch (error) {
      reason = error.name === 'AbortError' ? 'no response headers within 15 s' : (error.cause?.code ?? error.message)
    }
    clearTimeout(timer)
    response = undefined
    sse.failures.set(reason, (sse.failures.get(reason) ?? 0) + 1)
    if (attempt === 1) { sse.retried++; await sleep(500) }
  }
  if (!response) {
    sse.failed++
    return () => {}
  }
  sse.open++

  ;(async () => {
    const decoder = new TextDecoder()
    let buffer = ''
    try {
      for await (const chunk of response.body) {
        buffer += decoder.decode(chunk, { stream: true })
        let end
        while ((end = buffer.indexOf('\n\n')) >= 0) {
          const frame = buffer.slice(0, end)
          buffer = buffer.slice(end + 2)
          const event = /^event: (.+)$/m.exec(frame)?.[1]
          const data = /^data: (.+)$/m.exec(frame)?.[1]
          if (event === 'resync') sse.resyncs++
          if (!event || !data || event === 'ready' || event === 'resync' || event === 'reauth') continue
          sse.events++
          if (event === 'SectionUpdated') {
            const payload = JSON.parse(data).payload
            const sent = pendingSaves.get(`${engagementId}:${payload.version}`)
            if (sent !== undefined) deliveryMs.push(Math.max(0, performance.now() - sent))
          }
        }
      }
    } catch {
      /* aborted at the end of the run, or the connection dropped */
    }
    if (!stopping) sse.closed++
  })()

  return () => controller.abort()
}

// ---- Virtual users ----------------------------------------------------------------------------------------------

const counters = { iterations: 0, saves: 0, conflicts409: 0 }

async function virtualUser(vu, deadline, startDelay) {
  await sleep(startDelay)
  const { token, engagementId, field, isEditor, reviewEngagementId } = vu
  let version = field?.version
  while (performance.now() < deadline) {
    counters.iterations++
    const roll = Math.random()

    if (roll < 0.25) {
      await timed('dashboard list', 'GET', '/engagements?scope=participating&pageSize=20', { token })
    } else if (roll < 0.55) {
      await timed('open: engagement', 'GET', `/engagements/${engagementId}`, { token })
      await timed('open: tabs', 'GET', `/engagements/${engagementId}/tabs`, { token })
      await timed('open: tab 1', 'GET', `/engagements/${engagementId}/tabs/${field?.tabNo ?? 1}`, { token })
      await timed('open: validation', 'GET', `/engagements/${engagementId}/validation-summary`, { token })
    } else if (isEditor && roll < 0.9) {
      const r = await timed('save section', 'PATCH', `/engagements/${engagementId}/sections/${field.sectionKey}`, {
        token, body: { fields: { [field.fieldKey]: `load ${Date.now()}` } }, expect: [200], ifMatch: version,
      })
      if (r.status === 200) {
        counters.saves++
        version = r.json.section.version
        pendingSaves.set(`${engagementId}:${version}`, r.at)
      } else {
        version = (await timed('open: tab 1', 'GET', `/engagements/${engagementId}/tabs/${field.tabNo}`, { token })).json?.sections
          ?.find((s) => s.sectionKey === field.sectionKey)?.version ?? version
      }
    } else if (isEditor) {
      await timed('upload', 'POST', `/engagements/${engagementId}/attachments`, { token, form: tinyFile(), expect: [201] })
    } else {
      await timed('review screen', 'GET', `/reviews/${reviewEngagementId}`, { token })
    }
    await sleep(rand(THINK_MIN, THINK_MAX))
  }
}

/** Saves one shared section with whatever version it last read, so concurrent writers collide. 409 is the expected outcome. */
async function contentionUser(token, shared, deadline, startDelay) {
  await sleep(startDelay)
  while (performance.now() < deadline) {
    const tab = (await timed('contention: read', 'GET', `/engagements/${shared.engagementId}/tabs/${shared.field.tabNo}`, { token })).json
    const version = tab?.sections?.find((s) => s.sectionKey === shared.field.sectionKey)?.version
    await sleep(rand(50, 250)) // widen the window in which someone else saves first
    const r = await timed('contention: save', 'PATCH', `/engagements/${shared.engagementId}/sections/${shared.field.sectionKey}`, {
      token, body: { fields: { [shared.field.fieldKey]: `contend ${Date.now()}` } }, expect: [200, 409], ifMatch: version,
    })
    if (r.status === 409) counters.conflicts409++
    await sleep(rand(100, 400))
  }
}

// ---- Infrastructure probes (Docker) -----------------------------------------------------------------------------

/** Unpublished outbox rows across the four publishing databases, in one round trip. */
async function outboxPending() {
  const password = process.env.MSSQL_SA_PASSWORD ?? 'AuditFlow_Dev#2026'
  const count = (db) => `(SELECT COUNT(*) FROM AuditFlow_${db}.dbo.OutboxMessage WHERE PublishedAt IS NULL)`
  const query = `SET NOCOUNT ON; SELECT ${['Engagement', 'Workflow', 'Attachment', 'Review'].map(count).join(' + ')}`
  const { stdout } = await run('docker', [
    'exec', option('sql-container', 'auditflow-sqlserver-1'), '/opt/mssql-tools18/bin/sqlcmd', '-C', '-S', 'localhost', '-U', 'sa', '-P', password,
    '-h', '-1', '-W', '-Q', query,
  ], { cwd: ROOT })
  return Number(stdout.trim().split(/s+/)[0])
}

async function containerStats() {
  const { stdout } = await run('docker', ['stats', '--no-stream', '--format', '{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}'], { cwd: ROOT })
  return stdout.trim().split('\n').filter((l) => l.startsWith('auditflow-')).map((l) => {
    const [name, cpu, mem] = l.split('|')
    return { name: name.replace(/^auditflow-|-1$/g, ''), cpu, mem: mem.split('/')[0].trim() }
  })
}

// ---- Main -------------------------------------------------------------------------------------------------------

log(`Load test: ${USERS} VUs, ramp ${RAMP_S}s, steady ${STEADY_S}s, think ${THINK_MIN}-${THINK_MAX} ms, ${CONTENTION} contention VUs, target ${BASE}${smoke ? ' (smoke)' : ''}`)
const health = await timed('health', 'GET', '/health/auth')
if (health.status !== 200) {
  console.error(`The gateway at ${BASE} is not reachable. Start the stack: ./infra/local/up.ps1`)
  process.exit(1)
}

for (const name of ['alice', 'bob', 'rachel']) await login(name)

const editorsCount = Math.max(1, Math.round(USERS * 0.7))
log(`Setup: creating ${editorsCount} engagements (+1 shared for contention)...`)
const setupStarted = performance.now()
const owners = Array.from({ length: editorsCount }, (_, i) => (i % 2 === 0 ? 'alice' : 'bob'))
const engagementIds = await mapLimit(owners, 8, (owner, i) => createEngagement(owner, `vu-${i}`))
const sharedId = await createEngagement('alice', 'contention', [userIds.bob])
const fields = await mapLimit([...engagementIds, sharedId], 8, async (id, i) => {
  const owner = i < editorsCount ? owners[i] : 'alice'
  for (let attempt = 0; attempt < 60; attempt++) {
    const ready = (await timed('setup', 'GET', `/engagements/${id}/tabs`, { token: tokens[owner] })).json?.ready
    if (ready) return findField(id, owner)
    await sleep(500)
  }
  throw new Error(`${id} was never provisioned`)
})
const shared = { engagementId: sharedId, field: fields[editorsCount] }
log(`Setup done in ${((performance.now() - setupStarted) / 1000).toFixed(1)} s`)

const vus = Array.from({ length: USERS }, (_, i) => {
  const isEditor = i < editorsCount
  const engagementId = isEditor ? engagementIds[i] : engagementIds[i % editorsCount]
  return {
    isEditor,
    token: tokens[isEditor ? owners[i] : 'rachel'],
    engagementId,
    reviewEngagementId: engagementId,
    field: isEditor ? fields[i] : fields[i % editorsCount],
    streamUser: isEditor ? owners[i] : 'rachel',
  }
})

log(`Opening ${USERS} SSE streams...`)
const closers = await Promise.all(vus.map((vu) => openStream(vu.streamUser, vu.engagementId)))
log(`  ${sse.open} open, ${sse.failed} failed (${sse.retried} needed a retry; slowest open ${Math.round(Math.max(0, ...sse.openMs))} ms)`)
for (const [reason, count] of sse.failures) log(`    open attempt failed ${count}x: ${reason}`)

const startedAt = performance.now()
const deadline = startedAt + (RAMP_S + STEADY_S) * 1000
log(`Running for ${RAMP_S + STEADY_S} s...`)
const midpoint = startedAt + (RAMP_S + STEADY_S / 2) * 1000
let statsMid = null
const statsTimer = USE_DOCKER ? setTimeout(async () => { statsMid = await containerStats().catch(() => null) }, Math.max(0, midpoint - performance.now())) : null

const contentionTokens = ['alice', 'bob']
await Promise.all([
  ...vus.map((vu, i) => virtualUser(vu, deadline, (i / USERS) * RAMP_S * 1000)),
  ...Array.from({ length: CONTENTION }, (_, i) => contentionUser(tokens[contentionTokens[i % 2]], shared, deadline, (i / CONTENTION) * RAMP_S * 1000)),
])
clearTimeout(statsTimer)
const ranFor = (performance.now() - startedAt) / 1000

// Drain: the outbox should empty and the last events should arrive.
let drainSeconds = null
const backlog = []
if (USE_DOCKER) {
  const drainStarted = performance.now()
  try {
    while (performance.now() - drainStarted < 60000) {
      const pending = await outboxPending()
      backlog.push(`${((performance.now() - drainStarted) / 1000).toFixed(0)}s:${pending}`)
      if (pending === 0) { drainSeconds = (performance.now() - drainStarted) / 1000; break }
      await sleep(500)
    }
  } catch (error) {
    log(`(outbox check skipped: ${error.message.split('\n')[0]})`)
  }
}
await sleep(2500) // let in-flight events reach the streams
stopping = true
for (const close of closers) close()

// ---- Report -----------------------------------------------------------------------------------------------------

// Simple reads only. The review screen fans out to several services and the contention reads queue behind a hot row: both are reported, neither is in the p95 target.
const READ_OPS = ['dashboard list', 'open: engagement', 'open: tabs', 'open: tab 1', 'open: validation']
const WRITE_OPS = ['save section', 'upload']
const rows = [...samples.entries()].filter(([op]) => !['setup', 'login', 'health'].includes(op)).sort(([a], [b]) => a.localeCompare(b))
const fmt = (n) => (Number.isNaN(n) ? '-' : n.toFixed(0))

log('\nOperation                 n   errors    p50    p95    p99    max   (ms)')
let readSorted = []
let writeSorted = []
let total5xx = 0
for (const [op, s] of rows) {
  const sorted = [...s.ms].sort((a, b) => a - b)
  if (READ_OPS.includes(op)) readSorted = readSorted.concat(sorted)
  if (WRITE_OPS.includes(op)) writeSorted = writeSorted.concat(sorted)
  for (const [status, count] of s.status) if (status >= 500) total5xx += count
  log(`${op.padEnd(22)} ${String(sorted.length).padStart(5)} ${String(s.errors).padStart(8)} ${fmt(percentile(sorted, 50)).padStart(6)} ${fmt(percentile(sorted, 95)).padStart(6)} ${fmt(percentile(sorted, 99)).padStart(6)} ${fmt(sorted.at(-1) ?? NaN).padStart(6)}`)
}
readSorted.sort((a, b) => a - b)
writeSorted.sort((a, b) => a - b)
deliveryMs.sort((a, b) => a - b)

const contentionStat = samples.get('contention: save')
const contended = (contentionStat?.status.get(200) ?? 0) + (contentionStat?.status.get(409) ?? 0)
log(`\nThroughput: ${(rows.reduce((n, [, s]) => n + s.ms.length + s.errors, 0) / ranFor).toFixed(1)} req/s over ${ranFor.toFixed(0)} s, ${counters.iterations} iterations, ${counters.saves} saves`)
log(`Contention: ${counters.conflicts409} of ${contended} contended saves returned 409 (${contended ? ((100 * counters.conflicts409) / contended).toFixed(0) : 0}%)`)
log(`SSE: ${sse.open} streams, ${sse.events} events received, ${deliveryMs.length} save->event deliveries timed, ${sse.resyncs} resync, ${sse.closed} dropped`)
log(`SSE delivery latency: p50 ${fmt(percentile(deliveryMs, 50))} ms, p95 ${fmt(percentile(deliveryMs, 95))} ms, p99 ${fmt(percentile(deliveryMs, 99))} ms, max ${fmt(deliveryMs.at(-1) ?? NaN)} ms`)
if (backlog.length > 0) log(`Outbox backlog after the run (seconds:pending): ${backlog.join(' ')}`)
if (statsMid) log(`Containers (mid-run): ${statsMid.map((c) => `${c.name} ${c.cpu} ${c.mem}`).join(' | ')}`)

const checks = [
  ['reads p95', percentile(readSorted, 95), `< ${TARGETS.readP95} ms`, percentile(readSorted, 95) < TARGETS.readP95],
  ['writes p95', percentile(writeSorted, 95), `< ${TARGETS.writeP95} ms`, percentile(writeSorted, 95) < TARGETS.writeP95],
  ['SSE delivery p95', percentile(deliveryMs, 95), `< ${TARGETS.sseP95} ms`, percentile(deliveryMs, 95) < TARGETS.sseP95],
  ['5xx responses', total5xx, `= ${TARGETS.max5xx}`, total5xx <= TARGETS.max5xx],
  ['SSE streams opened', sse.open, `= ${USERS}`, sse.open === USERS && sse.failed === 0],
  ['SSE streams dropped', sse.closed, '= 0', sse.closed === 0],
]
if (USE_DOCKER) checks.push(['outbox drained after', drainSeconds ?? NaN, `<= ${TARGETS.outboxDrainSeconds} s`, drainSeconds !== null && drainSeconds <= TARGETS.outboxDrainSeconds])

log('\nTargets')
let ok = true
for (const [name, value, target, pass] of checks) {
  ok &&= pass
  const shown = typeof value === 'number' && !Number.isNaN(value) ? (Number.isInteger(value) ? String(value) : value.toFixed(0)) : 'n/a'
  log(`  ${pass ? 'PASS' : 'FAIL'}  ${name.padEnd(22)} ${shown.padStart(7)}   (target ${target})`)
}
log(ok ? '\nAll targets met.' : '\nTargets missed.')
process.exit(ok ? 0 : 1)
