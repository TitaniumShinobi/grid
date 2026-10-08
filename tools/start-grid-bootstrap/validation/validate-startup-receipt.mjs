import { readStartupReceipt } from '../runtime/startup-events.mjs';

const path = process.argv[2];
if (!path) {
  console.error('Usage: node validation/validate-startup-receipt.mjs <receipt.json>');
  process.exit(2);
}

const receipt = readStartupReceipt(path);
console.log(JSON.stringify({
  contract: receipt.contract,
  version: receipt.version,
  startup_id: receipt.startup_id,
  product_id: receipt.product_id,
  duration_ms: receipt.duration_ms,
  readiness: receipt.readiness,
  verification: receipt.verification,
  findings: receipt.findings,
  receipt_digest: receipt.receipt_digest,
}, null, 2));
