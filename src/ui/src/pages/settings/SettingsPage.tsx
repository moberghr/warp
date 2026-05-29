import { useEffect, useState } from 'react';
import { format } from 'date-fns';
import { Panel, PanelHeader } from '@/components/v2/Panel';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { usePageStore } from '@/stores/page';
import { DEFAULT_DATE_FORMAT, useSettingsStore } from '@/stores/settings';

const PRESETS: { label: string; value: string }[] = [
  { label: 'European', value: 'd.MM.yyyy HH:mm:ss' },
  { label: 'ISO', value: 'yyyy-MM-dd HH:mm:ss' },
  { label: 'ISO with ms', value: 'yyyy-MM-dd HH:mm:ss.SSS' },
  { label: 'US', value: 'MM/dd/yyyy hh:mm:ss a' },
  { label: 'Short EU', value: 'dd.MM.yyyy HH:mm' },
];

export default function SettingsPage() {
  const setPage = usePageStore((s) => s.set);
  const resetPage = usePageStore((s) => s.reset);
  const dateFormat = useSettingsStore((s) => s.dateFormat);
  const setDateFormat = useSettingsStore((s) => s.setDateFormat);
  const resetDateFormat = useSettingsStore((s) => s.resetDateFormat);

  const [draft, setDraft] = useState(dateFormat);

  useEffect(() => {
    setPage({ title: 'Settings', subtitle: 'Preferences for this browser' });

    return () => resetPage();
  }, [setPage, resetPage]);

  useEffect(() => {
    setDraft(dateFormat);
  }, [dateFormat]);

  let preview = '';
  let previewError: string | null = null;
  try {
    preview = format(new Date(), draft);
  } catch (e) {
    previewError = e instanceof Error ? e.message : 'Invalid format';
  }

  return (
    <div className="max-w-2xl flex flex-col gap-4">
      <Panel>
        <PanelHeader eyebrow="Date & time format" />
        <div className="p-4 flex flex-col gap-4">
          <div className="flex flex-col gap-2">
            <Label htmlFor="dateFormat">Format pattern</Label>
            <Input
              id="dateFormat"
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              className="font-mono"
              spellCheck={false}
            />
            <div className="text-[12px] text-text-mute">
              Uses{' '}
              <a
                href="https://date-fns.org/docs/format"
                target="_blank"
                rel="noreferrer"
                className="underline"
              >
                date-fns tokens
              </a>
              . Example tokens: <code>yyyy</code> year, <code>MM</code> month,{' '}
              <code>dd</code> day, <code>HH</code> 24h, <code>hh</code> 12h, <code>mm</code>{' '}
              min, <code>ss</code> sec, <code>SSS</code> ms.
            </div>
          </div>

          <div className="flex flex-col gap-1">
            <Label>Preview</Label>
            {previewError ? (
              <div className="font-mono text-[13px] text-warp-red">{previewError}</div>
            ) : (
              <div className="font-mono text-[13px]">{preview}</div>
            )}
          </div>

          <div className="flex flex-col gap-2">
            <Label>Presets</Label>
            <div className="flex flex-wrap gap-2">
              {PRESETS.map((p) => (
                <button
                  key={p.value}
                  type="button"
                  onClick={() => setDraft(p.value)}
                  className="rounded-md border border-border bg-panel px-2.5 py-1 text-[12px] hover:bg-panel-2"
                >
                  <span className="font-medium">{p.label}</span>{' '}
                  <span className="font-mono text-text-mute">{p.value}</span>
                </button>
              ))}
            </div>
          </div>

          <div className="flex gap-2 pt-2">
            <Button
              type="button"
              disabled={!!previewError || draft === dateFormat}
              onClick={() => setDateFormat(draft)}
            >
              Save
            </Button>
            <Button
              type="button"
              variant="outline"
              onClick={() => {
                resetDateFormat();
                setDraft(DEFAULT_DATE_FORMAT);
              }}
            >
              Reset to default
            </Button>
          </div>

          <div className="text-[12px] text-text-mute">
            Default: <code className="font-mono">{DEFAULT_DATE_FORMAT}</code>. Settings are
            stored in your browser.
          </div>
        </div>
      </Panel>
    </div>
  );
}
