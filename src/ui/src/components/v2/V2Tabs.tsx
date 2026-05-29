export type V2TabKind =
  | 'awaiting'
  | 'scheduled'
  | 'enqueued'
  | 'processing'
  | 'completed'
  | 'failed'
  | 'deleted';

export interface V2Tab {
  kind: V2TabKind;
  label: string;
  count: number;
}

export interface V2TabsProps {
  tabs: V2Tab[];
  active: V2TabKind;
  onChange: (kind: V2TabKind) => void;
}

/**
 * V2 tabs: welded to the top edge of a table/card. Inactive tabs sit on
 * the toolbar (--panel-2) surface; the active tab lifts onto the card
 * (--card) surface with a coloured underline matching its state.
 */
export function V2Tabs({ tabs, active, onChange }: V2TabsProps) {
  return (
    <div className="warp-v2-tabs" role="tablist">
      {tabs.map(t => {
        const isActive = t.kind === active;
        const isEmpty = t.count === 0;
        const classes = [
          'warp-v2-tab',
          t.kind,
          isActive ? 'is-active' : '',
        ].filter(Boolean).join(' ');

        return (
          <button
            key={t.kind}
            type="button"
            role="tab"
            aria-selected={isActive}
            data-empty={isEmpty && !isActive ? '1' : undefined}
            className={classes}
            onClick={() => onChange(t.kind)}
          >
            <span className="dot" />
            {t.label}
            <span className="n">{t.count}</span>
          </button>
        );
      })}
    </div>
  );
}
