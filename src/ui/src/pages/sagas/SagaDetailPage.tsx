import { useState, useEffect, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import axios from 'axios';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { useRefreshKey } from '@/hooks/useRefreshKey';
import { useRealtimeRefetch } from '@/hooks/useRealtimeRefetch';
import type { SagaDetail, SagaActivityResponse } from '@/types';
import * as api from '@/api';
import { Hint } from '@/components/ui/tooltip';

export default function SagaDetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [saga, setSaga] = useState<SagaDetail | null>(null);
  const [activity, setActivity] = useState<SagaActivityResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [gone, setGone] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);
  const [confirmInput, setConfirmInput] = useState('');
  const [forcing, setForcing] = useState(false);
  const refreshKey = useRefreshKey();

  const fetchData = useCallback(async () => {
    if (!id) return;
    try {
      const [s, a] = await Promise.all([api.getSagaById(id), api.getSagaActivity(id)]);
      setSaga(s);
      setActivity(a);
      setError(null);
      setGone(false);
    } catch (e) {
      // A 404 mid-view means another operator force-completed the saga or it completed
      // naturally during the polling window. Show a friendly message, not a generic error.
      if (axios.isAxiosError(e) && e.response?.status === 404) {
        setGone(true);
        setError(null);
        return;
      }
      setError('Unable to load saga');
    }
  }, [id]);

  useEffect(() => {
    fetchData();
  }, [refreshKey, fetchData]);

  // Live updates via the SignalR push bus: every routed message arrival or job
  // finalization is a candidate event for the saga's state moving. 30s safety net
  // catches anything the push channel misses (and matches other detail pages).
  useRealtimeRefetch(['JobFinalized', 'MessageEnqueued'], fetchData);

  const forceComplete = async () => {
    if (!saga || confirmInput !== saga.correlationKey) return;
    setForcing(true);
    try {
      await api.forceCompleteSaga(saga.id);
      navigate('/sagas');
    } catch {
      setError('Unable to force-complete saga');
      setForcing(false);
    }
  };

  const copy = (text: string) => {
    void navigator.clipboard.writeText(text);
  };

  if (gone) {
    return (
      <div>
        <div className="mb-4">
          <Link to="/sagas" className="text-sm text-muted-foreground hover:underline">← Sagas</Link>
        </div>
        <Card>
          <CardContent className="py-8 text-center text-muted-foreground">
            This saga has completed or been removed.
          </CardContent>
        </Card>
      </div>
    );
  }

  if (error) return <ErrorState message={error} />;
  if (!saga || !activity) return <LoadingState />;

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <div>
          <Link to="/sagas" className="text-sm text-muted-foreground hover:underline">← Sagas</Link>
          <h1 className="text-2xl font-bold">{shortName(saga.type)} <span className="font-mono text-base text-muted-foreground">/ {saga.correlationKey}</span></h1>
        </div>
        <Button variant="destructive" size="sm" onClick={() => setShowConfirm(true)}>
          Force complete
        </Button>
      </div>

      <Card className="mb-4">
        <CardHeader><CardTitle className="text-base">Metadata</CardTitle></CardHeader>
        <CardContent className="space-y-1 text-sm">
          <div><span className="text-muted-foreground inline-block w-32">Type</span><span className="font-mono text-xs">{saga.type}</span></div>
          <div>
            <span className="text-muted-foreground inline-block w-32">Correlation</span>
            <span className="font-mono text-xs">{saga.correlationKey}</span>
            <CopyButton onClick={() => copy(saga.correlationKey)} />
          </div>
          <div>
            <span className="text-muted-foreground inline-block w-32">Id</span>
            <span className="font-mono text-xs">{saga.id}</span>
            <CopyButton onClick={() => copy(saga.id)} />
          </div>
          <div><span className="text-muted-foreground inline-block w-32">Version</span><span className="font-mono text-xs">{saga.version}</span></div>
          <div><span className="text-muted-foreground inline-block w-32">Created</span><RelativeTime date={saga.createdAt} /></div>
          <div><span className="text-muted-foreground inline-block w-32">Updated</span><RelativeTime date={saga.updatedAt} /></div>
        </CardContent>
      </Card>

      <Card className="mb-4">
        <CardHeader><CardTitle className="text-base">State</CardTitle></CardHeader>
        <CardContent>
          <pre className="text-xs font-mono bg-muted/50 rounded-md p-3 overflow-auto max-h-96">{prettyJson(saga.stateJson)}</pre>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">
            Activity ({activity.entries.length}
            {activity.isTruncated ? ` of ${activity.totalInvocations}, most recent shown` : ''})
          </CardTitle>
        </CardHeader>
        <CardContent>
          {activity.entries.length === 0 ? (
            <div className="text-center text-muted-foreground py-4 text-sm">No activity yet</div>
          ) : (
            <div className="space-y-2">
              {activity.entries.map(entry => (
                <div key={entry.jobId} className="border-l-2 border-muted pl-3 py-1">
                  <div className="flex items-baseline gap-2 text-sm">
                    <RelativeTime date={entry.createTime} />
                    <Link to={`/detail/${entry.jobId}`} className="font-medium text-primary hover:underline">
                      {entry.messageType}
                    </Link>
                    <StateBadge state={entry.jobState} />
                  </div>
                  {entry.logs.length > 0 && (
                    <div className="mt-1 text-xs text-muted-foreground space-y-0.5">
                      {entry.logs.slice(-5).map(log => (
                        <div key={log.id}>
                          <span className="inline-block w-16 text-xs">{log.eventType}</span>
                          <span>{log.message}</span>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              ))}
            </div>
          )}
        </CardContent>
      </Card>

      {showConfirm && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50">
          <Card className="w-full max-w-md">
            <CardHeader><CardTitle>Force complete saga?</CardTitle></CardHeader>
            <CardContent className="space-y-3">
              <p className="text-sm text-muted-foreground">
                Deletes the saga row and all its job links. In-flight messages for this correlation
                will hit <code className="font-mono text-xs">NotFoundAsync</code> — by default Failed,
                but your handler's override may differ.
              </p>
              <div className="text-sm">
                Type <code className="font-mono text-xs bg-muted px-1 rounded">{saga.correlationKey}</code> to confirm:
              </div>
              <input
                type="text"
                className="w-full border rounded-md px-2 py-1 text-sm bg-background font-mono"
                value={confirmInput}
                onChange={(e) => setConfirmInput(e.target.value)}
                autoFocus
              />
              <div className="flex justify-end gap-2">
                <Button variant="ghost" onClick={() => { setShowConfirm(false); setConfirmInput(''); }}>Cancel</Button>
                <Button
                  variant="destructive"
                  disabled={confirmInput !== saga.correlationKey || forcing}
                  onClick={forceComplete}
                >
                  {forcing ? 'Completing…' : 'Force complete'}
                </Button>
              </div>
            </CardContent>
          </Card>
        </div>
      )}
    </div>
  );
}

function shortName(assemblyQualifiedName: string): string {
  const typeName = assemblyQualifiedName.split(',')[0];
  return typeName.split('.').pop() ?? typeName;
}

function prettyJson(json: string): string {
  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
}

function CopyButton({ onClick }: { onClick: () => void }) {
  return (
    <Hint text="Copy to clipboard">
      <button
        type="button"
        onClick={onClick}
        aria-label="Copy to clipboard"
        className="ml-2 text-xs text-muted-foreground hover:text-foreground"
      >
        ⧉
      </button>
    </Hint>
  );
}

function StateBadge({ state }: { state: string }) {
  const cls =
    state === 'Completed' ? 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400' :
    state === 'Failed' ? 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400' :
    state === 'Processing' ? 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400' :
    'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-400';
  return <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${cls}`}>{state}</span>;
}
