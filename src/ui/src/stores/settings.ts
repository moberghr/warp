import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export const DEFAULT_DATE_FORMAT = 'dd.MM.yyyy HH:mm:ss';

interface SettingsStore {
  dateFormat: string;
  setDateFormat: (format: string) => void;
  resetDateFormat: () => void;
}

export const useSettingsStore = create<SettingsStore>()(
  persist(
    (set) => ({
      dateFormat: DEFAULT_DATE_FORMAT,
      setDateFormat: (format) => set({ dateFormat: format }),
      resetDateFormat: () => set({ dateFormat: DEFAULT_DATE_FORMAT }),
    }),
    { name: 'warp.settings' },
  ),
);
