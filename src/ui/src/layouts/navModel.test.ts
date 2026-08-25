import { describe, expect, it } from 'vitest';
import {
  PANEL_MARGIN,
  PANEL_WIDTH,
  badgesForItem,
  clampPanelLeft,
  isNavItemActive,
  matchPathFor,
  resolveActiveLocation,
  filterNavTargets,
  flattenNavTargets,
  gateGroups,
  rollUpBadges,
  COUNTER_FAMILY_GROUP,
  NAV_GROUPS,
  type NavGroup,
  type NavItem,
} from './navModel';
import type { DashboardStatistics, WarpAddonsInfo } from '@/types';

const Icon = () => null;

function item(to: string, label: string): NavItem {
  return { to, label, icon: Icon };
}

const messages = item('/messages/enqueued', 'Messages');
const batches = item('/batches/processing', 'Batches');
const recurring = item('/recurring', 'Recurring');
const applications = item('/applications', 'Applications');
const dashboard = item('/', 'Dashboard');
const jobs = item('/jobs/enqueued', 'Jobs');

const workloads: NavGroup = { label: 'Workloads', items: [messages, batches, recurring] };
const health: NavGroup = { label: 'Health', items: [applications] };

function stats(overrides: Partial<DashboardStatistics> = {}): DashboardStatistics {
  return {
    total: 0, pending: 0, scheduled: 0, created: 0, completed: 0, failed: 0, processing: 0,
    servers: 0, awaiting: 0, deleted: 0, batchesProcessing: 0, batchesAwaiting: 0,
    batchesDeleted: 0, batchesCompleted: 0, batchesFailed: 0, messagesEnqueued: 0,
    messagesProcessing: 0, messagesCompleted: 0, messagesFailed: 0, messages: 0,
    totalSucceeded: 0, totalFailed: 0, totalDeleted: 0, totalCreated: 0,
    adapterRecordsDropped: 0, endpointRecordsDropped: 0, clientRecordsDropped: 0,
    batches: 0, databaseConnection: null,
    ...overrides,
  };
}

describe('matchPathFor', () => {
  it('reduces a deep route to its first segment', () => {
    expect(matchPathFor('/messages/enqueued')).toBe('/messages');
  });

  it('leaves a single-segment route alone', () => {
    expect(matchPathFor('/recurring')).toBe('/recurring');
  });

  it('leaves the root alone', () => {
    expect(matchPathFor('/')).toBe('/');
  });
});

describe('isNavItemActive', () => {
  it('keeps the section active on a sibling state route', () => {
    expect(isNavItemActive('/jobs/enqueued', '/jobs/failed')).toBe(true);
  });

  it('keeps the section active on a detail route', () => {
    expect(isNavItemActive('/adapters', '/adapters/billing/calls/7')).toBe(true);
  });

  it('matches the root exactly so it does not swallow every route', () => {
    expect(isNavItemActive('/', '/')).toBe(true);
    expect(isNavItemActive('/', '/counters')).toBe(false);
  });

  it('does not match a different section', () => {
    expect(isNavItemActive('/messages/enqueued', '/batches/processing')).toBe(false);
  });
});

describe('resolveActiveLocation', () => {
  it('finds the group holding the active page', () => {
    const active = resolveActiveLocation('/recurring', [dashboard, jobs], [workloads, health]);

    expect(active.group?.label).toBe('Workloads');
    expect(active.item?.label).toBe('Recurring');
  });

  it('resolves a deep route to its group', () => {
    const active = resolveActiveLocation('/messages/detail/abc', [dashboard, jobs], [workloads, health]);

    expect(active.group?.label).toBe('Workloads');
    expect(active.item?.label).toBe('Messages');
  });

  it('reports no group for a top-level item', () => {
    const active = resolveActiveLocation('/jobs/failed', [dashboard, jobs], [workloads, health]);

    expect(active.group).toBeNull();
    expect(active.item?.label).toBe('Jobs');
  });

  it('reports nothing for a route outside the nav', () => {
    const active = resolveActiveLocation('/trace/abc', [dashboard, jobs], [workloads, health]);

    expect(active.group).toBeNull();
    expect(active.item).toBeNull();
  });
});

describe('badgesForItem', () => {
  it('reads job counts', () => {
    expect(badgesForItem('/jobs/enqueued', stats({ created: 4, failed: 2 })))
      .toEqual({ pending: 4, failed: 2, neutral: null });
  });

  it('reads message counts from the message-specific fields', () => {
    expect(badgesForItem('/messages/enqueued', stats({ messages: 7, messagesFailed: 1 })))
      .toEqual({ pending: 7, failed: 1, neutral: null });
  });

  it('reads batch counts', () => {
    expect(badgesForItem('/batches/processing', stats({ batchesProcessing: 3, batchesFailed: 5 })))
      .toEqual({ pending: 3, failed: 5, neutral: null });
  });

  it('gives Applications a neutral server count that still renders at zero', () => {
    expect(badgesForItem('/applications', stats({ servers: 0 })))
      .toEqual({ pending: 0, failed: 0, neutral: 0 });
  });

  it('is empty for an item with no counts', () => {
    expect(badgesForItem('/counters', stats({ created: 9 })))
      .toEqual({ pending: 0, failed: 0, neutral: null });
  });

  it('is empty before stats have loaded', () => {
    expect(badgesForItem('/jobs/enqueued', null)).toEqual({ pending: 0, failed: 0, neutral: null });
  });
});

describe('rollUpBadges', () => {
  it('sums both counts across the group', () => {
    const rolled = rollUpBadges(workloads, stats({
      messages: 7,
      messagesFailed: 1,
      batchesProcessing: 3,
      batchesFailed: 5,
    }));

    expect(rolled).toEqual({ pending: 10, failed: 6, neutral: null });
  });

  it('sums rather than maxing, so work in several places is not under-reported', () => {
    const rolled = rollUpBadges(workloads, stats({ messages: 156, batchesProcessing: 5 }));

    expect(rolled.pending).toBe(161);
  });

  it('drops the neutral server count — a fleet size is not outstanding work', () => {
    expect(rollUpBadges(health, stats({ servers: 12 })))
      .toEqual({ pending: 0, failed: 0, neutral: null });
  });

  it('is empty when the group has no counted items', () => {
    const group: NavGroup = { label: 'Runtime', items: [item('/sagas', 'Sagas')] };

    expect(rollUpBadges(group, stats({ created: 9, failed: 9 })))
      .toEqual({ pending: 0, failed: 0, neutral: null });
  });
});

describe('clampPanelLeft', () => {
  it('leaves a left-hand trigger where it is', () => {
    expect(clampPanelLeft(240, 1600)).toBe(240);
  });

  it('pulls a right-hand trigger back inside the shell', () => {
    // Health sits well past the point where a 520px panel still fits at 1280.
    expect(clampPanelLeft(880, 1280)).toBe(1280 - PANEL_WIDTH - PANEL_MARGIN);
  });

  it('keeps the left margin when the trigger sits at the very edge', () => {
    expect(clampPanelLeft(0, 1600)).toBe(PANEL_MARGIN);
  });

  it('falls back to the left margin when the shell cannot hold a panel', () => {
    expect(clampPanelLeft(400, 400)).toBe(PANEL_MARGIN);
  });
});

describe('gateGroups', () => {
  const addons: WarpAddonsInfo = {
    concurrency: false, rateLimits: false, push: false, sagas: false, adapters: false,
    endpoints: false, client: false, webhooks: false, applications: false, slo: false,
  };

  it('drops Traffic and Runtime entirely when no addons are registered', () => {
    const groups = gateGroups(NAV_GROUPS, addons);

    expect(groups.map((x) => x.label)).toEqual(['Workloads', 'Health']);
  });

  it('keeps only the Core pages in Health when SLOs are off', () => {
    const groups = gateGroups(NAV_GROUPS, addons);
    const health = groups.find((x) => x.label === 'Health');

    expect(health?.items.map((x) => x.label)).toEqual(['Issues', 'Counters', 'Applications']);
  });

  it('renders a group as soon as one of its items is registered', () => {
    const groups = gateGroups(NAV_GROUPS, { ...addons, sagas: true });
    const runtime = groups.find((x) => x.label === 'Runtime');

    expect(runtime?.items.map((x) => x.label)).toEqual(['Sagas']);
  });

  it('shows only Core pages before the addon probe has answered', () => {
    const groups = gateGroups(NAV_GROUPS, null);

    expect(groups.map((x) => x.label)).toEqual(['Workloads', 'Health']);
  });

  it('never gates an ungated group', () => {
    const groups = gateGroups(NAV_GROUPS, addons);
    const workloads = groups.find((x) => x.label === 'Workloads');

    expect(workloads?.items).toHaveLength(4);
  });
});

describe('flattenNavTargets', () => {
  it('tags each destination with the group it is reached through', () => {
    const targets = flattenNavTargets([dashboard], [workloads], [item('/ext', 'Extension')]);

    expect(targets.map((x) => [x.item.label, x.group])).toEqual([
      ['Dashboard', null],
      ['Messages', 'Workloads'],
      ['Batches', 'Workloads'],
      ['Recurring', 'Workloads'],
      ['Extension', null],
    ]);
  });
});

describe('filterNavTargets', () => {
  const targets = flattenNavTargets([dashboard, jobs], NAV_GROUPS, []);

  it('returns everything for an empty query', () => {
    expect(filterNavTargets(targets, '   ')).toHaveLength(targets.length);
  });

  it('ranks a label prefix above a label hit elsewhere', () => {
    const labels = filterNavTargets(targets, 'e').map((x) => x.item.label);

    expect(labels[0]).toBe('Endpoints');
  });

  it('finds a page by its group name', () => {
    const labels = filterNavTargets(targets, 'runtime').map((x) => x.item.label);

    expect(labels).toEqual(['Concurrency', 'Rate Limits', 'Sagas']);
  });

  it('finds a page by its hint', () => {
    const labels = filterNavTargets(targets, 'cron').map((x) => x.item.label);

    expect(labels).toEqual(['Recurring']);
  });

  it('matches a hint regardless of its casing', () => {
    // The hint was compared raw against a lowercased query, so any hint carrying a
    // capital silently stopped matching. Every shipped hint is lowercase, which is
    // exactly why nothing caught it.
    const target = { item: item('/x', 'Thing'), group: null, ...{} };
    target.item.hint = 'HTTP inbound';

    expect(filterNavTargets([target], 'http').map((x) => x.item.label)).toEqual(['Thing']);
  });

  it('is case-insensitive', () => {
    expect(filterNavTargets(targets, 'COUNTERS').map((x) => x.item.label)).toEqual(['Counters']);
  });

  it('returns nothing when nothing matches', () => {
    expect(filterNavTargets(targets, 'zzzz')).toEqual([]);
  });
});

describe('COUNTER_FAMILY_GROUP', () => {
  it('lets the palette reach a counter family that has no nav entry of its own', () => {
    // Queues was a top-level page until 6.0; the name a user learned must still find something.
    const targets = flattenNavTargets([dashboard, jobs], [...NAV_GROUPS, COUNTER_FAMILY_GROUP], []);
    const hits = filterNavTargets(targets, 'queues');

    expect(hits.map((x) => [x.item.label, x.item.to, x.group])).toEqual([['Queues', '/counters/queues', 'Counters']]);
  });

  it('is not part of the rendered nav', () => {
    expect(NAV_GROUPS.map((x) => x.label)).not.toContain('Counters');
  });
});
