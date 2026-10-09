/** Authenticated active verifier generation (or install-time runtime contract).
 *  CI verify.mjs must remain the generation launcher template (upgradeChain pins its bytes).
 *  External tooling (authority export) loads the same generation module through here. */
import { readFile, realpath, readdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const UPGRADE_PREFIX = '// AUTH authorized verifier upgrade ';
const UPGRADE_HISTORY_DIR = '.auth-kit/upgrades/history';
const fail = code => {
  throw Object.assign(new Error(code), { code });
};

async function authorizedVerifierUpgradeRecorded(root) {
  let names;
  try {
    names = await readdir(path.join(root, UPGRADE_HISTORY_DIR));
  } catch (e) {
    if (e.code === 'ENOENT') return false;
    throw e;
  }
  return names.some(name => /^[a-f0-9]{64}\.json$/.test(name));
}

export async function loadActiveProductContract(root) {
  root = await realpath(typeof root === 'string' ? root : fileURLToPath(root));
  const verify = await readFile(path.join(root, '.auth-kit/verify.mjs'), 'utf8');
  const firstLine = verify.split(/\r?\n/, 1)[0] ?? '';
  const validUpgrade = new RegExp(`^${UPGRADE_PREFIX.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}([a-f0-9]{64})$`).exec(firstLine);
  if (!validUpgrade) {
    if (firstLine.startsWith(UPGRADE_PREFIX)) fail('UPGRADE_INVALID');
    if (await authorizedVerifierUpgradeRecorded(root)) fail('UPGRADE_INVALID');
    return import(pathToFileURL(path.join(root, '.auth-kit/runtime/auth-product-contract.mjs')).href);
  }
  const head = validUpgrade[1];
  const generation = path.join(root, '.auth-kit/upgrades/generations', head);
  let chainMod;
  try {
    chainMod = await import(pathToFileURL(path.join(generation, 'auth-verifier-upgrade-chain.mjs')).href);
  } catch {
    fail('UPGRADE_INVALID');
  }
  const chain = await chainMod.upgradeChain(root);
  if (!chain || chain.head !== head) fail('UPGRADE_INVALID');
  return import(pathToFileURL(path.join(generation, 'auth-product-contract.mjs')).href);
}
