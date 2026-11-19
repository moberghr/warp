import { describe, expect, it } from 'vitest';
import type { ApplicationDeployModel } from '@/types/applications';
import { withPreviousVersion } from './deployModel';

function deploy(version: string, firstSeenAt: string, environment: string | null = 'prod', application = 'api'): ApplicationDeployModel {
  return { id: `${application}-${environment}-${version}`, application, version, environment, firstSeenAt, instanceId: 'i', instanceName: null };
}

describe('withPreviousVersion', () => {
  it('pairs each deploy with the one before it in the same environment, newest first', () => {
    const rows = withPreviousVersion([
      deploy('1.0.0', '2026-11-10T10:00:00Z'),
      deploy('1.1.0', '2026-11-12T10:00:00Z'),
      deploy('1.0.5', '2026-11-11T10:00:00Z', 'staging'),
      deploy('1.2.0', '2026-11-14T10:00:00Z'),
    ]);

    expect(rows.map((x) => [x.version, x.previousVersion])).toEqual([
      ['1.2.0', '1.1.0'],
      ['1.1.0', '1.0.0'],
      ['1.0.5', null],
      ['1.0.0', null],
    ]);
  });

  it('treats a missing environment as one environment of its own', () => {
    const rows = withPreviousVersion([deploy('2.0.0', '2026-11-12T10:00:00Z', null), deploy('1.0.0', '2026-11-10T10:00:00Z', null)]);

    expect(rows[0].previousVersion).toBe('1.0.0');
  });

  it('never pairs deploys of different applications', () => {
    const rows = withPreviousVersion([deploy('2.0.0', '2026-11-12T10:00:00Z', 'prod', 'api'), deploy('1.0.0', '2026-11-10T10:00:00Z', 'prod', 'worker')]);

    expect(rows.map((x) => x.previousVersion)).toEqual([null, null]);
  });
});
