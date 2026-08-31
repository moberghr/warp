import { formatRelativeTime, formatDateTimeExact, formatDateTimeMinute } from '@/utils/format';

// `minute` drops seconds/milliseconds for cron-derived instants (recurring jobs),
// where a sub-minute figure is noise. Defaults to the exact shape everywhere else.
export function RelativeTime({ date, precision = 'exact' }: { date: string; precision?: 'exact' | 'minute' }) {
  const absolute = precision === 'minute' ? formatDateTimeMinute(date) : formatDateTimeExact(date);

  return (
    <span>
      {absolute} <span className="text-muted-foreground">({formatRelativeTime(date)})</span>
    </span>
  );
}
