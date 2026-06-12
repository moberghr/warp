import { useCallback, useRef, useState } from 'react';
import { ConfirmDialog } from './ConfirmDialog';

type ConfirmOptions = {
  title: string;
  description?: React.ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  destructive?: boolean;
};

type State = ConfirmOptions & { open: boolean };

const DEFAULT_STATE: State = {
  open: false,
  title: '',
};

export function useConfirm() {
  const [state, setState] = useState<State>(DEFAULT_STATE);
  const resolverRef = useRef<((ok: boolean) => void) | null>(null);

  const confirm = useCallback((opts: ConfirmOptions) => {
    // Settle any still-pending confirm so its caller doesn't hang forever.
    resolverRef.current?.(false);
    setState({ ...opts, open: true });

    return new Promise<boolean>((resolve) => {
      resolverRef.current = resolve;
    });
  }, []);

  const handleOpenChange = (open: boolean) => {
    if (!open) {
      resolverRef.current?.(false);
      resolverRef.current = null;
      setState((s) => ({ ...s, open: false }));
    }
  };

  const handleConfirm = () => {
    resolverRef.current?.(true);
    resolverRef.current = null;
    setState((s) => ({ ...s, open: false }));
  };

  const dialog = (
    <ConfirmDialog
      open={state.open}
      onOpenChange={handleOpenChange}
      title={state.title}
      description={state.description}
      confirmLabel={state.confirmLabel}
      cancelLabel={state.cancelLabel}
      destructive={state.destructive}
      onConfirm={handleConfirm}
    />
  );

  return { confirm, dialog };
}
