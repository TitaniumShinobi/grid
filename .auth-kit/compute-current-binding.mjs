/** Lightweight currentBinding (no observation, no product edits). */
import { readFile, lstat } from 'node:fs/promises';
import path from 'node:path';
import { realpath } from 'node:fs/promises';
import { collectProductInventory } from './runtime/auth-product-inventory.mjs';
import { hash } from './runtime/auth-product-contract.mjs';
import { runGit, filesystemPath } from './runtime/auth-git.mjs';

const kit = '.auth-kit';
const root = process.cwd();
const fail = code => {
  throw Object.assign(new Error(code), { code });
};
const text = v => typeof v === 'string' && v.trim().length > 0;

async function safeFile(rootDir, relative) {
  if (!text(relative) || path.isAbsolute(relative) || relative.includes('\\') || relative.split('/').some(p => p === '..' || p === '')) fail('UNSAFE_BUNDLE_PATH');
  const target = filesystemPath(path.join(rootDir, relative));
  let current = filesystemPath(rootDir);
  for (const part of relative.split('/')) {
    current = path.join(current, part);
    if ((await lstat(current)).isSymbolicLink()) fail('SYMLINK_NOT_ALLOWED');
  }
  if (!(await lstat(target)).isFile()) fail('EXPECTED_FILE');
  return target;
}

async function hashes(rootDir, paths) {
  const result = {};
  for (const p of [...paths].sort()) result[p] = hash(await readFile(await safeFile(rootDir, p)));
  return result;
}

async function currentBinding(rootDir, c) {
  const git = args => runGit(args, { cwd: rootDir });
  if ((await realpath(git(['rev-parse', '--show-toplevel']).trim())) !== (await realpath(rootDir))) fail('PRODUCT_REPOSITORY_ROOT_REQUIRED');
  const commit = git(['rev-parse', 'HEAD']).trim();
  const source = await collectProductInventory(rootDir);
  if (!c.inputs.artifact.length || !c.inputs.configuration.length) fail('ARTIFACT_CONFIGURATION_INPUTS_REQUIRED');
  const artifact = await hashes(rootDir, c.inputs.artifact);
  const configuration = await hashes(rootDir, c.inputs.configuration);
  return {
    productId: c.productId,
    revision: hash({ commit, source }),
    commit,
    artifactDigest: hash(artifact),
    configurationDigest: hash(configuration),
  };
}

const c = JSON.parse(await readFile(path.join(root, `${kit}/contract.json`), 'utf8'));
const b1 = await currentBinding(root, c);
const b2 = await currentBinding(root, c);
const stable = hash(b1) === hash(b2) && b1.revision === b2.revision;
console.log(JSON.stringify({ binding1: b1, binding2: b2, stable }, null, 2));
