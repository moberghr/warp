import { describe, it, expect, beforeAll } from 'vitest';
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
  it('renders cron-derived instants to the minute, without seconds or milliseconds', async () => {
    renderAt('/recurring');

    // 'Inventory Sync' is enabled with both a next and a last execution in the fixtures.
    const row = await rowFor('Inventory Sync');

    // Both stamps present at minute precision; the anchored shape rejects a seconds component.
    expect(within(row).getAllByText(minuteShape).length).toBe(2);
  });

  it('the raw cron is a tooltip trigger that reveals its plain-English reading on hover', async () => {
    renderAt('/recurring');

    const row = await rowFor('Inventory Sync');
    const trigger = within(row).getByText('*/15 * * * *');

    // The raw expression stays the display; the description only appears on hover.
    expect(screen.queryByText('Every 15 minutes')).toBeNull();

    fireEvent.pointerEnter(trigger);
    fireEvent.mouseOver(trigger);
    fireEvent.focus(trigger);

    expect(await screen.findByText('Every 15 minutes')).toBeTruthy();
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

    expect(lastRunLinks.length).toBe(2); // the timestamp and the Last Result badge
    expect(within(lastRunLinks[0]).getByText(minuteShape)).toBeTruthy();
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

    // The timestamp still shows, and explains on hover why it is not a link. The row holds two
    // minute stamps (next + last execution); the LAST cell is the one carrying the note.
    const stamp = within(row).getAllByText(minuteShape).at(-1)!;
    hover(stamp);
    expect(await screen.findByText('The job for this run has been cleaned up')).toBeTruthy();
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
