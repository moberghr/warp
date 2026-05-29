import type { ReactNode } from 'react';

export type LifecycleEventKind =
  | 'created'
  | 'enqueued'
  | 'scheduled'
  | 'processing'
  | 'progress'
  | 'completed'
  | 'failed'
  | 'deleted';

export interface LifecycleEvent {
  kind: LifecycleEventKind;
  /** Display label (e.g. "Created", "Failed"). Falls back to capitalised kind. */
  label?: string;
  /** Timestamp string. Pass already-formatted ("2026-05-21 13:05 · 3m ago"). */
  when: string;
  /** Message body — can include <code> via ReactNode. */
  message?: ReactNode;
  /** Visually de-emphasised (future / pending event). */
  future?: boolean;
}

export interface LifecycleCardProps {
  events: LifecycleEvent[];
  /** Optional footer slot (e.g. "View all →"). */
  footer?: ReactNode;
}

function capitalise(s: string) {
  return s.charAt(0).toUpperCase() + s.slice(1);
}

export function LifecycleCard({ events, footer }: LifecycleCardProps) {
  const count = events.length;

  return (
    <div className="warp-inner-card">
      <div className="warp-lc-head">
        <span className="warp-card-label">Lifecycle</span>
        <span className="count">
          {count} event{count === 1 ? '' : 's'}
        </span>
      </div>
      <div className="warp-lc-scroll">
        <div className="warp-timeline">
          {events.map((e, i) => (
            <div
              key={i}
              className={`warp-tl-item ${e.kind}${e.future ? ' future' : ''}`}
            >
              <div className="warp-tl-row">
                <span className={`warp-tl-kind ${e.kind}`}>{e.label ?? capitalise(e.kind)}</span>
                <span className="warp-tl-when">{e.when}</span>
              </div>
              {e.message && <div className="warp-tl-msg">{e.message}</div>}
            </div>
          ))}
        </div>
      </div>
      {footer && <div className="warp-lc-foot">{footer}</div>}
    </div>
  );
}
