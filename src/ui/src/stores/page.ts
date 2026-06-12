import type { ReactNode } from 'react';
import { create } from 'zustand';

interface PageStore {
  title: string;
  subtitle?: string;
  right?: ReactNode;
  set: (state: Partial<Omit<PageStore, 'set' | 'reset'>>) => void;
  reset: () => void;
}

const defaultState = {
  title: 'Warp',
  subtitle: undefined as string | undefined,
  right: undefined as ReactNode | undefined,
};

export const usePageStore = create<PageStore>((set) => ({
  ...defaultState,
  set: (next) => set(next),
  reset: () => set(defaultState),
}));
