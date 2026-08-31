import { formatRelativeTime, absoluteLabel, type TimePrecision } from '@/utils/format';
import { Hint } from '@/components/ui/tooltip';

// `precision: minute` drops seconds/milliseconds for cron-derived instants (recurring jobs), where a
// sub-minute figure is noise. Defaults to the exact shape everywhere else.
//
// Two layouts for the same pair of facts:
//   'absolute' (default) — "2026-05-25 13:10 (in 10 minutes)", both visible.
//   'relative'           — "in 10 minutes", with the timestamp on hover. For columns read as
//                          "when, roughly?" (a recurring job's next/last run), where the exact
//                          instant is the follow-up question rather than the answer.
export function RelativeTime({
  date,
  precision = 'exact',
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

  return (
    <span>
      {absolute} <span className="text-muted-foreground">({formatRelativeTime(date)})</span>
    </span>
  );
}
