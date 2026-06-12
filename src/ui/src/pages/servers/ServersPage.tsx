import { useEffect } from 'react';
import { Link } from 'react-router-dom';
import { Panel, PanelHeader } from '@/components/v2/Panel';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { formatBytes, serverStatusDotColor, isServerStale } from '@/utils/format';
import { RelativeTime } from '@/components/RelativeTime';
import { LoadingState, ErrorState } from '@/components/PageState';
import { usePageStore } from '@/stores/page';
import { Pause, Play } from 'lucide-react';
import type { ServerModel } from '@/types';
import { useServers, usePauseServer, useResumeServer } from '@/api/hooks/useServers';

export default function ServersPage() {
  const query = useServers();
  const pause = usePauseServer();
  const resume = useResumeServer();

  useEffect(() => {
    usePageStore.getState().set({
      title: 'Servers',
      subtitle: 'Connected worker servers and their status',
    });
    return () => usePageStore.getState().reset();
  }, []);

  const handleTogglePause = (server: ServerModel) => {
    if (server.pausedAt) {
      resume.mutate(server.id);
    } else {
      pause.mutate(server.id);
    }
  };

  if (query.error) return <ErrorState message={(query.error as Error).message} />;
  if (!query.data) return <LoadingState />;

  const servers = query.data;

  return (
    <div className="flex flex-col gap-3 py-5">
      {servers.length === 0 ? (
        <Panel>
          <div className="py-10 text-center text-[13px] text-text-mute">
            No servers connected
          </div>
        </Panel>
      ) : (
        servers.map((server) => (
          <Panel key={server.id}>
            <PanelHeader
              eyebrow={
                <span className="flex items-center gap-2">
                  <span className={`inline-block w-2 h-2 rounded-full ${serverStatusDotColor(server.lastHeartbeatTime, server.pausedAt)}`} />
                  <Link to={`/servers/${server.id}`} className="text-primary hover:underline normal-case tracking-normal text-[13px] font-medium">
                    {server.serverName}
                  </Link>
                  {server.pausedAt && <Badge variant="outline" className="text-amber-600 border-amber-300">Paused</Badge>}
                  {isServerStale(server.lastHeartbeatTime) && <Badge variant="outline" className="text-red-600 border-red-300">Inactive</Badge>}
                </span>
              }
              action={
                <div className="flex items-center gap-4 text-[12.5px] text-text-mute">
                  <span>{server.serviceCount} workers</span>
                  <span>CPU: {server.cpuUsagePercent != null ? `${server.cpuUsagePercent}%` : 'N/A'}</span>
                  <span>Mem: {server.memoryWorkingSetBytes != null ? formatBytes(server.memoryWorkingSetBytes) : 'N/A'}</span>
                  <span>Started <RelativeTime date={server.startedTime} /></span>
                  <span>Heartbeat <RelativeTime date={server.lastHeartbeatTime} /></span>
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={pause.isPending || resume.isPending}
                    onClick={(e) => { e.preventDefault(); handleTogglePause(server); }}
                    title={server.pausedAt ? 'Resume server (takes effect on next heartbeat, ~3s)' : 'Pause server (takes effect on next heartbeat, ~3s)'}
                    aria-label={server.pausedAt ? 'Resume server' : 'Pause server'}
                  >
                    {server.pausedAt ? <Play className="h-4 w-4" /> : <Pause className="h-4 w-4" />}
                  </Button>
                </div>
              }
            />
          </Panel>
        ))
      )}
    </div>
  );
}
