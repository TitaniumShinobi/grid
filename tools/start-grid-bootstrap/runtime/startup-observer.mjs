import { buildStartupReceipt, validateStartupEvent, writeStartupReceipt } from './startup-events.mjs';

const CONTRACT = 'life-startup-event/v1';
const VERSION = '1.0.0';

export class StartupObserver {
  #events = [];
  #active = new Map();

  constructor({ startup_id, product_id, receipt_directory }) {
    if (!startup_id || !product_id || !receipt_directory) throw new TypeError('startup_id, product_id, and receipt_directory are required');
    this.startup_id = startup_id;
    this.product_id = product_id;
    this.receipt_directory = receipt_directory;
  }

  begin(stage_id, stage_class, required = true, started_at = new Date().toISOString()) {
    if (this.#active.has(stage_id)) throw new Error(`stage already active: ${stage_id}`);
    this.#active.set(stage_id, { stage_id, stage_class, required, started_at });
    return {
      complete: (details = {}) => this.#finish(stage_id, 'COMPLETED', details),
      skip: (details = {}) => this.#finish(stage_id, 'SKIPPED', details),
      fail: (details = {}) => this.#finish(stage_id, 'FAILED', details),
    };
  }

  emit(event) {
    const normalized = validateStartupEvent({
      contract: CONTRACT,
      contract_version: VERSION,
      state_generation: null,
      registration_generation: null,
      state_snapshot_id: null,
      completed_at: null,
      cache_hit: null,
      rebuild_reason: null,
      progress_units: { status: 'INDETERMINATE', completed: 0, total: null },
      dependencies: [],
      error: null,
      readiness: null,
      receipt_ref: null,
      ...event,
      startup_id: this.startup_id,
      product_id: this.product_id,
    });
    this.#events.push(normalized);
    return normalized;
  }

  finish(readiness, verification, options = {}) {
    if (this.#active.size) throw new Error(`active stages remain: ${[...this.#active.keys()].join(', ')}`);
    const receipt = buildStartupReceipt(this.#events, readiness, verification, {
      product_id: this.product_id,
      ...options,
    });
    const path = writeStartupReceipt(receipt, this.receipt_directory);
    return { receipt, path };
  }

  #finish(stage_id, status, details) {
    const active = this.#active.get(stage_id);
    if (!active) throw new Error(`stage is not active: ${stage_id}`);
    this.#active.delete(stage_id);
    return this.emit({
      ...active,
      status,
      completed_at: details.completed_at || new Date().toISOString(),
      cache_hit: details.cache_hit ?? null,
      rebuild_reason: details.rebuild_reason ?? null,
      dependencies: details.dependencies || [],
      error: details.error || null,
      readiness: details.readiness || null,
      receipt_ref: details.receipt_ref || null,
      progress_units: details.progress_units || { status: 'INDETERMINATE', completed: 0, total: null },
    });
  }
}
