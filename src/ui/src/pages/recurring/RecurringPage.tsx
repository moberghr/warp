import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { ArrowLeftRight } from 'lucide-react';
import type { ColumnDef } from '@tanstack/react-table';
import { Button } from '@/components/ui/button';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { RelativeTime } from '@/components/RelativeTime';
import { StateBadge } from '@/components/StateBadge';
import { Hint } from '@/components/ui/tooltip';
import { LoadingState, ErrorState } from '@/components/PageState';
import { DataTable } from '@/components/DataTable';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import { usePersistedCronDisplay } from '@/hooks/usePersistedCronDisplay';
import {
  useRecurringList,
  useEnableRecurringJob,
  useDisableRecurringJob,
  useTriggerRecurringJob,
  useDeleteRecurringJob,
} from '@/api/hooks/useRecurring';
import { formatRelativeTime, absoluteLabel } from '@/utils/format';
import { encodeUrlSafeId } from '@/lib/urlSafeId';
import { lastRunHref, isLastRunCleanedUp, isLastRunOutcomeUnknown, describeCron } from './recurringModel';
import type { RecurringJobModel } from '@/types';
import { PageHeading } from '@/components/PageHeading';

// A definition is addressed by its name — the identity the API keys on. It travels URL-encoded in
// the detail route because a name may hold '/' and spaces.
type RecurringPending =
  | { kind: 'trigger'; name: string }
  | { kind: 'remove'; name: string };

export default function RecurringPage() {
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePersistedPageSize();
  const [cronDisplay, setCronDisplay] = usePersistedCronDisplay();
  const showCronExpression = cronDisplay === 'expression';
  const { data, isLoading, isError } = useRecurringList(page, pageSize);

  const enable = useEnableRecurringJob();
  const disable = useDisableRecurringJob();
  const trigger = useTriggerRecurringJob();
  const remove = useDeleteRecurringJob();

  const [pending, setPending] = useState<RecurringPending | null>(null);

  const runPending = () => {
    if (!pending) return;
    if (pending.kind === 'trigger') trigger.mutate(pending.name);
    else remove.mutate(pending.name);
    setPending(null);
  };

  const columns = useMemo<ColumnDef<RecurringJobModel>[]>(
    () => [
      {
        accessorKey: 'name',
        header: 'Name',
        cell: ({ row }) => (
          <Link
            to={`/recurring/${encodeUrlSafeId(row.original.name)}`}
            className="font-medium text-primary hover:underline"
          >
            {row.original.name}
          </Link>
        ),
      },
      {
        accessorKey: 'cron',
        // The header names what the column is CURRENTLY showing and doubles as the switch between
        // the two halves of a schedule, so it can never label the wrong one. Default is the
        // plain-English reading: scanning a list of schedules is this column's job, and "Every 15
        // minutes" answers it faster than "*/15 * * * *". Whichever half is not in the cell is the
        // hover hint, so nothing is ever hidden — and someone who thinks in cron flips it once and
        // the choice sticks.
        header: () => (
          <Hint text={showCronExpression ? 'Show schedules in plain English' : 'Show the raw cron expressions'}>
            <button
              type="button"
              onClick={() => setCronDisplay(showCronExpression ? 'description' : 'expression')}
              className="inline-flex items-center gap-1 hover:text-foreground"
            >
              {showCronExpression ? 'Cron' : 'Schedule'}
              <ArrowLeftRight className="h-3 w-3 opacity-60" />
            </button>
          </Hint>
        ),
        // An unparseable cron has no reading, so it always falls back to the expression itself.
        cell: ({ row }) => {
          const description = describeCron(row.original.cron);

          if (!description) {
            return <span className="block truncate font-mono text-xs">{row.original.cron}</span>;
          }

          // The hint carries BOTH halves, not just the hidden one: the column is a fixed width, so a
          // long reading ellipsizes, and hovering has to be able to give back the full text of what
          // is on screen as well as the half that is not.
          const hint = (
            <span className="block">
              {description}
              <br />
              <span className="font-mono">{row.original.cron}</span>
            </span>
          );

          return (
            <Hint text={hint}>
              <span
                className={`block truncate text-xs decoration-dotted underline-offset-4 hover:underline ${showCronExpression ? 'font-mono' : ''}`}
              >
                {showCronExpression ? row.original.cron : description}
              </span>
            </Hint>
          );
        },
        // A FIXED width, so switching halves cannot resize the column — and with it the whole table.
        // The two halves are naturally different widths ("*/15 * * * *" against "Every 15 minutes"),
        // and under `table-auto` that difference propagated: flipping the switch moved every other
        // column too (Name 149→166px, Actions 278→310px). Reserving the space once keeps the layout
        // still, at the cost of ellipsizing a very long reading — which the hint gives back.
        meta: { headerClassName: 'w-72', cellClassName: 'w-72 max-w-72' },
      },
      {
        accessorKey: 'type',
        header: 'Type',
        cell: ({ row }) => row.original.type.split(',')[0].split('.').pop(),
      },
      {
        id: 'status',
        header: 'Status',
        cell: ({ row }) =>
          row.original.disabledAt ? (
            <span className="inline-flex items-center rounded-full bg-orange-100 px-2 py-0.5 text-xs font-medium text-orange-800 dark:bg-orange-900/30 dark:text-orange-400">
              Disabled
            </span>
          ) : (
            <span className="inline-flex items-center rounded-full bg-green-100 px-2 py-0.5 text-xs font-medium text-green-800 dark:bg-green-900/30 dark:text-green-400">
              Enabled
            </span>
          ),
      },
      {
        accessorKey: 'nextExecution',
        header: 'Next Execution',
        cell: ({ row }) =>
          row.original.disabledAt ? (
            <Hint text="Disabled — this recurring job will not execute">
              <span className="text-sm text-muted-foreground">—</span>
            </Hint>
          ) : row.original.nextExecution ? (
            <span className="text-sm">
              <RelativeTime date={row.original.nextExecution} precision="minute" display="relative" />
            </span>
          ) : (
            <span className="text-sm">N/A</span>
          ),
      },
      {
        accessorKey: 'lastExecution',
        header: 'Last Execution',
        // Shown whether or not the definition is disabled — the run happened, and the scheduler's
        // skip branch deliberately never advances LastExecution. The timestamp links to that run's
        // job (the Last Result badge links to the same place) unless the job has been cleaned up.
        cell: ({ row }) => {
          const { lastExecution } = row.original;

          if (!lastExecution) {
            return <span className="text-sm text-muted-foreground">Never</span>;
          }

          // One hint per element, never a hint inside a hint: the timestamp and the swept-job note
          // share this cell's single tooltip rather than nesting two triggers.
          const swept = isLastRunCleanedUp(row.original) || isLastRunOutcomeUnknown(row.original);
          const stamp = absoluteLabel(lastExecution, 'minute');
          const hint = swept ? `${stamp} · the job for this run has been cleaned up` : stamp;
          const href = lastRunHref(row.original);

          return (
            <Hint text={hint}>
              {href ? (
                <Link to={href} className="text-sm text-primary hover:underline">
                  {formatRelativeTime(lastExecution)}
                </Link>
              ) : (
                <span className="text-sm text-muted-foreground decoration-dotted underline-offset-4 hover:underline">
                  {formatRelativeTime(lastExecution)}
                </span>
              )}
            </Hint>
          );
        },
      },
      {
        id: 'lastResult',
        header: 'Last Result',
        cell: ({ row }) => {
          const { hasLastRun, lastState } = row.original;

          if (!hasLastRun) {
            return <span className="text-sm text-muted-foreground">—</span>;
          }

          // No outcome to show: swept before ExpirationCleanup started stamping FinalState. The null
          // check also narrows lastState for the badge below.
          if (lastState == null) {
            return <span className="text-xs text-muted-foreground">Cleaned up</span>;
          }

          const href = lastRunHref(row.original);

          if (!href) {
            // Outcome preserved, job row gone — show the result, say why it is not clickable.
            return (
              <Hint text="The job for this run has been cleaned up">
                <span className="inline-flex items-center gap-1">
                  <StateBadge state={lastState} />
                  <span className="text-xs text-muted-foreground">(cleaned up)</span>
                </span>
              </Hint>
            );
          }

          return (
            <Link to={href}>
              <StateBadge state={lastState} />
            </Link>
          );
        },
      },
      {
        id: 'actions',
        header: 'Actions',
        cell: ({ row }) => (
          <>
            {row.original.disabledAt ? (
              <Button variant="ghost" size="sm" onClick={() => enable.mutate(row.original.name)}>
                Enable
              </Button>
            ) : (
              <Button variant="ghost" size="sm" onClick={() => disable.mutate(row.original.name)}>
                Disable
              </Button>
            )}
            <Button
              variant="ghost"
              size="sm"
              onClick={() => setPending({ kind: 'trigger', name: row.original.name })}
            >
              Trigger
            </Button>
            <Button
              variant="ghost"
              size="sm"
              className="text-destructive"
              onClick={() => setPending({ kind: 'remove', name: row.original.name })}
            >
              Remove
            </Button>
          </>
        ),
        meta: { headerClassName: 'text-right', cellClassName: 'text-right' },
      },
    ],
    [enable, disable, showCronExpression, setCronDisplay],
  );

  if (isError) return <ErrorState message="Unable to load recurring jobs" />;
  if (isLoading || !data) return <LoadingState />;

  return (
    <div>
      <PageHeading className="mb-4">Recurring Jobs</PageHeading>

      <DataTable
        columns={columns}
        data={data.items}
        emptyMessage="No recurring jobs found"
        getRowId={(row) => row.name}
        pagination={{
          page,
          pageSize,
          pageCount: data.pageCount,
          onPageChange: setPage,
          onPageSizeChange: (size) => {
            setPageSize(size);
            setPage(0);
          },
        }}
      />

      <ConfirmDialog
        open={pending !== null}
        onOpenChange={(open) => !open && setPending(null)}
        title={pending?.kind === 'remove' ? `Remove recurring job "${pending.name}"?` : pending ? `Trigger "${pending.name}" now?` : ''}
        description={
          pending?.kind === 'remove'
            ? 'The recurring job definition and its history will be removed permanently. Any future scheduled runs will not fire. This cannot be undone.'
            : pending
              ? 'A job will be enqueued immediately, on top of the normal cron schedule. Any in-progress concurrency or rate-limit guards on the underlying handler still apply.'
              : null
        }
        confirmLabel={pending?.kind === 'remove' ? 'Remove' : 'Trigger'}
        variant={pending?.kind === 'remove' ? 'destructive' : 'default'}
        onConfirm={runPending}
      />
    </div>
  );
}
