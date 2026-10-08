import { createHash } from 'node:crypto';

function compareUtf8(left, right) {
  return Buffer.from(left, 'utf8').compare(Buffer.from(right, 'utf8'));
}

function canonicalValue(value) {
  if (Array.isArray(value)) return value.map(canonicalValue);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort(compareUtf8).map((key) => [key, canonicalValue(value[key])]));
  }
  return value;
}

export function deepFreeze(value) {
  if (!value || typeof value !== 'object' || Object.isFrozen(value)) return value;
  for (const child of Object.values(value)) deepFreeze(child);
  return Object.freeze(value);
}

export function canonicalJson(value) {
  return JSON.stringify(canonicalValue(value));
}

export function digest(value) {
  return createHash('sha256').update(canonicalJson(value), 'utf8').digest('hex');
}
