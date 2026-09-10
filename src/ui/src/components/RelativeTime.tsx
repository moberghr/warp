import { absoluteLabel, formatDateTimeExact, type TimePrecision } from '@/utils/format';
import { useCountdownLabel, useRelativeLabel } from '@/hooks/useTicking';
import { Hint } from '@/components/ui/tooltip';

// Precision defaults to `second`: milliseconds are three digits of noise in a list column, and the
// full instant is always one hover away, so nothing is actually lost. `minute` drops seconds too, for
// cron-derived instants (recurring jobs) that are only ever minute-aligned. `exact` renders the
// milliseconds inline, for a surface where sub-second ordering is the thing being read.
//
// Two layouts for the same pair of facts:
//   'absolute' (default) — "2026-05-25 13:10:42 (in 10 minutes)", both visible, exact instant on hover.
//   'relative'           — "in 10 minutes", with the timestamp on hover. For columns read as
//                          "when, roughly?" (a recurring job's next/last run), where the exact
//                          instant is the follow-up question rather than the answer.
//
// `tense` picks which formatter fills the relative slot, orthogonal to the layout:
//   'past'      (default) — Luxon's relative label, which flips sign on its own.
//   'countdown'           — for an instant still being waited on (a scheduled job, a retry's next
//                           attempt, a webhook's next delivery). Past due reads "due now" rather
//                           than "3 seconds ago", which would claim a run that has not happened, and
//                           reaching zero asks the page to refetch (see lib/dueSignal).
export function RelativeTime({
  date,
  precision = 'second',
  display = 'absolute',
  tense = 'past',
}: {
  date: string;
  precision?: TimePrecision;
  display?: 'absolute' | 'relative';
  tense?: 'past' | 'countdown';
}) {
  const absolute = absoluteLabel(date, precision);

  if (display === 'relative') {
    return (
      <Hint text={absolute}>
        <span className="decoration-dotted underline-offset-4 hover:underline">
          <RelativeLabel date={date} tense={tense} />
        </span>
      </Hint>
    );
  }

  // A native `title` rather than <Hint>: this renders in every row of every list, so a 100-row jobs
  // page would otherwise mount ~200 Base UI tooltip roots purely to reveal three digits. At `exact`
  // the milliseconds are already on screen and there is nothing left to reveal.
  return (
    <span title={precision === 'exact' ? undefined : formatDateTimeExact(date)}>
      {absolute} <span className="text-muted-foreground">(<RelativeLabel date={date} tense={tense} />)</span>
    </span>
  );
}

// The ticking label on its own, for call sites that already own the surrounding markup — a cell that
// wraps the timestamp in its own link and tooltip, where a second <Hint> would nest triggers.
// It is a component rather than a `formatRelativeTime` call because only a component can subscribe
// to the clock (lib/clockTick); a bare function renders once and then goes stale.
export function RelativeLabel({ date, tense = 'past' }: { date: string; tense?: 'past' | 'countdown' }) {
  return <>{tense === 'countdown' ? <Countdown date={date} /> : <Elapsed date={date} />}</>;
}

// Split in two so each branch calls exactly one hook unconditionally — the countdown carries a
// cross-zero effect the elapsed label has no use for. Keeping the subscription down here in a leaf
// also means a tick re-renders the text and nothing else: the absolute label and its `title` are
// computed once by the parent, which the clock never touches.
function Elapsed({ date }: { date: string }) {
  return <>{useRelativeLabel(date)}</>;
}

function Countdown({ date }: { date: string }) {
  return <>{useCountdownLabel(date)}</>;
}
