import { describe, expect, it } from 'vitest';
import type { DeployVerdictCheck, DeployVerdictCheckState, DeployVerdictModel, DeployVerdictValue } from '@/types/applications';
import {
  checkStateLabel,
  coverageLabel,
  describeDeploy,
  orderChecks,
  shouldLoadVerdict,
  verdictClass,
  verdictLabel,
  verdictPanel,
  verdictRefetchMs,
} from './verdictModel';

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
    threshold: null,
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
  it.each<[DeployVerdictValue, string]>([
    ['pass', 'Pass'],
    ['warn', 'Warn'],
    ['fail', 'Fail'],
    ['pending', 'Pending'],
    ['noData', 'No data'],
  ])('labels the %s verdict "%s"', (verdict, label) => {
    expect(verdictLabel(verdict)).toBe(label);
  });

  it.each<[DeployVerdictCheckState, string]>([
    ['healthy', 'Healthy'],
    ['warning', 'Warning'],
    ['breaching', 'Breaching'],
    ['acknowledged', 'Acknowledged'],
    ['noData', 'No data'],
  ])('labels the %s check state "%s"', (state, label) => {
    expect(checkStateLabel(state)).toBe(label);
  });

  it('passes an unknown token through rather than rendering nothing', () => {
    expect(verdictLabel('later' as DeployVerdictValue)).toBe('later');
    expect(checkStateLabel('later' as DeployVerdictCheckState)).toBe('later');
  });

  it('colours pass, warn and fail and leaves pending / noData neutral', () => {
    expect(verdictClass('pass')).toBe('bg-green-500/15 text-green-700 dark:text-green-400');
    expect(verdictClass('warn')).toBe('bg-amber-500/15 text-amber-700 dark:text-amber-400');
    expect(verdictClass('fail')).toBe('bg-red-500/15 text-red-700 dark:text-red-400');
    expect(verdictClass('pending')).toBe('bg-muted text-muted-foreground');
    expect(verdictClass('noData')).toBe('bg-muted text-muted-foreground');
  });
});

describe('describeDeploy', () => {
  it('shows the upgrade path and the environment when known', () => {
    expect(describeDeploy({ version: '2.0.0', previousVersion: '1.9.0', environment: 'prod' })).toBe('1.9.0 → 2.0.0 (prod)');
    expect(describeDeploy({ version: '2.0.0', previousVersion: null, environment: null })).toBe('2.0.0');
  });
});

describe('verdictRefetchMs', () => {
  it('polls every 30 s while pending and every 2 min otherwise, including before the first answer', () => {
    expect(verdictRefetchMs('pending')).toBe(30_000);
    for (const verdict of ['pass', 'warn', 'fail', 'noData', undefined] as const) {
      expect(verdictRefetchMs(verdict)).toBe(120_000);
    }
  });
});

describe('coverageLabel', () => {
  it('says how many checks had data, in the singular for one', () => {
    expect(coverageLabel({ checksWithData: 2, checksTotal: 9 })).toBe('2 of 9 checks had data');
    expect(coverageLabel({ checksWithData: 0, checksTotal: 1 })).toBe('0 of 1 check had data');
    expect(coverageLabel({ checksWithData: 3, checksTotal: 3 })).toBe('3 of 3 checks had data');
  });

  it('says nothing covers the application when there are no checks', () => {
    expect(coverageLabel({ checksWithData: 0, checksTotal: 0 })).toBe('No board tile with a rule and no SLO covers this application');
  });
});

describe('verdictPanel', () => {
  const verdict = { verdict: 'pass', checksWithData: 1, checksTotal: 1 } as DeployVerdictModel;
  const failure = (status: number, data: unknown = {}) => ({ response: { status, data } });

  it('hides the card until there is an answer', () => {
    expect(verdictPanel({ data: undefined, isError: false, error: null })).toEqual({ kind: 'hidden' });
  });

  it('shows the verdict', () => {
    expect(verdictPanel({ data: verdict, isError: false, error: null })).toEqual({ kind: 'verdict', verdict, staleError: null });
  });

  it('reads a 404 as "no matching deploy", even after an earlier answer', () => {
    expect(verdictPanel({ data: undefined, isError: true, error: failure(404, { error: 'No recorded deploy…' }) })).toEqual({ kind: 'noMatch' });
    expect(verdictPanel({ data: verdict, isError: true, error: failure(404) })).toEqual({ kind: 'noMatch' });
  });

  it('shows any other failure with the server message, never hiding the card', () => {
    expect(verdictPanel({ data: undefined, isError: true, error: failure(400, { error: "'minCoverage' must be a number from 0 to 1." }) }))
      .toEqual({ kind: 'error', message: "'minCoverage' must be a number from 0 to 1." });
    expect(verdictPanel({ data: undefined, isError: true, error: failure(500) })).toEqual({ kind: 'error', message: 'Unable to load the verdict.' });
    expect(verdictPanel({ data: undefined, isError: true, error: new Error('Network Error') })).toEqual({ kind: 'error', message: 'Network Error' });
  });

  it('keeps an earlier verdict on a failed refetch and says so', () => {
    expect(verdictPanel({ data: verdict, isError: true, error: failure(500, { error: 'boom' }) }))
      .toEqual({ kind: 'verdict', verdict, staleError: 'boom' });
  });
});

describe('shouldLoadVerdict', () => {
  it('asks once a deploy is recorded, or when the deploy history failed, never for an unknown application', () => {
    expect(shouldLoadVerdict('shop', { isError: false, count: 1 })).toBe(true);
    expect(shouldLoadVerdict('shop', { isError: true, count: 0 })).toBe(true);
    expect(shouldLoadVerdict('shop', { isError: false, count: 0 })).toBe(false);
    expect(shouldLoadVerdict(undefined, { isError: true, count: 3 })).toBe(false);
  });
});
