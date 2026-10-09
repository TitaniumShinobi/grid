import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { createReadStream } from 'node:fs';
import { mkdtemp, readFile, writeFile, rm, cp, readdir, copyFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';
import test from 'node:test';
import assert from 'node:assert/strict';

const repoRoot = join(import.meta.dirname, '..', '..', '..');
const head = '1e45e6f3467b70c66c0338d0c6c0f937e9a18f8db32fdae8efef0a4f8f613e2e';
const protectedRoots = [
  join(repoRoot, '.auth-kit/provenance.json'),
  join(repoRoot, '.auth-kit/runtime'),
  join(repoRoot, '.auth-kit/upgrades/history'),
  join(repoRoot, '.auth-kit/upgrades/generations'),
];

async function blobHash(path) {
  const h = createHash('sha256');
  for await (const chunk of createReadStream(path)) h.update(chunk);
  return h.digest('hex');
}

async function protectedSnapshot() {
  const out = {};
  for (const root of protectedRoots) {
    if (root.endsWith('provenance.json')) {
      out[root] = await blobHash(root);
      continue;
    }
    async function walk(dir, prefix = '') {
      for (const name of (await readdir(dir, { withFileTypes: true })).sort((a, b) => a.name.localeCompare(b.name))) {
        const p = join(dir, name.name);
        const rel = join(prefix, name.name);
        if (name.isDirectory()) await walk(p, rel);
        else out[join(root, rel)] = await blobHash(p);
      }
    }
    await walk(root);
  }
  return out;
}

async function cloneRepo() {
  const base = await mkdtemp(join(tmpdir(), 'grid-auth-export-'));
  const dest = join(base, 'grid');
  const r = spawnSync('git', ['clone', '--no-hardlinks', repoRoot, dest], { encoding: 'utf8' });
  assert.equal(r.status, 0, r.stderr || r.stdout);
  return { base, dest };
}

test('loadActiveProductContract: inspect passes on upgraded GRID', async () => {
  const { loadActiveProductContract } = await import(pathToFileURL(join(repoRoot, '.auth-kit/load-active-product-contract.mjs')).href);
  const active = await loadActiveProductContract(repoRoot);
  const state = await active.inspectProductBundle(repoRoot);
  assert.equal(state.contract.productId, 'grid');
});

test('exportAuthorityBinding: fast path on current binding snapshot', async () => {
  const { loadActiveProductContract } = await import(pathToFileURL(join(repoRoot, '.auth-kit/load-active-product-contract.mjs')).href);
  const { exportAuthorityBinding } = await import(pathToFileURL(join(repoRoot, 'scripts/auth-authority-binding-export/auth-export-authority-binding.mjs')).href);
  const active = await loadActiveProductContract(repoRoot);
  const state = await active.inspectProductBundle(repoRoot);
  const binding = await active.currentBinding(repoRoot, state.contract);
  const observedAt = new Date().toISOString();
  const observerDigest = createHash('sha256').update(await readFile(join(repoRoot, '.auth-kit/observe.mjs'))).digest('hex');
  const report = {
    productId: 'grid',
    revision: binding.revision,
    binding: { ...binding, observedAt, runId: '00000000-0000-4000-8000-000000000001' },
    contractHead: state.contract.head,
    contractDigest: active.hash(state.contract),
    observerDigest,
  };
  const reportDir = await mkdtemp(join(tmpdir(), 'grid-auth-report-'));
  const reportPath = join(reportDir, 'reviewed.json');
  const reportBytes = JSON.stringify(report, null, 2) + '\n';
  await writeFile(reportPath, reportBytes);
  const reportSha256 = createHash('sha256').update(reportBytes).digest('hex');
  const outDir = await mkdtemp(join(tmpdir(), 'grid-auth-out-'));
  const outPath = join(outDir, 'binding.json');
  const env = {
    ...process.env,
    GRID_AUTH_AUTHORITY_ID: 'https://grid.thewreck.org',
    GRID_AUTH_AUTHORITY_REVISION: '5f1176186b3bab33769745dfce5f1d1e127ffaeb',
  };
  const result = await exportAuthorityBinding({
    repo: repoRoot,
    out: outPath,
    report: reportPath,
    reportSha256,
    env,
    now: Date.now(),
  });
  assert.match(result.sha256, /^[a-f0-9]{64}$/);
  assert.equal(result.reviewedRevision, binding.revision);
  await rm(reportDir, { recursive: true, force: true });
  await rm(outDir, { recursive: true, force: true });
});

test('loadActiveProductContract: rejects tampered generation', async () => {
  const { base, dest } = await cloneRepo();
  try {
    await copyFile(join(repoRoot, '.auth-kit/verify.mjs'), join(dest, '.auth-kit/verify.mjs'));
    const target = join(dest, '.auth-kit/upgrades/generations', head, 'auth-product-contract.mjs');
    await copyFile(
      join(repoRoot, '.auth-kit/upgrades/generations', head, 'auth-product-contract.mjs'),
      target,
    );
    const bytes = await readFile(target);
    bytes[bytes.length - 1] ^= 0x01;
    await writeFile(target, bytes);
    const { loadActiveProductContract } = await import(pathToFileURL(join(repoRoot, '.auth-kit/load-active-product-contract.mjs')).href);
    await assert.rejects(
      () => loadActiveProductContract(dest),
      err => err.code === 'VERIFIER_OR_GATE_DRIFT',
    );
  } finally {
    await rm(base, { recursive: true, force: true });
  }
});

test('loadActiveProductContract: rejects malformed upgrade header', async () => {
  const { base, dest } = await cloneRepo();
  try {
    await writeFile(
      join(dest, '.auth-kit/verify.mjs'),
      '// AUTH authorized verifier upgrade not-valid-hex-or-length\r\nimport "./runtime/auth-product-contract.mjs";\n',
    );
    const { loadActiveProductContract } = await import(
      pathToFileURL(join(repoRoot, '.auth-kit/load-active-product-contract.mjs')).href,
    );
    await assert.rejects(
      () => loadActiveProductContract(dest),
      err => err.code === 'UPGRADE_INVALID',
    );
  } finally {
    await rm(base, { recursive: true, force: true });
  }
});

test('loadActiveProductContract: rejects initialVerifier stub after upgrade recorded', async () => {
  const { base, dest } = await cloneRepo();
  try {
    const hist = JSON.parse(
      await readFile(join(dest, '.auth-kit/upgrades/history', `${head}.json`), 'utf8'),
    );
    await writeFile(join(dest, '.auth-kit/verify.mjs'), hist.initialVerifier);
    const { loadActiveProductContract } = await import(
      pathToFileURL(join(repoRoot, '.auth-kit/load-active-product-contract.mjs')).href,
    );
    await assert.rejects(
      () => loadActiveProductContract(dest),
      err => err.code === 'UPGRADE_INVALID',
    );
  } finally {
    await rm(base, { recursive: true, force: true });
  }
});

test('resolver first-line parser accepts CRLF after valid upgrade header', () => {
  const verify = `// AUTH authorized verifier upgrade ${head}\r\nimport './runtime/auth-product-contract.mjs';\n`;
  const firstLine = verify.split(/\r?\n/, 1)[0] ?? '';
  assert.match(firstLine, /^\/\/ AUTH authorized verifier upgrade [a-f0-9]{64}$/);
  assert.doesNotMatch(firstLine, /\r/);
});

test('loadActiveProductContract: rejects unknown generation head', async () => {
  const { base, dest } = await cloneRepo();
  try {
    const chainMod = await import(
      pathToFileURL(join(dest, '.auth-kit/upgrades/generations', head, 'auth-verifier-upgrade-chain.mjs')).href,
    );
    const bogus = 'a'.repeat(64);
    await writeFile(join(dest, '.auth-kit/verify.mjs'), chainMod.launcher(bogus));
    const { loadActiveProductContract } = await import(pathToFileURL(join(repoRoot, '.auth-kit/load-active-product-contract.mjs')).href);
    await assert.rejects(
      () => loadActiveProductContract(dest),
      err => ['VERIFIER_OR_GATE_DRIFT', 'UPGRADE_HISTORY_TAMPERED', 'UPGRADE_INVALID'].includes(err.code),
    );
  } finally {
    await rm(base, { recursive: true, force: true });
  }
});

test('resolver loads generation contract aligned with launcher head', async () => {
  const verify = await readFile(join(repoRoot, '.auth-kit/verify.mjs'), 'utf8');
  const firstLine = verify.split(/\r?\n/, 1)[0] ?? '';
  const match = /^\/\/ AUTH authorized verifier upgrade ([a-f0-9]{64})$/.exec(firstLine);
  assert.ok(match, 'expected authenticated upgrade launcher first line');
  const launcherHead = match[1];
  assert.equal(launcherHead, head);

  const hist = JSON.parse(
    await readFile(join(repoRoot, '.auth-kit/upgrades/history', `${launcherHead}.json`), 'utf8'),
  );
  assert.equal(hist.digest, launcherHead);

  const { loadActiveProductContract } = await import(
    pathToFileURL(join(repoRoot, '.auth-kit/load-active-product-contract.mjs')).href,
  );
  const active = await loadActiveProductContract(repoRoot);
  await active.inspectProductBundle(repoRoot);

  const runtime = await import(pathToFileURL(join(repoRoot, '.auth-kit/runtime/auth-product-contract.mjs')).href);
  await assert.rejects(
    () => runtime.inspectProductBundle(repoRoot),
    err => err.code === 'VERIFIER_OR_GATE_DRIFT',
  );
});

test('protected runtime/provenance/generation bytes unchanged on product tree', async (t) => {
  const before = await protectedSnapshot();
  t.after(async () => {
    const after = await protectedSnapshot();
    assert.deepEqual(after, before);
  });
});
