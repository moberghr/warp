import { Link } from 'react-router-dom';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { StateBadge } from '@/components/StateBadge';
import { DASHBOARD_LOCALE, shortId, shortType } from '@/utils/format';
import type { ContinuationInfo } from '@/types';

interface FlowCardProps {
  jobId?: string;
  traceId?: string | null;
  parentJob?: ContinuationInfo | null;
  spawnedByJob?: ContinuationInfo | null;
  continuations: ContinuationInfo[];
  /** Total continuations; the list holds at most the first 50. */
  continuationCount: number;
  spawnedJobs: ContinuationInfo[];
  /** Total spawned jobs; the list holds at most the first 50. */
  spawnedJobCount: number;
}

function kindLabel(kind: number | null | undefined) {
  if (kind === 3) return 'Batch';
  if (kind === 2) return 'Message';
  return 'Job';
}

function FlowRow({ item }: { item: ContinuationInfo }) {
  return (
    <TableRow>
      <TableCell className="font-mono text-xs">
        <Link to={`/detail/${item.id}`} className="text-primary hover:underline">
          {shortId(item.id)}
        </Link>
      </TableCell>
      <TableCell>{shortType(item.type)}</TableCell>
      <TableCell>{item.handlerType ? shortType(item.handlerType) : '—'}</TableCell>
      <TableCell className="text-xs">{kindLabel(item.kind)}</TableCell>
      <TableCell><StateBadge state={item.currentState} /></TableCell>
    </TableRow>
  );
}

// Shown under a list the server capped, so a fan-out of thousands doesn't read as complete.
function TruncationRow({ shown, total }: { shown: number; total: number }) {
  if (total <= shown) return null;

  return (
    <TableRow>
      <TableCell colSpan={5} className="text-xs text-muted-foreground py-1.5">
        Showing the first {shown} of {total.toLocaleString(DASHBOARD_LOCALE)}.
      </TableCell>
    </TableRow>
  );
}

function GroupHeader({ label, count }: { label: string; count: number }) {
  return (
    <TableRow className="bg-muted/50">
      <TableCell colSpan={5} className="text-xs font-semibold text-muted-foreground py-1.5">
        {label} ({count})
      </TableCell>
    </TableRow>
  );
}

export function FlowCard({ jobId, traceId, parentJob, spawnedByJob, continuations, continuationCount, spawnedJobs, spawnedJobCount }: FlowCardProps) {
  const hasTable = parentJob || spawnedByJob || continuations.length > 0 || spawnedJobs.length > 0;
  const hasContent = traceId || hasTable;
  if (!hasContent) return null;

  return (
    <Card>
      <CardHeader className="pb-2"><CardTitle className="text-sm">Flow</CardTitle></CardHeader>
      <CardContent className="space-y-3 text-sm">
        {traceId && (
          <div>
            <span className="text-muted-foreground">Trace:</span>{' '}
            <Link to={`/trace/${traceId.replace(/-/g, '')}${jobId ? `/${jobId}` : ''}`} className="text-primary hover:underline font-mono text-xs">{traceId.replace(/-/g, '')}</Link>
          </div>
        )}
        {hasTable && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>ID</TableHead>
                <TableHead>Type</TableHead>
                <TableHead>Handler</TableHead>
                <TableHead>Kind</TableHead>
                <TableHead>State</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {parentJob && (
                <>
                  <GroupHeader label="Parent" count={1} />
                  <FlowRow item={parentJob} />
                </>
              )}
              {spawnedByJob && (
                <>
                  <GroupHeader label="Spawned by" count={1} />
                  <FlowRow item={spawnedByJob} />
                </>
              )}
              {continuations.length > 0 && (
                <>
                  <GroupHeader label="Continuations" count={continuationCount} />
                  {continuations.map(c => <FlowRow key={c.id} item={c} />)}
                  <TruncationRow shown={continuations.length} total={continuationCount} />
                </>
              )}
              {spawnedJobs.length > 0 && (
                <>
                  <GroupHeader label="Spawned jobs" count={spawnedJobCount} />
                  {spawnedJobs.map(c => <FlowRow key={c.id} item={c} />)}
                  <TruncationRow shown={spawnedJobs.length} total={spawnedJobCount} />
                </>
              )}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}
