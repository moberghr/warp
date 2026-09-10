import { formatRelativeTime, absoluteLabel, formatDateTimeExact, type TimePrecision } from '@/utils/format';
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
export function RelativeTime({
  date,
  precision = 'second',
  display = 'absolute',
}: {
  date: string;
  precision?: TimePrecision;
  display?: 'absolute' | 'relative';
}) {
  const absolute = absoluteLabel(date, precision);

  if (display === 'relative') {
    return (
      <Hint text={absolute}>
        <span className="decoration-dotted underline-offset-4 hover:underline">{formatRelativeTime(date)}</span>
      </Hint>
    );
  }

  // A native `title` rather than <Hint>: this renders in every row of every list, so a 100-row jobs
  // page would otherwise mount ~200 Base UI tooltip roots purely to reveal three digits. At `exact`
  // the milliseconds are already on screen and there is nothing left to reveal.
  return (
    <span title={precision === 'exact' ? undefined : formatDateTimeExact(date)}>
      {absolute} <span className="text-muted-foreground">({formatRelativeTime(date)})</span>
    </span>
  );
}
