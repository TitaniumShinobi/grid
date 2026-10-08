import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { canonicalJson, deepFreeze, digest } from './canonical-json.mjs';

const EVENT_CONTRACT = 'life-startup-event/v1';
const RECEIPT_CONTRACT = 'life-startup-receipt/v1';
const EVENT_VERSION = '1.0.0';
const STAGE_CLASSES = new Set(['REGISTRATION', 'STARTUP_REQUIRED', 'BACKGROUND_OPTIONAL', 'STALE_REFRESH', 'REBUILD']);
const EVENT_STATUSES = new Set(['STARTED', 'COMPLETED', 'SKIPPED', 'FAILED', 'RETRYING']);
const DEPENDENCY_STATUSES = new Set(['PASS', 'FAIL', 'INDETERMINATE']);
const RESULT_STATUSES = new Set(['PASS', 'FAIL', 'INDETERMINATE']);
const STATUS_ORDER = new Map([...EVENT_STATUSES].map((status, index) => [status, index]));

function fail(message) { throw new TypeError(message); }

function string(value, name) {
  if (typeof value !== 'string' || value.length === 0 || value !== value.normalize('NFC')) fail(`${name} must be a non-empty NFC string`);
  return value;
}

function date(value, name, nullable = false) {
  if (value === null && nullable) return null;
  string(value, name);
  const milliseconds = Date.parse(value);
  if (!Number.isFinite(milliseconds)) fail(`${name} must be an ISO date-time`);
  return milliseconds;
}

function result(value, name) {
  if (!RESULT_STATUSES.has(value)) fail(`${name} must be PASS, FAIL, or INDETERMINATE`);
  return value;
}

function normalizeDependency(value, index) {
  if (!value || typeof value !== 'object') fail(`dependencies[${index}] must be an object`);
  string(value.dependency_id, `dependencies[${index}].dependency_id`);
  if (!DEPENDENCY_STATUSES.has(value.status)) fail(`dependencies[${index}].status is invalid`);
  if (typeof value.required !== 'boolean') fail(`dependencies[${index}].required must be boolean`);
  if (value.observed_at !== undefined && value.observed_at !== null) date(value.observed_at, `dependencies[${index}].observed_at`);
  if (value.details !== undefined && value.details !== null && (typeof value.details !== 'object' || Array.isArray(value.details))) fail(`dependencies[${index}].details must be an object or null`);
  return { dependency_id: value.dependency_id, status: value.status, required: value.required, observed_at: value.observed_at ?? null, details: value.details ?? null };
}

function normalizeProgress(value) {
  if (!value || typeof value !== 'object' || !['KNOWN', 'INDETERMINATE'].includes(value.status)) fail('progress_units is invalid');
  if (!Number.isInteger(value.completed) || value.completed < 0) fail('progress_units.completed must be a non-negative integer');
  if (value.total !== null && (!Number.isInteger(value.total) || value.total < 0)) fail('progress_units.total must be a non-negative integer or null');
  if (value.status === 'KNOWN' && value.total === null) fail('KNOWN progress requires total');
  if (value.status === 'KNOWN' && value.completed > value.total) fail('completed progress cannot exceed total');
  if (value.status === 'INDETERMINATE' && value.total !== null) fail('INDETERMINATE progress requires null total');
  return { status: value.status, completed: value.completed, total: value.total };
}

function normalizeReadiness(value) {
  if (value === undefined || value === null) return null;
  if (!value || typeof value !== 'object') fail('readiness must be an object or null');
  result(value.status, 'readiness.status');
  if (value.evidence_refs !== undefined && (!Array.isArray(value.evidence_refs) || value.evidence_refs.some((entry) => typeof entry !== 'string'))) fail('readiness.evidence_refs must be an array of strings');
  return { status: value.status, evidence_refs: [...(value.evidence_refs || [])].sort(compareUtf8), details: value.details ?? null };
}

function compareUtf8(left, right) { return Buffer.from(left, 'utf8').compare(Buffer.from(right, 'utf8')); }

export function validateStartupEvent(event) {
  if (!event || typeof event !== 'object' || Array.isArray(event)) fail('startup event must be an object');
  if (event.contract !== EVENT_CONTRACT) fail('startup event contract is invalid');
  if (event.contract_version !== EVENT_VERSION) fail('startup event contract_version is invalid');
  for (const [key, value] of [['startup_id', event.startup_id], ['product_id', event.product_id], ['stage_id', event.stage_id]]) string(value, key);
  for (const key of ['state_generation', 'registration_generation', 'state_snapshot_id', 'completed_at', 'cache_hit', 'rebuild_reason', 'error', 'readiness', 'receipt_ref']) if (!Object.hasOwn(event, key)) fail(`${key} must be present`);
  if (!STAGE_CLASSES.has(event.stage_class)) fail('startup event stage_class is invalid');
  if (!EVENT_STATUSES.has(event.status)) fail('startup event status is invalid');
  if (typeof event.required !== 'boolean') fail('startup event required must be boolean');
  const started = date(event.started_at, 'started_at');
  const completed = date(event.completed_at, 'completed_at', true);
  if (completed !== null && completed < started) fail('completed_at cannot precede started_at');
  if (['STARTED', 'RETRYING'].includes(event.status) && completed !== null) fail(`${event.status} events cannot have completed_at`);
  if (['COMPLETED', 'SKIPPED', 'FAILED'].includes(event.status) && completed === null) fail(`${event.status} events require completed_at`);
  for (const [key, value] of [['state_generation', event.state_generation], ['registration_generation', event.registration_generation], ['state_snapshot_id', event.state_snapshot_id]]) if (value !== null) string(value, key);
  if (event.cache_hit !== undefined && event.cache_hit !== null && typeof event.cache_hit !== 'boolean') fail('cache_hit must be boolean or null');
  if (event.rebuild_reason !== undefined && event.rebuild_reason !== null) string(event.rebuild_reason, 'rebuild_reason');
  const normalized = {
    contract: EVENT_CONTRACT,
    startup_id: event.startup_id,
    product_id: event.product_id,
    contract_version: EVENT_VERSION,
    stage_id: event.stage_id,
    stage_class: event.stage_class,
    state_generation: event.state_generation ?? null,
    registration_generation: event.registration_generation ?? null,
    state_snapshot_id: event.state_snapshot_id ?? null,
    started_at: event.started_at,
    completed_at: event.completed_at ?? null,
    status: event.status,
    required: event.required,
    cache_hit: event.cache_hit ?? null,
    rebuild_reason: event.rebuild_reason ?? null,
    progress_units: normalizeProgress(event.progress_units),
    dependencies: event.dependencies.map(normalizeDependency).sort((left, right) => compareUtf8(left.dependency_id, right.dependency_id)),
    error: event.error ?? null,
    readiness: normalizeReadiness(event.readiness),
    receipt_ref: event.receipt_ref ?? null,
  };
  return deepFreeze(normalized);
}

export function normalizeStartupEvents(events) {
  if (!Array.isArray(events)) fail('startup events must be an array');
  const normalized = events.map(validateStartupEvent);
  return deepFreeze([...normalized].sort((left, right) => {
    const time = Date.parse(left.started_at) - Date.parse(right.started_at);
    return time || compareUtf8(left.stage_id, right.stage_id) || STATUS_ORDER.get(left.status) - STATUS_ORDER.get(right.status);
  }));
}

function finding(code, stage_id = null, details = {}) { return { code, stage_id, details }; }

export function analyzeStartupFindings(events) {
  const normalized = normalizeStartupEvents(events);
  const findings = [];
  const byStage = new Map();
  for (const event of normalized) {
    const entries = byStage.get(event.stage_id) || [];
    entries.push(event);
    byStage.set(event.stage_id, entries);
    if (event.dependencies.some((dependency) => dependency.required && dependency.status === 'FAIL')) findings.push(finding('REQUIRED_DEPENDENCY_FAILED', event.stage_id));
    if (event.dependencies.some((dependency) => dependency.required && dependency.status === 'INDETERMINATE')) findings.push(finding('REQUIRED_DEPENDENCY_UNAVAILABLE', event.stage_id));
    if (event.stage_class === 'STALE_REFRESH') findings.push(finding('STALE_REFRESH_OBSERVED', event.stage_id));
    if (event.stage_class === 'REBUILD') findings.push(finding('REBUILD_OBSERVED', event.stage_id, { rebuild_reason: event.rebuild_reason }));
  }
  for (const [stage_id, entries] of byStage) {
    const registrations = entries.filter((event) => event.stage_class === 'REGISTRATION');
    if (registrations.length > 1) findings.push(finding('REPEATED_REGISTRATION', stage_id, { count: registrations.length }));
    const generations = new Set(entries.map((event) => event.state_generation).filter(Boolean));
    const snapshots = new Set(entries.map((event) => event.state_snapshot_id).filter(Boolean));
    if (generations.size > 1 || snapshots.size > 1) findings.push(finding('CONFLICTING_STATE_IDENTITY', stage_id, { state_generations: [...generations].sort(compareUtf8), state_snapshot_ids: [...snapshots].sort(compareUtf8) }));
    if (entries.some((event) => event.required && ['STARTED', 'RETRYING'].includes(event.status) && !entries.some((candidate) => ['COMPLETED', 'SKIPPED', 'FAILED'].includes(candidate.status)))) findings.push(finding('REQUIRED_STAGE_INCOMPLETE', stage_id));
    const knownTotals = new Set(entries.filter((event) => event.progress_units.status === 'KNOWN').map((event) => event.progress_units.total));
    if (knownTotals.size > 1) findings.push(finding('INCONSISTENT_PROGRESS_TOTAL', stage_id));
  }
  const completedRequired = new Set(normalized.filter((event) => event.required && ['COMPLETED', 'SKIPPED'].includes(event.status)).map((event) => event.stage_id));
  if (normalized.some((event) => event.readiness?.status === 'PASS' && normalized.filter((candidate) => candidate.required).some((candidate) => !completedRequired.has(candidate.stage_id)))) findings.push(finding('READINESS_PRECEDES_REQUIRED_STAGES'));
  return deepFreeze(findings.sort((left, right) => compareUtf8(`${left.code}\u0000${left.stage_id || ''}`, `${right.code}\u0000${right.stage_id || ''}`)));
}

function assertReceiptShape(receipt) {
  if (!receipt || typeof receipt !== 'object' || receipt.contract !== RECEIPT_CONTRACT || receipt.version !== EVENT_VERSION) fail('startup receipt contract is invalid');
  string(receipt.startup_id, 'receipt.startup_id');
  string(receipt.product_id, 'receipt.product_id');
  date(receipt.started_at, 'receipt.started_at');
  date(receipt.ready_at, 'receipt.ready_at', true);
  if (receipt.duration_ms !== null && (!Number.isInteger(receipt.duration_ms) || receipt.duration_ms < 0)) fail('receipt.duration_ms is invalid');
  if (!Array.isArray(receipt.stages)) fail('receipt.stages must be an array');
  receipt.stages.forEach(validateStartupEvent);
  if (!receipt.readiness || !RESULT_STATUSES.has(receipt.readiness.status)) fail('receipt.readiness is invalid');
  if (!receipt.verification || !RESULT_STATUSES.has(receipt.verification.status)) fail('receipt.verification is invalid');
  if (typeof receipt.receipt_digest !== 'string' || !/^[a-f0-9]{64}$/u.test(receipt.receipt_digest)) fail('receipt.receipt_digest is invalid');
}

function receiptDigest(receipt) {
  const { receipt_digest: ignored, ...preimage } = receipt;
  return digest(preimage);
}

export function validateStartupReceipt(receipt) {
  assertReceiptShape(receipt);
  if (receiptDigest(receipt) !== receipt.receipt_digest) fail('startup receipt digest mismatch');
  return deepFreeze(receipt);
}

export function buildStartupReceipt(events, readiness, verification, options = {}) {
  const stages = normalizeStartupEvents(events);
  if (!stages.length) fail('at least one startup event is required');
  const startup_id = stages[0].startup_id;
  const product_id = options.product_id || stages[0].product_id;
  if (stages.some((event) => event.startup_id !== startup_id || event.product_id !== product_id)) fail('startup events must share startup and product identity');
  const normalizedReadiness = normalizeReadiness(readiness);
  if (!normalizedReadiness) fail('receipt readiness is required');
  if (!verification || typeof verification !== 'object') fail('receipt verification is required');
  const verificationResult = { status: result(verification.status, 'verification.status'), digest: verification.digest ?? null, details: verification.details ?? null };
  const started_at = stages.reduce((value, event) => Date.parse(event.started_at) < Date.parse(value) ? event.started_at : value, stages[0].started_at);
  const ready_at = options.ready_at ?? (normalizedReadiness.status === 'PASS' ? stages.reduce((value, event) => event.completed_at && Date.parse(event.completed_at) > Date.parse(value) ? event.completed_at : value, started_at) : null);
  if (ready_at !== null) date(ready_at, 'ready_at');
  if (ready_at !== null && Date.parse(ready_at) < Date.parse(started_at)) fail('ready_at cannot precede started_at');
  const dependencies = [...new Map(stages.flatMap((event) => event.dependencies).map((dependency) => [dependency.dependency_id, dependency])).values()].sort((left, right) => compareUtf8(left.dependency_id, right.dependency_id));
  const state = { state_generation: options.state?.state_generation ?? null, registration_generation: options.state?.registration_generation ?? null, state_snapshot_id: options.state?.state_snapshot_id ?? null, prepared_state_loaded: options.state?.prepared_state_loaded ?? null, rebuilds: stages.filter((event) => event.stage_class === 'REBUILD').map((event) => ({ stage_id: event.stage_id, reason: event.rebuild_reason })) };
  const core = { contract: RECEIPT_CONTRACT, version: EVENT_VERSION, startup_id, product_id, started_at, ready_at, duration_ms: ready_at === null ? null : Date.parse(ready_at) - Date.parse(started_at), stages, dependencies, state, readiness: normalizedReadiness, verification: verificationResult, findings: analyzeStartupFindings(stages), baseline_comparison: options.baseline_comparison || { status: 'NO_BASELINE' } };
  return deepFreeze({ ...core, receipt_digest: receiptDigest(core) });
}

function receiptFilename(startup_id) { return `${startup_id.replace(/[^A-Za-z0-9._-]/gu, '_')}.json`; }

export function writeStartupReceipt(receipt, directory) {
  validateStartupReceipt(receipt);
  string(directory, 'receipt directory');
  mkdirSync(directory, { recursive: true });
  const path = join(directory, receiptFilename(receipt.startup_id));
  writeFileSync(path, `${canonicalJson(receipt)}\n`, 'utf8');
  return path;
}

export function readStartupReceipt(path) {
  string(path, 'receipt path');
  return validateStartupReceipt(JSON.parse(readFileSync(path, 'utf8')));
}

export function compareStartupReceipt(receipt, baseline, policy = {}) {
  if (!baseline) return { status: 'NO_BASELINE', regressions: [] };
  try { validateStartupReceipt(receipt); validateStartupReceipt(baseline); } catch { return { status: 'INDETERMINATE', regressions: [] }; }
  const absolute = Number.isFinite(policy.absolute_ms) ? policy.absolute_ms : 0;
  const relative = Number.isFinite(policy.relative_ratio) ? policy.relative_ratio : 0;
  const regressions = [];
  if (receipt.duration_ms !== null && baseline.duration_ms !== null && receipt.duration_ms - baseline.duration_ms > Math.max(absolute, baseline.duration_ms * relative)) regressions.push({ scope: 'startup', observed_ms: receipt.duration_ms, baseline_ms: baseline.duration_ms });
  const baselineStages = new Map(baseline.stages.map((stage) => [stage.stage_id, stage]));
  for (const stage of receipt.stages) {
    const prior = baselineStages.get(stage.stage_id);
    if (!prior || !stage.completed_at || !prior.completed_at) continue;
    const observed = Date.parse(stage.completed_at) - Date.parse(stage.started_at);
    const previous = Date.parse(prior.completed_at) - Date.parse(prior.started_at);
    if (observed - previous > Math.max(absolute, previous * relative)) regressions.push({ scope: 'stage', stage_id: stage.stage_id, observed_ms: observed, baseline_ms: previous });
  }
  return { status: regressions.length ? 'REGRESSION' : 'PASS', regressions };
}
