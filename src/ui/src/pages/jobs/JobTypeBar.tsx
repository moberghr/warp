import { Panel, PanelHeader } from '@/components/v2/Panel';
import type { TypeCountModel } from '@/types';
import { shortType } from '@/utils/format';

// Stable color rotation for type segments — chosen from the warp palette.
const TYPE_COLORS = [
  'var(--warp-red)',
  'var(--warp-amber)',
  'var(--warp-purple)',
  'var(--warp-blue)',
  'var(--warp-green)',
];

function colorFor(index: number): string {
  return TYPE_COLORS[index % TYPE_COLORS.length];
}

interface JobTypeBarProps {
  types: TypeCountModel[];
  activeType: string | null;
  onPick: (type: string | null) => void;
}

export function JobTypeBar({ types, activeType, onPick }: JobTypeBarProps) {
  if (types.length === 0) {
    return null;
  }

  const total = types.reduce((s, x) => s + x.count, 0);

  return (
    <Panel className="mb-4">
      <PanelHeader eyebrow="By type">
        <span className="text-[11.5px] text-text-mute">
          {total} failures · click to filter
        </span>
      </PanelHeader>
      <div className="p-4">
        <div className="flex h-2 rounded overflow-hidden bg-panel-2 mb-3">
          {types.map((tt, i) => (
            <button
              key={tt.type}
              type="button"
              onClick={() => onPick(activeType === tt.type ? null : tt.type)}
              aria-label={`Filter by ${shortType(tt.type)}`}
              className="h-full transition-opacity"
              style={{
                flex: tt.count,
                background: colorFor(i),
                opacity: !activeType || activeType === tt.type ? 1 : 0.25,
                borderRight: i < types.length - 1 ? '2px solid var(--panel)' : 'none',
              }}
            />
          ))}
        </div>
        <div className="flex flex-wrap gap-1.5">
          <button
            type="button"
            onClick={() => onPick(null)}
            className={`px-3 py-1 text-[12px] font-medium rounded-md border transition-colors ${
              !activeType
                ? 'bg-foreground text-background border-foreground'
                : 'bg-transparent text-text-dim border-border hover:bg-panel-2'
            }`}
          >
            All
          </button>
          {types.map((tt, i) => {
            const isActive = activeType === tt.type;
            const color = colorFor(i);

            return (
              <button
                key={tt.type}
                type="button"
                onClick={() => onPick(isActive ? null : tt.type)}
                className={`mono px-2.5 py-1 text-[11.5px] font-medium rounded-md border inline-flex items-center gap-1.5 transition-colors ${
                  isActive ? 'bg-panel-2' : 'bg-transparent hover:bg-panel-2/60'
                }`}
                style={{
                  borderColor: isActive ? color : 'var(--border)',
                  color: isActive ? color : 'var(--text-dim)',
                }}
              >
                <span
                  className="inline-block w-1.5 h-1.5 rounded-full"
                  style={{ background: color }}
                />
                {shortType(tt.type)}
                <span className="opacity-80">({tt.count})</span>
              </button>
            );
          })}
        </div>
      </div>
    </Panel>
  );
}
