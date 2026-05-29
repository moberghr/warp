import { Copy } from 'lucide-react';
import type { ReactNode } from 'react';

export interface PageHeaderMetaItem {
  /** Uppercase mono label (e.g. "Type", "Queue", "Created"). */
  k: string;
  /** Value — string or any node. Renders in mono. */
  v: ReactNode;
  /** Optional relative-time suffix (e.g. "· 3 minutes ago"). */
  rel?: string;
  /** When set, shows a copy button that puts this string on the clipboard. */
  copy?: string;
}

export interface PageHeaderProps {
  /** Small label rendered before the title (e.g. "Job", "Batch", "Message"). */
  kindLabel?: string;
  /** Main title — usually a short ID. Rendered in mono at display size. */
  title: ReactNode;
  /** Optional status pill rendered inline with the title. */
  pill?: ReactNode;
  /** Right-side action buttons. */
  actions?: ReactNode;
  /** Meta rail below the title (Type / Queue / Created / ID etc.). */
  meta?: PageHeaderMetaItem[];
  /** Optional extra content (e.g. error banner) rendered between meta and bottom of card. */
  children?: ReactNode;
}

export function PageHeader({
  kindLabel,
  title,
  pill,
  actions,
  meta,
  children,
}: PageHeaderProps) {
  return (
    <div className="warp-card">
      <div className="warp-title-row">
        <h1 className="warp-title">
          {kindLabel && <span className="lbl">{kindLabel}</span>}
          <span className="id">{title}</span>
        </h1>
        {pill}
        {actions && <div className="ml-auto flex items-center gap-2">{actions}</div>}
      </div>

      {meta && meta.length > 0 && (
        <div className="warp-meta">
          {meta.map((m, i) => (
            <div key={i} className="item">
              <span className="k">{m.k}</span>
              <span className="v">
                {m.v}
                {m.rel && <span className="rel">· {m.rel}</span>}
              </span>
              {m.copy && (
                <button
                  type="button"
                  className="copy"
                  title="Copy"
                  onClick={() => navigator.clipboard?.writeText(m.copy!)}
                >
                  <Copy size={11} />
                </button>
              )}
            </div>
          ))}
        </div>
      )}

      {children}
    </div>
  );
}
