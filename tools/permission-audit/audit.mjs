#!/usr/bin/env node
// Black-box permission audit. Logs in as the demo users, drives throwaway engagements through every lifecycle status via the
// real API (through the gateway), and checks every role x status x operation against matrix.json.
// No dependencies (Node 20+). Exit code 1 on any mismatch or if the stack cannot be driven.
//
//   node tools/permission-audit/audit.mjs [--base http://localhost:5000] [--matrix path/to/matrix.json] [--quiet]

import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

const args = process.argv.slice(2)
const option = (name, fallback) => {
  const i = args.indexOf(`--${name}`)
  return i >= 0 && args[i + 1] && !args[i + 1].startsWith('--') ? args[i + 1] : fallback
}
const BASE = option('base', 'http://localhost:5000').replace(/\/$/, '')
const QUIET = args.includes('--quiet')
const PASSWORD = 'Passw0rd!'
const matrix = JSON.parse(readFileSync(option('matrix', join(dirname(fileURLToPath(import.meta.url)), 'matrix.json')), 'utf8'))

const color = (code, text) => (process.stdout.isTTY ? `\x1b[${code}m${text}\x1b[0m` : text)
const green = (t) => color(32, t)
const red = (t) => color(31, t)
const dim = (t) => color(90, t)

// ---- HTTP -------------------------------------------------------------------------------------------------------

const tokens = {}

async function http(method, path, { token, body, headers = {}, form, abortAfterHeaders = false } = {}) {
  const init = { method, headers: { Accept: 'application/json', 'X-Correlation-Id': `audit-${crypto.randomUUID()}`, ...headers } }
  if (token) init.headers.Authorization = `Bearer ${token}`
  if (form) init.body = form
  else if (body !== undefined) {
    init.headers['Content-Type'] = 'application/json'
    init.body = JSON.stringify(body)
  }
  const controller = new AbortController()
  init.signal = controller.signal
  const timer = setTimeout(() => controller.abort(), 20000)
  try {
    const response = await fetch(BASE + path, init)
    if (abortAfterHeaders) {
      controller.abort() // a stream never ends; the status line is all the audit needs
      return { status: response.status, headers: response.headers, json: null }
    }
    const text = await response.text()
    let json = null
    try { json = text ? JSON.parse(text) : null } catch { /* not JSON */ }
    return { status: response.status, headers: response.headers, json }
  } catch (error) {
    if (error.name === 'AbortError' && abortAfterHeaders) return { status: 0, headers: new Headers(), json: null }
    throw error
  } finally {
    clearTimeout(timer)
  }
}

async function login(name) {
  const { email } = matrix.users[name]
  const response = await http('POST', '/auth/login', { body: { email, password: PASSWORD } })
  if (response.status !== 200) throw new Error(`Login as ${name} failed (${response.status}). Is the stack up and seeded?`)
  tokens[name] = response.json.accessToken
  matrix.users[name].userId = response.json.user.userId
}

// ---- Results ----------------------------------------------------------------------------------------------------

const results = []
function record(scope, op, actor, expected, actual, note = '') {
  const ok = expected.includes(actual)
  results.push({ scope, op, actor, expected, actual, ok, note })
  if (!ok && !QUIET) console.log(red(`  MISMATCH ${scope} ${op} as ${actor}: expected ${expected.join('|')}, got ${actual} ${note}`))
}

function expectedFor(op, engagementRole, status) {
  const rule = matrix.expect[op][engagementRole]
  if (!rule) throw new Error(`Matrix has no rule for ${op}/${engagementRole}`)
  return Array.isArray(rule) ? rule : rule[status]
}

// ---- Engagement driver ------------------------------------------------------------------------------------------

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

async function waitFor(description, probe, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    const value = await probe()
    if (value) return value
    await sleep(500)
  }
  throw new Error(`Timed out waiting for ${description}`)
}

async function createEngagement(owner, label) {
  const options = (await http('GET', '/engagements/creation-options', { token: tokens[owner] })).json
  const request = {
    name: `Permission audit ${label} ${new Date().toISOString()}`,
    periodType: options.periodTypes[0],
    region: options.regions[0],
    reviewerUserId: matrix.users.rachel.userId,
    participantUserIds: ['alice', 'bob', 'victor'].filter((n) => n !== owner).map((n) => matrix.users[n].userId),
    configuration: { configTypes: [options.configTypes[0]], options: {} },
  }
  const created = await http('POST', '/engagements', { token: tokens[owner], body: request })
  if (created.status !== 201) throw new Error(`Could not create an engagement as ${owner}: ${created.status} ${JSON.stringify(created.json)}`)
  const id = created.json.engagementId
  await waitFor(`workflow setup of ${id}`, async () => (await http('GET', `/engagements/${id}/tabs`, { token: tokens[owner] })).json?.ready)
  return id
}

const statusOf = async (id, user) => (await http('GET', `/engagements/${id}`, { token: tokens[user] })).json?.status
const waitStatus = (id, user, status) => waitFor(`${id} to become ${status}`, async () => (await statusOf(id, user)) === status)

/** First editable text-like field, found as the editor so its key/version can be used by every actor. */
async function findEditableField(id, editor) {
  for (let tabNo = 1; tabNo <= 5; tabNo++) {
    const tab = (await http('GET', `/engagements/${id}/tabs/${tabNo}`, { token: tokens[editor] })).json
    for (const section of tab?.sections ?? []) {
      const field = section.fields.find((f) => (f.type === 'text' || f.type === 'documentation') && f.editable)
      if (field) return { sectionKey: section.sectionKey, fieldKey: field.key }
    }
  }
  throw new Error(`No editable text field found on ${id}`)
}

async function sectionVersion(id, editor, sectionKey) {
  for (let tabNo = 1; tabNo <= 5; tabNo++) {
    const tab = (await http('GET', `/engagements/${id}/tabs/${tabNo}`, { token: tokens[editor] })).json
    const section = tab?.sections?.find((s) => s.sectionKey === sectionKey)
    if (section) return section.version
  }
  throw new Error(`Section ${sectionKey} not found`)
}

const tinyFile = () => {
  const form = new FormData()
  form.append('file', new Blob(['permission audit'], { type: 'text/plain' }), 'audit.txt')
  return form
}

/** Fills whatever the backend reports as blocking, until it says the engagement is ready to submit. */
async function makeReady(id, editor) {
  const valueFor = (field) =>
    field.type === 'checkbox' ? true : field.type === 'number' ? '100' : field.type === 'date' ? '2026-01-15' : 'permission audit'

  for (let round = 1; round <= 20; round++) {
    const summary = (await http('GET', `/engagements/${id}/validation-summary`, { token: tokens[editor] })).json
    if (summary.readyToSubmit) return round
    const blocking = summary.issues.filter((i) => i.isBlocking)
    if (blocking.length === 0) { await sleep(1000); continue }

    let progressed = false
    for (const issue of blocking) {
      if (issue.fieldKey && issue.sectionKey && issue.tabNo) {
        const tab = (await http('GET', `/engagements/${id}/tabs/${issue.tabNo}`, { token: tokens[editor] })).json
        const section = tab.sections.find((s) => s.sectionKey === issue.sectionKey)
        const field = section?.fields.find((f) => f.key === issue.fieldKey)
        if (!field || !field.editable) continue
        const saved = await http('PATCH', `/engagements/${id}/sections/${section.sectionKey}`, {
          token: tokens[editor], headers: { 'If-Match': `"${section.version}"` }, body: { fields: { [field.key]: valueFor(field) } },
        })
        progressed ||= saved.status === 200
      } else if (issue.sectionKey) {
        progressed ||= (await http('POST', `/engagements/${id}/sections/${issue.sectionKey}/attachments`, { token: tokens[editor], form: tinyFile() })).status === 201
      } else if (issue.tabNo) {
        progressed ||= (await http('POST', `/engagements/${id}/tabs/${issue.tabNo}/attachments`, { token: tokens[editor], form: tinyFile() })).status === 201
      }
    }
    if (!progressed) break
  }
  const final = (await http('GET', `/engagements/${id}/validation-summary`, { token: tokens[editor] })).json
  throw new Error(`${id} did not become ready to submit. Remaining: ${final.issues.filter((i) => i.isBlocking).map((i) => i.message).join(' | ')}`)
}

// ---- One lifecycle ----------------------------------------------------------------------------------------------

async function runLifecycle(primary, secondary) {
  const scope = (status) => `${primary}/${status}`
  const id = await createEngagement(primary, primary)
  console.log(`\n== Engagement ${id} (owner ${primary}, co-editor ${secondary}) ==`)
  const roleOf = { [primary]: 'editor', [secondary]: 'editor', rachel: 'reviewer', victor: 'viewer', rohan: 'outsider' }
  const actors = [primary, secondary, 'rachel', 'victor', 'rohan']
  const target = await findEditableField(id, primary)
  let counter = 0

  // Each operation returns the HTTP status for an actor. Writes that need a version fetch it fresh (versions are not secret).
  const run = {
    read: (u) => http('GET', `/engagements/${id}`, { token: tokens[u] }),
    participants: (u) => http('GET', `/engagements/${id}/participants`, { token: tokens[u] }),
    tabs: (u) => http('GET', `/engagements/${id}/tabs`, { token: tokens[u] }),
    tab: (u) => http('GET', `/engagements/${id}/tabs/1`, { token: tokens[u] }),
    validation: (u) => http('GET', `/engagements/${id}/validation-summary`, { token: tokens[u] }),
    attachments: (u) => http('GET', `/engagements/${id}/attachments`, { token: tokens[u] }),
    stream: (u) => http('GET', `/engagements/${id}/events/stream`, { token: tokens[u], abortAfterHeaders: true }),
    review: (u) => http('GET', `/reviews/${id}`, { token: tokens[u] }),
    editDetails: async (u) => {
      const current = (await http('GET', `/engagements/${id}`, { token: tokens[primary] })).json
      return http('PATCH', `/engagements/${id}`, {
        token: tokens[u], headers: { 'If-Match': `"${current.version}"` }, body: { name: `${current.name.replace(/ #\d+$/, '')} #${++counter}` },
      })
    },
    edit: async (u) => {
      const version = await sectionVersion(id, primary, target.sectionKey)
      return http('PATCH', `/engagements/${id}/sections/${target.sectionKey}`, {
        token: tokens[u], headers: { 'If-Match': `"${version}"` }, body: { fields: { [target.fieldKey]: `audit edit ${++counter}` } },
      })
    },
    upload: (u) => http('POST', `/engagements/${id}/attachments`, { token: tokens[u], form: tinyFile() }),
    submit: (u) => http('POST', `/engagements/${id}/submit`, { token: tokens[u] }),
    comment: (u) => http('POST', `/reviews/${id}/comments`, { token: tokens[u], body: { tabNo: 1, body: `audit comment ${++counter}` } }),
    rejectNoComment: (u) => http('POST', `/reviews/${id}/reject`, { token: tokens[u], body: { comment: '' } }),
    reject: (u) => http('POST', `/reviews/${id}/reject`, { token: tokens[u], body: { comment: `audit rejection ${++counter}` } }),
    approve: (u) => http('POST', `/reviews/${id}/approve`, { token: tokens[u], body: { comment: 'audit approval' } }),
  }

  // Submit returns before review-service has seen EngagementSubmitted and the access caches have learned the new status
  // (both are event-driven). Wait for the reviewer's own screen to say a decision is possible before testing reviewer actions.
  const awaitDecidable = async () => {
    const started = Date.now()
    await waitFor('the reviewer to be able to decide', async () => (await http('GET', `/reviews/${id}`, { token: tokens.rachel })).json?.canDecide === true)
    const waited = Date.now() - started
    if (waited > 600 && !QUIET) console.log(dim(`   (reviewer could decide ${(waited / 1000).toFixed(1)} s after submit)`))
  }

  const check = async (status, op, actor) => {
    const response = await run[op](actor)
    record(scope(status), op, actor, expectedFor(op, roleOf[actor], status), response.status, response.status >= 400 && response.json?.detail ? dim(`(${response.json.detail})`) : '')
  }

  // Everything that cannot change state, and every mutation that must be refused, for every actor.
  const auditStatus = async (status, { skip = [] } = {}) => {
    const seen = await statusOf(id, primary)
    if (seen !== status) throw new Error(`Expected ${id} to be ${status} but it is ${seen}`)
    console.log(`-- ${status}`)
    for (const op of Object.keys(matrix.operations)) {
      if (skip.includes(op)) continue
      for (const actor of actors) {
        const allowedToChange = expectedFor(op, roleOf[actor], status).some((c) => c < 300) && matrix.operations[op].mutates
        if (!allowedToChange) await check(status, op, actor)
      }
    }
  }

  // 1. Draft
  await auditStatus('Draft', { skip: [] })
  for (const actor of [primary, secondary]) { await check('Draft', 'upload', actor); await check('Draft', 'editDetails', actor) }
  await check('Draft', 'edit', primary) // first edit starts the work: Draft -> In Progress
  await waitStatus(id, primary, 'InProgress')

  // 2. In Progress
  await auditStatus('InProgress')
  for (const actor of [primary, secondary]) { await check('InProgress', 'upload', actor); await check('InProgress', 'edit', actor) }
  await makeReady(id, primary)
  await check('InProgress', 'submit', primary)
  await waitStatus(id, primary, 'UnderReview')

  // 3. Under Review (first cycle): comment, reject-without-comment, reject
  await awaitDecidable()
  await auditStatus('UnderReview')
  await check('UnderReview', 'comment', 'rachel')
  await check('UnderReview', 'rejectNoComment', 'rachel')
  await check('UnderReview', 'reject', 'rachel')
  await waitStatus(id, primary, 'Rejected')

  // 4. Rejected: rework by an editor moves it back to In Progress
  await auditStatus('Rejected')
  await check('Rejected', 'upload', primary)
  await check('Rejected', 'edit', primary)
  await waitStatus(id, primary, 'InProgress')
  await makeReady(id, primary)
  await check('InProgress', 'submit', secondary)
  await waitStatus(id, primary, 'UnderReview')

  // 5. Under Review (second cycle): approve
  await awaitDecidable()
  await check('UnderReview', 'approve', 'rachel')
  await waitStatus(id, primary, 'Approved')

  // 6. Approved is final: every mutation is refused
  await auditStatus('Approved')
  return id
}

// ---- Checks that do not depend on status --------------------------------------------------------------------------

async function runGlobal(sampleId) {
  const g = matrix.global
  console.log('\n== Global checks ==')
  for (const [user, codes] of Object.entries(g.me)) {
    record('global', 'me', user, codes, (await http('GET', '/auth/me', { token: tokens[user] })).status)
    record('global', 'permissions', user, g.permissions[user], (await http('GET', '/auth/permissions', { token: tokens[user] })).status)
    record('global', 'list', user, g.list[user], (await http('GET', '/engagements', { token: tokens[user] })).status)
  }
  for (const [user, codes] of Object.entries(g.createEngagement)) {
    if (user === 'alice' || user === 'bob') continue // creation by editors is exercised by the lifecycles themselves
    const options = (await http('GET', '/engagements/creation-options', { token: tokens[user] })).json
    const body = {
      name: 'Permission audit forbidden create', periodType: options?.periodTypes?.[0] ?? 'Quarterly', region: options?.regions?.[0] ?? 'EMEA',
      reviewerUserId: matrix.users.rachel.userId, participantUserIds: [], configuration: { configTypes: [options?.configTypes?.[0] ?? 'Equities'], options: {} },
    }
    record('global', 'createEngagement', user, codes, (await http('POST', '/engagements', { token: tokens[user], body })).status)
  }

  const protectedRoutes = [
    ['GET', '/engagements'], ['GET', `/engagements/${sampleId}`], ['PATCH', `/engagements/${sampleId}`], ['POST', `/engagements/${sampleId}/submit`],
    ['GET', `/engagements/${sampleId}/tabs`], ['PATCH', `/engagements/${sampleId}/tabs/1`], ['GET', `/engagements/${sampleId}/attachments`],
    ['POST', `/engagements/${sampleId}/attachments`], ['GET', `/reviews/${sampleId}`], ['POST', `/reviews/${sampleId}/approve`],
    ['GET', `/engagements/${sampleId}/events/stream`], ['GET', '/auth/me'],
  ]
  for (const [method, path] of protectedRoutes) {
    const stream = path.endsWith('/stream')
    record('global', `noToken ${method} ${path.replace(sampleId, '{id}')}`, 'anonymous', g.noToken, (await http(method, path, { abortAfterHeaders: stream })).status)
    record('global', `badToken ${method} ${path.replace(sampleId, '{id}')}`, 'anonymous', g.badToken, (await http(method, path, { token: 'not.a.jwt', abortAfterHeaders: stream })).status)
  }

  record('global', 'unknownEngagement', 'alice', g.unknownEngagement, (await http('GET', '/engagements/ENG-999999', { token: tokens.alice })).status)
  record('global', 'internal access via gateway', 'alice', g.internalViaGateway, (await http('GET', `/internal/engagements/${sampleId}/access`, { token: tokens.alice })).status)
  record('global', 'internal readiness via gateway', 'alice', g.internalViaGateway, (await http('GET', `/internal/engagements/${sampleId}/submission-readiness`, { token: tokens.alice })).status)
}

// ---- Report -----------------------------------------------------------------------------------------------------

function report() {
  const scoped = results.filter((r) => r.scope !== 'global')
  const ops = Object.keys(matrix.operations)
  console.log('\n== Grid (rows: operation, columns: actor; each cell is the HTTP status, red = differs from the matrix) ==')
  for (const scope of [...new Set(scoped.map((r) => r.scope))]) {
    const rows = scoped.filter((r) => r.scope === scope)
    const actors = [...new Set(rows.map((r) => r.actor))]
    console.log(`\n${scope}`)
    console.log('  ' + 'operation'.padEnd(18) + actors.map((a) => a.padEnd(9)).join(''))
    for (const op of ops) {
      const line = actors.map((a) => {
        const cell = rows.filter((r) => r.op === op && r.actor === a)
        if (cell.length === 0) return dim('-'.padEnd(9))
        const text = String(cell[0].actual).padEnd(9)
        return cell.every((c) => c.ok) ? green(text) : red(text)
      })
      if (line.every((l) => l.includes('-') && !/\d/.test(l))) continue
      console.log('  ' + op.padEnd(18) + line.join(''))
    }
  }

  const failed = results.filter((r) => !r.ok)
  console.log(`\n${results.length} checks, ${results.length - failed.length} passed, ${failed.length} failed.`)
  for (const f of failed) console.log(red(`  FAIL ${f.scope} ${f.op} as ${f.actor}: expected ${f.expected.join('|')}, got ${f.actual}`))
  return failed.length === 0
}

// ---- Main -------------------------------------------------------------------------------------------------------

try {
  const health = await http('GET', '/health/auth').catch(() => null)
  if (!health || health.status !== 200) throw new Error(`The gateway at ${BASE} is not reachable. Start the stack: ./infra/local/up.ps1`)

  for (const name of Object.keys(matrix.users)) await login(name)
  const first = await runLifecycle('alice', 'bob')
  await runLifecycle('bob', 'alice')
  await runGlobal(first)
  process.exit(report() ? 0 : 1)
} catch (error) {
  console.error(red(`\nAudit could not complete: ${error.message}`))
  if (results.length > 0) report()
  process.exit(1)
}
