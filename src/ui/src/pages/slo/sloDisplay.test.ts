import { describe, expect, it } from 'vitest';
import { SloState } from '@/types/slo';
import { showsSloValues, sloValueDisplay } from './sloDisplay';

describe('sloValueDisplay', () => {
  it('reports not-evaluated before the first evaluation, whatever the state', () => {
    expect(sloValueDisplay({ evaluated: false, state: SloState.Healthy })).toBe('not-evaluated');
    expect(showsSloValues({ evaluated: false, state: SloState.Healthy })).toBe(false);
  });

  it('hides the values of an evaluated NoData objective, so it never renders as healthy', () => {
    expect(sloValueDisplay({ evaluated: true, state: SloState.NoData })).toBe('no-data');
    expect(showsSloValues({ evaluated: true, state: SloState.NoData })).toBe(false);
  });

  it.each([SloState.Healthy, SloState.Warning, SloState.Breaching, SloState.Acknowledged])(
    'shows the values of an evaluated objective in state %s',
    (state) => {
      expect(sloValueDisplay({ evaluated: true, state })).toBe('values');
      expect(showsSloValues({ evaluated: true, state })).toBe(true);
    },
  );
});
