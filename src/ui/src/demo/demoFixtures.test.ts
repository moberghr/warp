import { describe, it, expect } from 'vitest';
import { demoAdapterDetails, demoAdapterCalls } from './data/adapters';

// Demo mode is what the docs screenshots and every "show me the dashboard" walkthrough run against,
// so a dangling reference in the fixtures reads as a broken product: the page renders "Unable to
// load call detail — make sure the Warp backend is running", which is exactly the wrong diagnosis.
// These guards check the fixtures are internally consistent, since nothing else does.
describe('demo adapter fixtures', () => {
  it('every clickable recent call has a detail fixture', () => {
    const referenced = Object.values(demoAdapterDetails).flatMap((adapter) =>
      adapter.recentCalls.map((call) => call.id));

    const missing = referenced.filter((id) => !(id in demoAdapterCalls));

    expect(referenced.length).toBeGreaterThan(0);
    expect(missing).toEqual([]);
  });

  it('each detail fixture agrees with its list row on the shared fields', () => {
    // A detail page that contradicts the row it was opened from is worse than a missing one: it
    // teaches the reader to distrust the numbers.
    const mismatches: string[] = [];

    for (const adapter of Object.values(demoAdapterDetails)) {
      for (const row of adapter.recentCalls) {
        const detail = demoAdapterCalls[row.id];
        if (!detail) {
          continue;
        }

        const compare: [string, unknown, unknown][] = [
          ['adapterName', detail.adapterName, adapter.name],
          ['operation', detail.operation, row.operation],
          ['groupName', detail.groupName, row.groupName],
          ['durationMs', detail.durationMs, row.durationMs],
          ['attempts', detail.attempts, row.attempts],
          ['outcome', detail.outcome, row.outcome],
          ['statusCode', detail.statusCode, row.statusCode],
          ['correlationId', detail.correlationId, row.correlationId],
          ['tagsJson', detail.tagsJson, row.tagsJson],
        ];

        for (const [field, detailValue, rowValue] of compare) {
          if (detailValue !== rowValue) {
            mismatches.push(`${row.id}.${field}: detail=${String(detailValue)} row=${String(rowValue)}`);
          }
        }
      }
    }

    expect(mismatches).toEqual([]);
  });

  it('no detail fixture is orphaned', () => {
    // An entry nothing links to is dead weight that drifts out of shape unnoticed.
    const referenced = new Set(Object.values(demoAdapterDetails).flatMap((adapter) =>
      adapter.recentCalls.map((call) => call.id)));

    expect(Object.keys(demoAdapterCalls).filter((id) => !referenced.has(id))).toEqual([]);
  });
});
