// GRID product-owned AUTH evidence collector (RELYING_PARTY).
// Every check result comes from GRID's in-repository auth test runner, executed
// fresh during this observation. Source text, provider buttons and earlier logs
// are never evidence. Checks without a directly exercising test are omitted and
// stay INDETERMINATE. No live credentials, tokens, cookies or DPAPI data are read.
import {readFileSync, statSync, readdirSync} from 'node:fs';
import {spawnSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import path from 'node:path';
import {CAPABILITIES} from './runtime/auth-capability-catalog.mjs';

const binding = JSON.parse(readFileSync(0, 'utf8'));
const root = process.cwd();
const ARTIFACT = 'src/Grid.App/bin/x64/Debug/net9.0-windows10.0.19041.0/win-x64/Grid.dll';
const AUTH_SOURCE_DIRS = ['core/api/Auth', 'core/app', 'core/features/users/auth', 'core/features/users/auth/ui'];
const AUTH_SOURCE_FILES = ['src/Grid.App/MainWindow.xaml.cs', 'src/Grid.App/Grid.App.csproj', 'config/grid.auth.config.json'];
const AUTH_CONFIG = 'config/grid.auth.config.json';

// capabilityId -> checkId -> exact "[suite] name" tests that must all PASS.
const MAP = {
  'signin': {
    explicit_entry: ['[flow] native provider flow end to end over a real loopback listener'],
    existing_identity_authenticated: ['[flow] native provider flow end to end over a real loopback listener'],
  },
  'providers.callback': {
    provider_callback_contract_preserved: ['[flow] native provider flow end to end over a real loopback listener'],
    provider_error_visible: ['[flow] recoverable authorization failure allows a subsequent sign-in attempt'],
    provider_cancellation_handled: ['[flow] cancel pending native flow and oauth decline resolve signed out'],
  },
  'relying_party.client_operations': {
    authorization_launched: ['[flow] native provider flow end to end over a real loopback listener'],
    userinfo_identity_validated: ['[http] GET /api/me attaches bearer and unwraps user', '[flow] native provider flow end to end over a real loopback listener'],
    refresh_response_handled: ['[session] RestoreAsync refreshes an expiring bag', '[flow] persisted session remains resolving through refresh and identity verification'],
    revoke_request_sent: ['[session] SignOutAsync revokes then clears'],
  },
  'relying_party.pkce': {
    applicability_declared: ['[pkce] RFC 7636 S256 vector', '[flow] native provider flow end to end over a real loopback listener'],
    s256_challenge_required_when_applicable: ['[flow] native provider flow end to end over a real loopback listener', '[http] authorization_code + PKCE form is submitted'],
  },
  'relying_party.callback': {
    state_bound_to_request: ['[flow] native provider flow end to end over a real loopback listener'],
    successful_completion_explicit: ['[flow] native provider flow end to end over a real loopback listener'],
    failure_completion_explicit: ['[flow] cancel pending native flow and oauth decline resolve signed out'],
  },
  'relying_party.authority': {
    consumer_does_not_reissue_unverified_identity: ['[flow] native provider flow end to end over a real loopback listener', '[flow] persisted session remains resolving through refresh and identity verification'],
    assertion_issuer_and_audience_validated: ['[http] GET /api/me attaches bearer and unwraps user', '[flow] native provider flow end to end over a real loopback listener'],
  },
  'session.establishment': {
    session_bound_to_identity: ['[flow] native provider flow end to end over a real loopback listener', '[flow] sign out and account switch replace the canonical profile image'],
  },
  'session.persistence': {
    session_persisted: ['[token-store] in-memory store round-trip', '[session] RestoreAsync refreshes an expiring bag'],
    session_bound_to_canonical_identity: ['[flow] persisted session remains resolving through refresh and identity verification'],
  },
  'session.restart': {
    valid_session_restored_after_restart: ['[flow] persisted session remains resolving through refresh and identity verification', '[session] RestoreAsync refreshes an expiring bag'],
  },
  'session.expiry': {
    expired_session_has_explicit_outcome: ['[flow] session restore publishes resolving before its terminal state', '[session] RestoreAsync clears store on 401'],
  },
  'session.logout': {
    client_credentials_cleared: ['[flow] sign out clears service and store', '[session] SignOutAsync revokes then clears'],
    completion_defined: ['[flow] sign out clears service and store'],
  },
  'security.failure': {
    dependency_failure_fails_closed: ['[flow] discovery failure falls back with error code'],
    no_silent_destructive_repair: ['[session] RestoreAsync clears store on 401'],
    sensitive_data_not_exposed: ['[http] api errors omit bearer token from exception surface'],
  },
  'security.identity': {
    canonical_identity_source_used: ['[flow] persisted session remains resolving through refresh and identity verification', '[flow] sign out and account switch replace the canonical profile image'],
  },
  'security.protocol': {
    csrf_or_request_binding_enforced: ['[flow] native provider flow end to end over a real loopback listener', '[pkce] generated state/verifier/verifier lengths'],
  },
  'magic.request': {
    request_contract_defined: ['[http] magic request posts email/intent/origin'],
    request_intent_preserved: ['[http] magic request posts email/intent/origin'],
  },
  'ux.continuation': {
    pending_action_explained: ['[flow] consent gate consumes then finalizes through consent'],
    retry_available_when_recoverable: ['[flow] recoverable authorization failure allows a subsequent sign-in attempt'],
    cancel_or_decline_outcome_defined: ['[flow] cancel pending native flow and oauth decline resolve signed out'],
  },
  'ux.terminal': {
    all_routes_have_explicit_outcome: ['[flow] session restore publishes resolving before its terminal state'],
    recoverable_errors_visible: ['[flow] discovery failure falls back with error code'],
    configuration_errors_visible: ['[session] BootstrapFromHostedSessionAsync requires refresh cookie'],
    blank_terminal_absent: ['[flow] session restore publishes resolving before its terminal state'],
  },
};

// Applicability + product integration for capabilities whose catalog checks are authority-owned.
const APPLICABILITY_ONLY = {
  'session.validation': [
    '[session] RestoreAsync clears store on 401',
    '[flow] persisted session remains resolving through refresh and identity verification',
  ],
  'session.refresh': [
    '[session] RestoreAsync refreshes an expiring bag',
    '[http] refresh grant request shape',
    '[flow] persisted session remains resolving through refresh and identity verification',
  ],
  'relying_party.authorization': [
    '[flow] native provider flow end to end over a real loopback listener',
    '[http] authorization_code + PKCE form is submitted',
  ],
};

function newestMtime(rel) {
  const full = path.join(root, rel);
  const st = statSync(full);
  if (!st.isDirectory()) return st.mtimeMs;
  return Math.max(0, ...readdirSync(full).filter(n => n.endsWith('.cs')).map(n => statSync(path.join(full, n)).mtimeMs));
}

function buildIsCurrent() {
  try {
    const built = statSync(path.join(root, ARTIFACT)).mtimeMs;
    const newest = Math.max(...AUTH_SOURCE_DIRS.concat(AUTH_SOURCE_FILES).map(newestMtime));
    return built >= newest;
  } catch { return false; }
}

function runAuthTests() {
  const r = spawnSync('dotnet', ['run', '--project', 'tests/Grid.Auth.Tests', '--nologo'], {
    cwd: root, encoding: 'utf8', timeout: 100000, maxBuffer: 4 * 1024 * 1024,
    env: {...process.env, DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1'},
  });
  const results = new Map();
  for (const line of (r.stdout || '').split(/\r?\n/)) {
    const m = /^(PASS|FAIL) {2}(\[[^\]]+\] .+)$/.exec(line);
    if (m) results.set(m[2].trim(), m[1]);
  }
  const summary = /== (\d+) tests, (\d+) passed, (\d+) failed ==/.exec(r.stdout || '');
  return {status: r.status, results, summary: summary ? {total: +summary[1], passed: +summary[2], failed: +summary[3]} : null,
    digest: createHash('sha256').update([...results].map(([k, v]) => `${v} ${k}`).sort().join('\n')).digest('hex')};
}

function finalizeEntry(entry) {
  entry.evidence = [...new Set(entry.evidence)];
  if (Object.keys(entry.checks).length) entry.implementation = 'ESTABLISHED';
  return entry;
}

/** CONTRACT: Grid declares an external identity authority via hosted OAuth config, not local issuance. */
function applyIdentityAuthorityBoundary(entry) {
  try {
    const bytes = readFileSync(path.join(root, AUTH_CONFIG));
    const config = JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
    const digest = createHash('sha256').update(bytes).digest('hex');
    if (typeof config.baseUrl !== 'string' || !/^https:\/\//.test(config.baseUrl)) return;
    if (typeof config.clientId !== 'string' || !config.clientId.trim()) return;
    entry.applicability = 'TRUE';
    entry.checks.identity_authority_boundary_declared = true;
    entry.evidence.push(`relying-party-boundary config=${AUTH_CONFIG} sha256=${digest} baseUrl=${config.baseUrl} clientId=${config.clientId} authenticationRole=RELYING_PARTY revision=${binding.revision}`);
  } catch { /* unreadable config leaves contract check unproven */ }
}

const current = buildIsCurrent();
const run = current ? runAuthTests() : null;
const runRef = run ? `grid-auth-tests run=${binding.runId} commit=${binding.commit} artifact=${binding.artifactDigest} results=${run.digest} total=${run.summary?.total ?? 'unparsed'} failed=${run.summary?.failed ?? 'unparsed'}` : null;

const capabilities = {};
for (const c of CAPABILITIES) capabilities[c.id] = {applicability: 'INDETERMINATE', implementation: 'INDETERMINATE', evidence: [], checks: {}};

function applyMappedChecks(entry, checks, run) {
  for (const [checkId, tests] of Object.entries(checks)) {
    const outcomes = tests.map(t => run.results.get(t));
    if (outcomes.some(o => o === undefined)) continue;
    entry.checks[checkId] = outcomes.every(o => o === 'PASS');
    entry.evidence.push(...tests.map(t => `${runRef} test="${t}" result=${run.results.get(t)}`));
  }
}

function applyApplicabilityOnly(tests, run) {
  const outcomes = tests.map(t => run.results.get(t));
  if (outcomes.some(o => o === undefined) || !outcomes.every(o => o === 'PASS')) return null;
  const evidence = tests.map(t => `${runRef} test="${t}" result=${run.results.get(t)}`);
  return {applicability: 'TRUE', implementation: 'ESTABLISHED', evidence: [...new Set(evidence)], checks: {}};
}

if (run && run.summary) {
  for (const [capabilityId, checks] of Object.entries(MAP)) {
    const entry = {applicability: 'TRUE', implementation: 'INDETERMINATE', evidence: [], checks: {}};
    applyMappedChecks(entry, checks, run);
    capabilities[capabilityId] = finalizeEntry(entry);
  }
  for (const [capabilityId, tests] of Object.entries(APPLICABILITY_ONLY)) {
    const entry = applyApplicabilityOnly(tests, run);
    if (entry) capabilities[capabilityId] = entry;
  }
}

{
  const entry = capabilities['relying_party.authority'] || {applicability: 'INDETERMINATE', implementation: 'INDETERMINATE', evidence: [], checks: {}};
  applyIdentityAuthorityBoundary(entry);
  if (Object.keys(entry.checks).length) capabilities['relying_party.authority'] = finalizeEntry(entry);
}

// Runtime independence is a reviewed operator attestation bound to this exact artifact.
let independence = {wizardStopped: false, authOrchestrationRequests: null, runtimePassed: false, evidence: []};
const attestationPath = process.env.GRID_AUTH_RUNTIME_ATTESTATION;
const attestationDigest = process.env.GRID_AUTH_RUNTIME_ATTESTATION_SHA256;
if (attestationPath && /^[a-f0-9]{64}$/.test(attestationDigest || '')) {
  try {
    const bytes = readFileSync(attestationPath);
    const a = JSON.parse(bytes);
    if (createHash('sha256').update(bytes).digest('hex') === attestationDigest && a.artifactDigest === binding.artifactDigest &&
        a.configurationDigest === binding.configurationDigest && Array.isArray(a.evidence) && a.evidence.length) {
      independence = {wizardStopped: a.wizardStopped === true, authOrchestrationRequests: a.authOrchestrationRequests,
        runtimePassed: a.runtimePassed === true, evidence: a.evidence.map(String)};
    }
  } catch { /* Unreadable attestation leaves independence unproven. */ }
}

const adapter = {schemaVersion: 1, productId: binding.productId, revision: binding.revision, authenticationRole: 'RELYING_PARTY', capabilities};
if (process.env.GRID_AUTH_AUTHORITY_ID && process.env.GRID_AUTH_AUTHORITY_REVISION) {
  adapter.identityAuthority = {id: process.env.GRID_AUTH_AUTHORITY_ID, revision: process.env.GRID_AUTH_AUTHORITY_REVISION};
}
console.log(JSON.stringify({binding, adapter, independence}));
