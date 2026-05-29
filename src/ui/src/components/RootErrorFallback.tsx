import { AlertTriangle } from 'lucide-react';
import type { FallbackProps } from 'react-error-boundary';
import { Button } from '@/components/ui/button';
import { Panel } from '@/components/v2/Panel';

export function RootErrorFallback({ error, resetErrorBoundary }: FallbackProps) {
  const message = error instanceof Error ? error.message : String(error);
  const stack = error instanceof Error ? error.stack : null;

  return (
    <div className="min-h-screen flex items-center justify-center p-6 bg-background">
      <Panel className="max-w-lg w-full">
        <div className="py-10 px-6 text-center space-y-4">
          <AlertTriangle className="h-12 w-12 text-warp-red mx-auto" />
          <div className="space-y-1">
            <p className="text-[15px] font-semibold">Something went wrong</p>
            <p className="text-[13px] text-text-mute">
              The dashboard hit an unexpected error. Reloading usually fixes it.
            </p>
          </div>
          <details className="text-left" open={import.meta.env.DEV}>
            <summary className="cursor-pointer text-[12px] text-text-mute hover:text-foreground select-none">
              Show details
            </summary>
            <pre className="mt-2 text-[11px] bg-panel-2 rounded p-3 overflow-auto max-h-40">
              {message}
              {import.meta.env.DEV && stack ? `\n\n${stack}` : ''}
            </pre>
          </details>
          <div className="flex gap-2 justify-center">
            <Button variant="outline" onClick={resetErrorBoundary}>
              Try again
            </Button>
            <Button onClick={() => window.location.reload()}>Reload page</Button>
          </div>
        </div>
      </Panel>
    </div>
  );
}
