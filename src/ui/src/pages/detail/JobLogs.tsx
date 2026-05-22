import { Panel, PanelHeader } from '@/components/v2/Panel';
import { formatDateTime } from '@/utils/format';
import type { JobLogModel } from '@/types';

interface JobLogsProps {
  jobId: string;
  logs: JobLogModel[];
}

export function JobLogs({ jobId, logs }: JobLogsProps) {
  if (logs.length === 0) {
    return null;
  }

  const jobContext = JSON.stringify({ jobId });

  return (
    <div data-warp-slot="detail.logs" data-warp-context={jobContext} key={`logs-${jobId}`}>
      <Panel>
        <PanelHeader eyebrow={`Handler output · ${logs.length}`} />
        <div className="mono max-h-[70vh] space-y-1 overflow-auto bg-[color:var(--panel-2)] px-4 py-3 text-[11.5px] leading-[1.65]">
          {logs.map(log => {
            const tone =
              log.level === 'Error' ? 'text-warp-red' : log.level === 'Warning' ? 'text-warp-amber' : 'text-text-dim';

            return (
              <div key={log.id} className={`flex gap-2 ${tone}`}>
                <span className="shrink-0 text-text-mute">{formatDateTime(log.timestamp)}</span>
                <span className="shrink-0 uppercase tracking-[0.06em] text-text-mute">[{log.level}]</span>
                <span className="break-all">{log.message}</span>
              </div>
            );
          })}
        </div>
      </Panel>
    </div>
  );
}
