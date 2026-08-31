import { describe, it, expect, beforeAll, beforeEach } from 'vitest';
import { render, screen, within, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import api from '@/api/client';
import { createDemoAdapter } from '@/demo/adapter';
import RecurringPage from './RecurringPage';
import RecurringDetailPage from './RecurringDetailPage';

// Rendering coverage for the recurring surfaces, driven against the demo mock adapter. Proves the
// three behaviours the pages promise: cron-derived instants render to the MINUTE, a disabled
// definition hides its next execution but KEEPS its last one, and the last run is reachable from the
// list — except when its job row has been cleaned up, where it must not be a link.
beforeAll(() => {
  api.defaults.adapter = createDemoAdapter(false);
});

// The cron-display preference persists, so one test's flip would otherwise decide the next test's
// starting state — order-dependent and invisible until someone reorders the file.
beforeEach(() => {
  localStorage.removeItem('warp:cronDisplay');
});

function renderAt(path: string) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/recurring" element={<RecurringPage />} />
          <Route path="/recurring/:id" element={<RecurringDetailPage />} />
          <Route path="/detail/:id" element={<div>job-detail-stub</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

async function rowFor(name: string) {
  const cell = await screen.findByText(name);

  return cell.closest('tr') as HTMLTableRowElement;
}

// Testing-library matches an element's OWN text nodes, so this is the absolute stamp alone —
// RelativeTime keeps the "(x ago)" suffix in a child span. Anchored, so any seconds fail the match.
const minuteShape = /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$/;

// Relative labels are LOCALE-dependent (luxon's toRelative renders "za 9 minuta" on a Croatian
// machine) and drift while the test runs, so never assert their wording. Assert the structure
// instead: the cell holds a relative label, and the exact instant is one hover away.
const CellIndex = { nextExecution: 4, lastExecution: 5 } as const;

function cell(row: HTMLElement, index: number) {
  return row.querySelectorAll('td')[index] as HTMLElement;
}

// Tooltips are Base UI popups now, not native title attributes, so a description is only in the
// DOM while its trigger is hovered.
function hover(el: HTMLElement) {
  fireEvent.pointerEnter(el);
  fireEvent.mouseOver(el);
  fireEvent.focus(el);
}

// Detail routes key on the URL-safe base64 of the definition NAME (mirrors UrlSafeId).
function encode(name: string) {
  return btoa(name).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

describe('recurring list', () => {
  it('shows next/last execution as relative text, with the exact instant on hover', async () => {
    renderAt('/recurring');

    // 'Inventory Sync' is enabled with both a next and a last execution in the fixtures.
    const row = await rowFor('Inventory Sync');

    // "when, roughly?" is the answer in the cell; no timestamp is on screen until hovered.
    const next = cell(row, CellIndex.nextExecution);
    expect(next.textContent).not.toMatch(minuteShape);
    expect(within(row).queryByText(minuteShape)).toBeNull();

    // The hovered stamp is minute-precision — the anchored shape rejects a seconds component.
    hover(next.querySelector('[data-slot="tooltip-trigger"]') as HTMLElement);
    expect(await screen.findByText(minuteShape)).toBeTruthy();
  });

  it('shows the schedule in plain English by default, with the raw cron on hover', async () => {
    renderAt('/recurring');

    const row = await rowFor('Inventory Sync');

    // The reading is the cell; the expression is a hover away.
    expect(within(row).getByText('Every 15 minutes')).toBeTruthy();
    expect(within(row).queryByText('*/15 * * * *')).toBeNull();

    hover(within(row).getByText('Every 15 minutes'));
    expect(await screen.findByText('*/15 * * * *')).toBeTruthy();
  });

  it('the column header switches which half is shown, and the header names it', async () => {
    renderAt('/recurring');

    // Header labels what the cell currently holds, so it can never label the wrong one.
    const header = await screen.findByRole('button', { name: /Schedule/ });
    fireEvent.click(header);

    const row = await rowFor('Inventory Sync');

    expect(within(row).getByText('*/15 * * * *')).toBeTruthy();
    expect(within(row).queryByText('Every 15 minutes')).toBeNull();
    expect(screen.getByRole('button', { name: /Cron/ })).toBeTruthy();

    // Flipped, the reading becomes the hint — neither half is ever unreachable.
    hover(within(row).getByText('*/15 * * * *'));
    expect(await screen.findByText('Every 15 minutes')).toBeTruthy();
  });

  it('a reading too long for the column truncates, with the full text in the hint', async () => {
    renderAt('/recurring');

    // 'Business Hours Sync' is the verbose fixture: its reading is far wider than the column.
    const row = await rowFor('Business Hours Sync');
    const trigger = cell(row, 1).querySelector('[data-slot="tooltip-trigger"]') as HTMLElement;

    // truncate is what ellipsizes it; the hint then has to carry BOTH halves in full, since the
    // visible half is the one that got cut. (truncate is CSS-only, so the cell still holds the whole
    // string in the DOM — hence anchoring on the expression, which appears only in the popup.)
    expect(trigger.className).toContain('truncate');

    hover(trigger);

    const popup = (await screen.findByText('5 9-17 * * 1-5')).closest('[data-slot="tooltip-content"]');

    expect(popup).not.toBeNull();
    expect(popup!.textContent).toContain('At 5 minutes past the hour, between 09:00 AM and 05:59 PM, Monday through Friday');
  });

  it('reserves a fixed width for the schedule column in both modes', async () => {
    // jsdom has no layout engine, so the guard is the reserved-width class rather than a measured
    // pixel value. Without it the two halves size the column differently and flipping the switch
    // moves every other column with it (measured: Name 149→166px, Actions 278→310px).
    renderAt('/recurring');

    const scheduleHeader = (await screen.findByRole('button', { name: /Schedule/ })).closest('th')!;
    expect(scheduleHeader.className).toContain('w-72');

    fireEvent.click(screen.getByRole('button', { name: /Schedule/ }));

    expect(screen.getByRole('button', { name: /Cron/ }).closest('th')!.className).toContain('w-72');
    expect(cell(await rowFor('Inventory Sync'), 1).className).toContain('w-72');
  });

  it('remembers the choice across a remount', async () => {
    const first = renderAt('/recurring');
    fireEvent.click(await screen.findByRole('button', { name: /Schedule/ }));

    first.unmount();
    renderAt('/recurring');

    // A reading preference, not a per-visit one: someone who thinks in cron flips it once.
    expect(await screen.findByRole('button', { name: /Cron/ })).toBeTruthy();
    expect(within(await rowFor('Inventory Sync')).getByText('*/15 * * * *')).toBeTruthy();
  });

  it('a disabled definition shows no next execution but keeps its last one, linked', async () => {
    renderAt('/recurring');

    // 'Email Digest' is the disabled fixture, with a Failed last run whose job still exists.
    const row = await rowFor('Email Digest');

    expect(within(row).getByText('Disabled')).toBeTruthy();

    const dash = within(row).getByText('—');
    hover(dash);
    expect(await screen.findByText('Disabled — this recurring job will not execute')).toBeTruthy();

    const lastRunLinks = within(row)
      .getAllByRole('link')
      .filter((a) => a.getAttribute('href')?.startsWith('/detail/'));

    expect(lastRunLinks.length).toBe(2); // the relative timestamp and the Last Result badge

    // The linked cell reads relatively and reveals its exact instant on hover.
    expect(lastRunLinks[0].textContent).not.toMatch(minuteShape);
    hover(lastRunLinks[0]);
    expect(await screen.findByText(minuteShape)).toBeTruthy();
  });

  it('a cleaned-up run keeps its outcome and stops being a link', async () => {
    renderAt('/recurring');

    // 'Tax Calculation' is the monthly fixture: its job row was swept after JobExpirationTimeout,
    // but ExpirationCleanup stamped RecurringJobLog.FinalState first, so the result survives.
    const row = await rowFor('Tax Calculation');

    expect(within(row).getByText('Completed')).toBeTruthy();
    expect(within(row).getByText('(cleaned up)')).toBeTruthy();
    expect(
      within(row)
        .queryAllByRole('link')
        .filter((a) => a.getAttribute('href')?.startsWith('/detail/')).length,
    ).toBe(0);
  });

  it('a run swept before stamping existed still degrades to a bare Cleaned up', async () => {
    renderAt('/recurring');

    // 'Order Cleanup' has no FinalState — nothing can recover the outcome of a pre-upgrade sweep.
    const row = await rowFor('Order Cleanup');

    expect(within(row).getByText('Cleaned up')).toBeTruthy();
    expect(within(row).queryByText('(cleaned up)')).toBeNull();

    // The run still shows when it happened, and one combined hint carries both the exact instant
    // and why it is not a link — a single tooltip per element, never a hint inside a hint.
    hover(cell(row, CellIndex.lastExecution).querySelector('[data-slot="tooltip-trigger"]') as HTMLElement);
    expect(await screen.findByText(/the job for this run has been cleaned up/)).toBeTruthy();
    expect(
      within(row)
        .queryAllByRole('link')
        .filter((a) => a.getAttribute('href')?.startsWith('/detail/')).length,
    ).toBe(0);
  });
});

describe('recurring detail', () => {
  it('history shows a preserved outcome for a firing whose job row is gone', async () => {
    renderAt(`/recurring/${encode('Daily Report')}`);

    // The oldest fixture row has no job but a stamped Completed outcome: badge plus the note, and
    // its job id must not be a link. The two skipped rows above it stay Skipped.
    expect(await screen.findByText('Execution History')).toBeTruthy();

    const noteRow = screen.getByText('(cleaned up)').closest('tr')!;

    expect(within(noteRow).getByText('Completed')).toBeTruthy();
    expect(within(noteRow).queryAllByRole('link').length).toBe(0);

    hover(screen.getByText('(cleaned up)'));
    expect(await screen.findByText('The job for this run has been cleaned up')).toBeTruthy();
    expect(screen.getAllByText('Skipped').length).toBe(2);
  });

  it('shows the cron description outright, not just on hover', async () => {
    renderAt(`/recurring/${encode('Email Digest')}`);

    expect(await screen.findByText('At 06:00 PM, Monday through Friday')).toBeTruthy();
  });

  it('renders its instants to the minute and dashes the next execution while disabled', async () => {
    renderAt(`/recurring/${encode('Email Digest')}`);

    expect(await screen.findByText(/^Next Execution:/)).toBeTruthy();

    const dash = screen.getByText('—');
    hover(dash);
    expect(await screen.findByText('Disabled — this recurring job will not execute')).toBeTruthy();

    // Created / Updated / Last Execution / the Disabled badge — all to the minute, no seconds.
    expect(screen.getAllByText(minuteShape).length).toBeGreaterThanOrEqual(3);
  });
});
