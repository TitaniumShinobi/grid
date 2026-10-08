import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { StartupObserver } from '../runtime/startup-observer.mjs';
import { readStartupReceipt, compareStartupReceipt } from '../runtime/startup-events.mjs';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const manifest = JSON.parse(readFileSync(join(root, 'manifest.json'), 'utf8'));
assert.equal(manifest.package, 'start-grid-bootstrap');
assert.equal(manifest.version, '1.0.0');
assert.equal(manifest.runtime_dependency_on_start, false);
for (const relative of Object.values(manifest.contents).flat()) assert.ok(readFileSync(join(root, relative)).length > 0, relative);
for (const [relative, expected] of Object.entries(manifest.file_sha256)) {
  const actual = createHash('sha256').update(readFileSync(join(root, relative))).digest('hex');
  assert.equal(actual, expected, relative);
}
assert.equal(manifest.file_sha256['README.md'], createHash('sha256').update(readFileSync(join(root, 'README.md'))).digest('hex'));
const event = JSON.parse(readFileSync(join(root, 'contracts/startup-event-v1.json'), 'utf8'));
const receipt = JSON.parse(readFileSync(join(root, 'contracts/startup-receipt-v1.json'), 'utf8'));
assert.equal(event.contract, 'life-startup-event/v1');
assert.equal(receipt.contract, 'life-startup-receipt/v1');
assert.match(readFileSync(join(root, 'runtime/startup-events.mjs'), 'utf8'), /compareStartupReceipt/);
assert.match(readFileSync(join(root, 'runtime/startup-observer.mjs'), 'utf8'), /StartupObserver/);

const receiptDirectory = mkdtempSync(join(tmpdir(), 'start-grid-bootstrap-'));
const observer = new StartupObserver({ startup_id: 'grid-package-test', product_id: 'grid', receipt_directory: receiptDirectory });
const stage = observer.begin('READY', 'STARTUP_REQUIRED', true, '2026-10-02T12:00:00.000Z');
stage.complete({ completed_at: '2026-10-02T12:00:01.000Z', readiness: { status: 'PASS', evidence_refs: ['test.ready'] } });
const first = observer.finish({ status: 'PASS', evidence_refs: ['test.ready'] }, { status: 'PASS' });
const loaded = readStartupReceipt(first.path);
assert.equal(loaded.duration_ms, 1000);
assert.equal(compareStartupReceipt(loaded, null).status, 'NO_BASELINE');
console.log('package surface passed');
