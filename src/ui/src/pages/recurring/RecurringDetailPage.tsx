import { useState, useEffect, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Button } from '@/components/ui/button';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { StateBadge } from '@/components/StateBadge';
import { Pagination } from '@/components/Pagination';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { usePersistedPageSize } from '@/hooks/usePersistedPageSize';
import { shortType, formatDateTimeMinute, shortId } from '@/utils/format';
import { describeCron } from './recurringModel';
import { Hint } from '@/components/ui/tooltip';
import { decodeUrlSafeId } from '@/lib/urlSafeId';
import type { RecurringJobDetailModel, RecurringJobHistoryModel, PagedList } from '@/types';
import * as api from '@/api';

export default function RecurringDetailPage() {
  // The route segment is the URL-safe base64 of the definition's NAME (the identity the API keys
  // on — see lib/urlSafeId). A hand-mangled segment decodes to garbage and the API answers 404.
  const { id } = useParams<{ id: string }>();
  const name = id ? decodeUrlSafeId(id) : undefined;
  const navigate = useNavigate();
  const [detail, setDetail] = useState<RecurringJobDetailModel | null>(null);
  const [jobs, setJobs] = useState<PagedList<RecurringJobHistoryModel> | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = usePersistedPageSize();
  const [pending, setPending] = useState<'trigger' | 'delete' | 'disable' | null>(null);

  useEffect(() => {
    if (name) {
      api.getRecurringJob(name).then(setDetail).catch(() => setError('Unable to load recurring job'));
    }
  }, [name]);

  const fetchJobs = useCallback(async () => {
    if (name) {
      try {
        const result = await api.getRecurringJobJobs(name, page, pageSize);
        setJobs(result);
      } catch {
        // Jobs loading failure is non-critical
      }
    }
  }, [name, page, pageSize]);

  useEffect(() => { fetchJobs(); }, [fetchJobs]);

  if (error) return <ErrorState message={error} />;
  if (!detail) return <LoadingState />;

  const cronDescription = describeCron(detail.cron);

  // Enable is reversible and harmless — apply immediately. Disable on a production
  // recurring job (billing sweep, reconciliation, etc.) is potentially an outage, so it
  // goes through the confirm dialog like the other destructive actions.
  const handleEnable = async () => {
    await api.enableRecurringJob(detail.name);
    setDetail(await api.getRecurringJob(detail.name));
  };

  const handleDisable = async () => {
    await api.disableRecurringJob(detail.name);
    setDetail(await api.getRecurringJob(detail.name));
  };

  const handleTrigger = async () => {
    await api.triggerRecurringJob(detail.name);
    setDetail(await api.getRecurringJob(detail.name));
    fetchJobs();
  };

  const handleDelete = async () => {
    await api.deleteRecurringJob(detail.name);
    navigate('/recurring');
  };

  return (
    <div>
      {/* Header */}
      <div className="flex items-center gap-4 mb-6">
        <h1 className="text-2xl font-bold">{detail.name}</h1>
        <Hint text={cronDescription}>
          <span className="font-mono text-sm bg-muted px-2 py-1 rounded">{detail.cron}</span>
        </Hint>
        {/* The detail page has the room, so the plain-English reading is shown outright here rather
            than hidden behind the tooltip the list has to rely on. */}
        {cronDescription && <span className="text-sm text-muted-foreground">{cronDescription}</span>}
        {detail.disabledAt ? (
          <span className="inline-flex items-center rounded-full bg-orange-100 px-2.5 py-0.5 text-xs font-medium text-orange-800 dark:bg-orange-900/30 dark:text-orange-400">
            Disabled <RelativeTime date={detail.disabledAt} precision="minute" />
          </span>
        ) : (
          <span className="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800 dark:bg-green-900/30 dark:text-green-400">Enabled</span>
        )}
        <div className="flex-1" />
        <Button
          variant="outline"
          size="sm"
          onClick={detail.disabledAt ? handleEnable : () => setPending('disable')}
        >
          {detail.disabledAt ? 'Enable' : 'Disable'}
        </Button>
        <Button variant="outline" size="sm" onClick={() => setPending('trigger')}>Trigger</Button>
        <Button variant="destructive" size="sm" onClick={() => setPending('delete')}>Delete</Button>
      </div>

      <ConfirmDialog
        open={pending !== null}
        onOpenChange={(open) => !open && setPending(null)}
        title={
          pending === 'delete' ? `Remove recurring job "${detail.name}"?`
            : pending === 'trigger' ? `Trigger "${detail.name}" now?`
              : pending === 'disable' ? `Disable recurring job "${detail.name}"?`
                : ''
        }
        description={
          pending === 'delete'
            ? 'The recurring job definition and its history will be removed permanently. Any future scheduled runs will not fire. This cannot be undone.'
            : pending === 'trigger'
              ? 'A job will be enqueued immediately, on top of the normal cron schedule.'
              : pending === 'disable'
                ? 'No new runs will fire until the job is re-enabled. In-flight jobs from earlier runs continue to completion. Disable a job that drives critical work (reconciliation, billing, etc.) only with the same care as deleting it.'
                : null
        }
        confirmLabel={pending === 'delete' ? 'Remove' : pending === 'disable' ? 'Disable' : 'Trigger'}
        variant={pending === 'delete' || pending === 'disable' ? 'destructive' : 'default'}
        onConfirm={() => {
          if (pending === 'trigger') handleTrigger();
          else if (pending === 'delete') handleDelete();
          else if (pending === 'disable') handleDisable();
          setPending(null);
        }}
      />

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Left column */}
        <div className="space-y-4">
          {/* Details */}
          <Card>
            <CardHeader className="pb-2"><CardTitle className="text-sm">Details</CardTitle></CardHeader>
            <CardContent className="space-y-2 text-sm">
              <div><span className="text-muted-foreground">Type:</span> {shortType(detail.type)}</div>
              <div><span className="text-muted-foreground">Created:</span> {formatDateTimeMinute(detail.createdAt)}</div>
              {detail.updatedAt && <div><span className="text-muted-foreground">Updated:</span> {formatDateTimeMinute(detail.updatedAt)}</div>}
              <div>
                <span className="text-muted-foreground">Next Execution:</span>{' '}
                {detail.disabledAt ? (
                  <Hint text="Disabled — this recurring job will not execute"><span>—</span></Hint>
                ) : detail.nextExecution ? (
                  <RelativeTime date={detail.nextExecution} precision="minute" />
                ) : (
                  'N/A'
                )}
              </div>
              <div>
                <span className="text-muted-foreground">Last Execution:</span>{' '}
                {detail.lastExecution ? <RelativeTime date={detail.lastExecution} precision="minute" /> : 'Never'}
              </div>
            </CardContent>
          </Card>

          {/* Payload */}
          {detail.message && (
            <Card>
              <CardHeader className="pb-2"><CardTitle className="text-sm">Payload</CardTitle></CardHeader>
              <CardContent>
                <pre className="text-xs bg-muted p-3 rounded-md overflow-auto max-h-40">{detail.message}</pre>
              </CardContent>
            </Card>
          )}
        </div>

        {/* Right column: Execution History */}
        <div className="space-y-4">
          <Card>
            <CardHeader className="pb-2">
              <CardTitle className="text-sm">Execution History</CardTitle>
            </CardHeader>
            <CardContent>
              {jobs && jobs.items.length > 0 ? (
                <>
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>Job</TableHead>
                        <TableHead>State</TableHead>
                        <TableHead>Executed</TableHead>
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {jobs.items.map((entry, idx) => (
                        <TableRow key={entry.jobId ?? `log-${idx}`}>
                          <TableCell className="font-mono text-xs">
                            {entry.jobExists && entry.jobId ? (
                              <Link to={`/detail/${entry.jobId}`} className="text-primary hover:underline">{shortId(entry.jobId)}</Link>
                            ) : entry.jobId ? (
                              <span className="text-muted-foreground">{shortId(entry.jobId)}</span>
                            ) : (
                              <span className="text-muted-foreground">-</span>
                            )}
                          </TableCell>
                          <TableCell>
                            {entry.skipped ? (
                              <span className="inline-flex items-center rounded-full bg-orange-100 px-2 py-0.5 text-xs font-medium text-orange-800 dark:bg-orange-900/30 dark:text-orange-400">Skipped</span>
                            ) : entry.currentState != null ? (
                              // The outcome survives the job row (ExpirationCleanup stamps FinalState
                              // before deleting it), so an old firing still reports its result — with
                              // a "(cleaned up)" note, since there is no detail page left to open.
                              <span className="inline-flex items-center gap-1">
                                <StateBadge state={entry.currentState} />
                                {!entry.jobExists && (
                                  <Hint text="The job for this run has been cleaned up">
                                    <span className="text-xs text-muted-foreground">(cleaned up)</span>
                                  </Hint>
                                )}
                              </span>
                            ) : (
                              <span className="text-xs text-muted-foreground">Cleaned up</span>
                            )}
                          </TableCell>
                          <TableCell className="text-sm"><RelativeTime date={entry.createdAt} precision="minute" /></TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                  <Pagination
                    page={page}
                    pageCount={jobs.pageCount}
                    onPageChange={setPage}
                    pageSize={pageSize}
                    onPageSizeChange={(size) => { setPageSize(size); setPage(0); }}
                  />
                </>
              ) : (
                <p className="text-muted-foreground text-sm py-4 text-center">No executions yet</p>
              )}
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  );
}
