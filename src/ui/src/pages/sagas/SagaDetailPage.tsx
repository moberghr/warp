import { useState, useEffect } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import axios from 'axios';
import { useQueryClient } from '@tanstack/react-query';
import { Panel, PanelHeader } from '@/components/v2/Panel';
import { Button } from '@/components/ui/button';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { usePageStore } from '@/stores/page';
import { useSagaDetail, useSagaActivity } from '@/api/hooks/useSagas';
import { queryScopes } from '@/lib/queryClient';
import * as api from '@/api';

export default function SagaDetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const qc = useQueryClient();
  const [showConfirm, setShowConfirm] = useState(false);
  const [confirmInput, setConfirmInput] = useState('');
  const [forcing, setForcing] = useState(false);
  const [forceError, setForceError] = useState<string | null>(null);

  // Live updates arrive through the central realtime invalidation (the 'sagas'
  // scope is invalidated on JobFinalized / MessageEnqueued bursts).
  const detailQuery = useSagaDetail(id);
  const activityQuery = useSagaActivity(id);

  const saga = detailQuery.data ?? null;
  const activity = activityQuery.data ?? null;

  // A 404 mid-view means another operator force-completed the saga or it completed
  // naturally during the polling window. Show a friendly message, not a generic error.
  const gone =
    axios.isAxiosError(detailQuery.error) && detailQuery.error.response?.status === 404;
  const error = forceError
    ?? ((detailQuery.error && !gone) || activityQuery.error ? 'Unable to load saga' : null);

  useEffect(() => {
    if (!saga) {
      usePageStore.getState().set({ title: 'Saga', subtitle: undefined });
      return;
    }
    usePageStore.getState().set({
      title: shortName(saga.type),
      subtitle: saga.correlationKey,
      right: (
        <Button variant="destructive" size="sm" aria-haspopup="dialog" onClick={() => setShowConfirm(true)}>
          Force complete
        </Button>
      ),
    });
  }, [saga]);

  useEffect(() => {
    return () => usePageStore.getState().reset();
  }, []);

  const forceComplete = async () => {
    if (!saga || confirmInput !== saga.correlationKey) return;
    setForcing(true);
    try {
      await api.forceCompleteSaga(saga.id);
      void qc.invalidateQueries({ queryKey: queryScopes.sagas });
      navigate('/sagas');
    } catch {
      setForceError('Unable to force-complete saga');
      setForcing(false);
    }
  };

  const copy = (text: string) => {
    void navigator.clipboard.writeText(text);
  };

  if (gone) {
    return (
      <div className="flex flex-col gap-3 py-5">
        <div>
          <Link to="/sagas" className="text-sm text-text-mute hover:underline">← Sagas</Link>
        </div>
        <Panel>
          <div className="py-8 text-center text-text-mute text-[13px]">
            This saga has completed or been removed.
          </div>
        </Panel>
      </div>
    );
  }

  if (error) return <ErrorState message={error} />;
  if (!saga || !activity) return <LoadingState />;

  return (
    <div className="flex flex-col gap-3 py-5">
      <Panel>
        <PanelHeader eyebrow="Metadata" />
        <div className="px-4 py-3">
          <dl className="grid grid-cols-[140px_1fr] gap-x-4 gap-y-2 text-[13px]">
            <dt className="warp-eyebrow text-text-mute">Type</dt>
            <dd className="font-mono text-xs">{saga.type}</dd>
            <dt className="warp-eyebrow text-text-mute">Correlation</dt>
            <dd className="font-mono text-xs">
              {saga.correlationKey}
              <CopyButton onClick={() => copy(saga.correlationKey)} />
            </dd>
            <dt className="warp-eyebrow text-text-mute">Id</dt>
            <dd className="font-mono text-xs">
              {saga.id}
              <CopyButton onClick={() => copy(saga.id)} />
            </dd>
            <dt className="warp-eyebrow text-text-mute">Version</dt>
            <dd className="font-mono text-xs">{saga.version}</dd>
            <dt className="warp-eyebrow text-text-mute">Created</dt>
            <dd><RelativeTime date={saga.createdAt} /></dd>
            <dt className="warp-eyebrow text-text-mute">Updated</dt>
            <dd><RelativeTime date={saga.updatedAt} /></dd>
          </dl>
        </div>
      </Panel>

      <Panel>
        <PanelHeader eyebrow="State" />
        <pre className="mono m-0 max-h-96 overflow-auto bg-[color:var(--panel-2)] px-4 py-3 text-[11.5px] leading-[1.7] text-text-dim">{prettyJson(saga.stateJson)}</pre>
      </Panel>

      <Panel>
        <PanelHeader
          eyebrow="Activity"
          action={
            <span className="text-[11px] text-text-mute">
              {activity.entries.length}
              {activity.isTruncated ? ` of ${activity.totalInvocations}, most recent shown` : ''}
            </span>
          }
        />
        <div className="px-4 py-3">
          {activity.entries.length === 0 ? (
            <div className="text-center text-text-mute py-4 text-[13px]">No activity yet</div>
          ) : (
            <div className="flex flex-col gap-2">
              {activity.entries.map(entry => (
                <div key={entry.jobId} className="border-l-2 border-border pl-3 py-1">
                  <div className="flex items-baseline gap-2 text-[13px]">
                    <RelativeTime date={entry.createTime} />
                    <Link to={`/jobs/detail/${entry.jobId}`} className="font-medium text-primary hover:underline">
                      {entry.messageType}
                    </Link>
                    <StateBadge state={entry.jobState} />
                  </div>
                  {entry.logs.length > 0 && (
                    <div className="mt-1 text-xs text-text-mute flex flex-col gap-0.5">
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
        </div>
      </Panel>

      {showConfirm && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50">
          <Panel className="w-full max-w-md">
            <PanelHeader eyebrow="Force complete saga?" />
            <div className="px-4 py-3 flex flex-col gap-3">
              <p className="text-[13px] text-text-mute">
                Deletes the saga row and all its job links. In-flight messages for this correlation
                will hit <code className="font-mono text-xs">NotFoundAsync</code> — by default Failed,
                but your handler's override may differ.
              </p>
              <div className="text-[13px]">
                Type <code className="font-mono text-xs bg-panel-2 px-1 rounded">{saga.correlationKey}</code> to confirm:
              </div>
              <input
                type="text"
                className="w-full border border-border rounded-md px-2 py-1 text-sm bg-background font-mono"
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
            </div>
          </Panel>
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
    <button
      type="button"
      onClick={onClick}
      title="Copy to clipboard"
      className="ml-2 text-xs text-text-mute hover:text-foreground"
    >
      ⧉
    </button>
  );
}

function StateBadge({ state }: { state: string }) {
  const cls =
    state === 'Completed' ? 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400' :
    state === 'Failed' ? 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400' :
    state === 'Processing' ? 'bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-400' :
    'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-400';
  return <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${cls}`}>{state}</span>;
}
