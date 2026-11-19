import { useMemo } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import type { ColumnDef } from '@tanstack/react-table';
import { LoadingState, ErrorState } from '@/components/PageState';
import { DataTable } from '@/components/DataTable';
import { RelativeTime } from '@/components/RelativeTime';
import * as api from '@/api';
import { ErrorSource, ErrorGroupKind, ErrorGroupStatus } from '@/types/issues';
import type { ErrorGroupSummary } from '@/types/issues';
import { SourceBadge, StatusChip, IssueFlags } from './shared';
import { PageHeading } from '@/components/PageHeading';
import { DASHBOARD_LOCALE } from '@/utils/format';
import { queryKeys } from '@/lib/queryClient';
import { parseIssuesParams, writeIssuesParams, type IssuesFilters } from './issuesModel';

// Issues (§8.29): errors grouped by fingerprint across all four sources — job exceptions, endpoint
// 5xx, adapter failures, and client-side errors — with a resolution workflow. By default the list
// shows only Unresolved exception groups; the "show 4xx" toggle folds in StatusCode groups.
export default function IssuesPage() {
  // The filters live in the URL (a shared link or a refresh keeps them); a change replaces the history
  // entry so Back leaves the page rather than stepping through every click.
  const [searchParams, setSearchParams] = useSearchParams();
  const { source, status, application, show4xx } = useMemo(() => parseIssuesParams(searchParams), [searchParams]);
  const set = (patch: Partial<IssuesFilters>) =>
    setSearchParams((prev) => writeIssuesParams(prev, { ...parseIssuesParams(prev), ...patch }), { replace: true });
  const setSource = (next: ErrorSource | null) => set({ source: next });
  const setStatus = (next: ErrorGroupStatus) => set({ status: next });

  const query = useQuery({
    queryKey: ['issues', 'list', source, status, application] as const,
    queryFn: () => api.getIssues({ source: source ?? undefined, status, application: application ?? undefined }),
    refetchInterval: 30_000,
  });

  const applicationsQuery = useQuery({
    queryKey: queryKeys.applications,
    queryFn: () => api.getApplications(),
    staleTime: 60_000,
  });
  const applications = useMemo(() => {
    const names = Array.isArray(applicationsQuery.data) ? applicationsQuery.data.map((x) => x.name) : [];

    // A linked application the roster no longer lists stays selectable, so the select shows what the list is filtered by.
    return application !== null && !names.includes(application) ? [...names, application] : names;
  }, [applicationsQuery.data, application]);

  // The list requests exception kinds by default; StatusCode (4xx) groups are filtered out client-side
  // unless the toggle is on. Keeps the volume knob on the client without a second query.
  const items = useMemo(() => {
    const all = query.data?.items ?? [];
    if (show4xx) {
      return all;
    }

    return all.filter((x) => x.kind !== ErrorGroupKind.StatusCode);
  }, [query.data, show4xx]);

  const columns = useMemo<ColumnDef<ErrorGroupSummary>[]>(
    () => [
      {
        accessorKey: 'exceptionType',
        header: 'Issue',
        cell: ({ row }) => (
          <Link to={`/issues/${encodeURIComponent(row.original.fingerprint)}`} className="block min-w-0 hover:underline">
            <span className="flex items-center gap-2">
              <span className="font-mono text-xs text-primary truncate">{row.original.exceptionType}</span>
              <IssueFlags isNew={row.original.isNew} isRegressed={row.original.isRegressed} />
            </span>
            <span className="block truncate text-sm">{row.original.title}</span>
            {row.original.culprit && <span className="block truncate text-xs text-muted-foreground">{row.original.culprit}</span>}
          </Link>
        ),
      },
      {
        accessorKey: 'source',
        header: 'Source',
        cell: ({ row }) => <SourceBadge source={row.original.source} />,
        meta: { headerClassName: 'w-24' },
      },
      {
        accessorKey: 'count',
        header: 'Events',
        cell: ({ row }) => row.original.count.toLocaleString(DASHBOARD_LOCALE),
        meta: { headerClassName: 'text-right w-24', cellClassName: 'text-right tabular-nums' },
      },
      {
        accessorKey: 'lastSeenAt',
        header: 'Last seen',
        cell: ({ row }) => (
          <span className="text-sm text-muted-foreground">
            <RelativeTime date={row.original.lastSeenAt} />
          </span>
        ),
        meta: { headerClassName: 'w-56' },
      },
      {
        accessorKey: 'status',
        header: 'Status',
        cell: ({ row }) => <StatusChip status={row.original.status} />,
        meta: { headerClassName: 'w-28' },
      },
    ],
    [],
  );

  if (query.isError) return <ErrorState message="Unable to load issues" />;

  return (
    <div>
      <div className="mb-4">
        <PageHeading className="">Issues</PageHeading>
        <p className="text-sm text-muted-foreground mt-1">
          Errors grouped by fingerprint across jobs, endpoints, outbound calls, and the browser — with a resolution workflow.
        </p>
      </div>

      <div className="flex flex-wrap items-center gap-2 mb-3">
        <div className="inline-flex rounded-md border p-0.5 text-xs">
          <FilterButton active={source === null} onClick={() => setSource(null)}>All</FilterButton>
          <FilterButton active={source === ErrorSource.Job} onClick={() => setSource(ErrorSource.Job)}>Job</FilterButton>
          <FilterButton active={source === ErrorSource.Endpoint} onClick={() => setSource(ErrorSource.Endpoint)}>Endpoint</FilterButton>
          <FilterButton active={source === ErrorSource.Adapter} onClick={() => setSource(ErrorSource.Adapter)}>Adapter</FilterButton>
          <FilterButton active={source === ErrorSource.Client} onClick={() => setSource(ErrorSource.Client)}>Client</FilterButton>
        </div>

        <div className="inline-flex rounded-md border p-0.5 text-xs">
          <FilterButton active={status === ErrorGroupStatus.Unresolved} onClick={() => setStatus(ErrorGroupStatus.Unresolved)}>Unresolved</FilterButton>
          <FilterButton active={status === ErrorGroupStatus.Resolved} onClick={() => setStatus(ErrorGroupStatus.Resolved)}>Resolved</FilterButton>
          <FilterButton active={status === ErrorGroupStatus.Ignored} onClick={() => setStatus(ErrorGroupStatus.Ignored)}>Ignored</FilterButton>
        </div>

        <select
          aria-label="Application"
          className="h-7 rounded-md border border-border bg-background px-2 text-xs"
          value={application ?? ''}
          onChange={(e) => set({ application: e.target.value || null })}
        >
          <option value="">All applications</option>
          {applications.map((x) => <option key={x} value={x}>{x}</option>)}
        </select>

        <label className="inline-flex items-center gap-1.5 text-xs text-muted-foreground ml-1 cursor-pointer select-none">
          <input type="checkbox" checked={show4xx} onChange={(e) => set({ show4xx: e.target.checked })} className="h-3.5 w-3.5 rounded border" />
          Show 4xx
        </label>
      </div>

      {query.isLoading || !query.data ? (
        <LoadingState />
      ) : (
        <DataTable
          columns={columns}
          data={items}
          emptyMessage="No issues match this filter."
          getRowId={(row) => row.fingerprint}
        />
      )}
    </div>
  );
}

function FilterButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`px-2.5 py-1 rounded font-medium transition-colors ${active ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:text-foreground'}`}
    >
      {children}
    </button>
  );
}
