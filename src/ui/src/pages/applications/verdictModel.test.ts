import { describe, expect, it } from 'vitest';
import type { DeployVerdictCheck, DeployVerdictCheckState } from '@/types/applications';
import { checkStateLabel, describeDeploy, orderChecks, verdictClass, verdictLabel, verdictRefetchMs } from './verdictModel';

function check(name: string, state: DeployVerdictCheckState, source: 'tile' | 'slo' = 'tile'): DeployVerdictCheck {
  return {
    source,
    id: name.length,
    name,
    application: null,
    state,
    unacknowledgedState: null,
    acknowledged: state === 'acknowledged',
    value: null,
    baseline: null,
    target: null,
    budgetRemaining: null,
    reason: '',
  };
}

describe('orderChecks', () => {
  it('puts the worst first and keeps the server order within a state', () => {
    const ordered = orderChecks([
      check('a', 'healthy'),
      check('b', 'noData'),
      check('c', 'breaching'),
      check('d', 'acknowledged'),
      check('e', 'warning'),
      check('f', 'healthy', 'slo'),
      check('g', 'breaching', 'slo'),
    ]);

    expect(ordered.map((x) => x.name)).toEqual(['c', 'g', 'e', 'd', 'a', 'f', 'b']);
  });

  it('does not mutate its input', () => {
    const input = [check('a', 'healthy'), check('b', 'breaching')];

    orderChecks(input);

    expect(input.map((x) => x.name)).toEqual(['a', 'b']);
  });
});

describe('labels and colours', () => {
  it('labels every verdict and state', () => {
    expect(verdictLabel('noData')).toBe('No data');
    expect(verdictLabel('fail')).toBe('Fail');
    expect(checkStateLabel('acknowledged')).toBe('Acknowledged');
  });

  it('colours pass, warn and fail and leaves pending / noData neutral', () => {
    expect(verdictClass('pass')).toContain('green');
    expect(verdictClass('warn')).toContain('amber');
    expect(verdictClass('fail')).toContain('red');
    expect(verdictClass('pending')).toBe(verdictClass('noData'));
  });
});

describe('describeDeploy', () => {
  it('shows the upgrade path and the environment when known', () => {
    expect(describeDeploy({ version: '2.0.0', previousVersion: '1.9.0', environment: 'prod' })).toBe('1.9.0 → 2.0.0 (prod)');
    expect(describeDeploy({ version: '2.0.0', previousVersion: null, environment: null })).toBe('2.0.0');
  });
});

describe('verdictRefetchMs', () => {
  it('polls faster only while pending', () => {
    expect(verdictRefetchMs('pending')).toBeLessThan(verdictRefetchMs('pass'));
    expect(verdictRefetchMs(undefined)).toBe(verdictRefetchMs('fail'));
  });
});
