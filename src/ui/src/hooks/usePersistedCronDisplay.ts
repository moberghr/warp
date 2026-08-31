import { useState } from 'react';

// Which half of a cron schedule the recurring list shows in the cell, the other half moving to the
// hover hint. Persisted because it is a reading preference, not a per-visit choice: someone who
// thinks in cron expressions wants the raw column every time they open the page.
export type CronDisplay = 'description' | 'expression';

const STORAGE_KEY = 'warp:cronDisplay';

export function usePersistedCronDisplay(): [CronDisplay, (value: CronDisplay) => void] {
  const [display, setDisplay] = useState<CronDisplay>(() =>
    localStorage.getItem(STORAGE_KEY) === 'expression' ? 'expression' : 'description');

  const update = (value: CronDisplay) => {
    setDisplay(value);
    localStorage.setItem(STORAGE_KEY, value);
  };

  return [display, update];
}
