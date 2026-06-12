import { AlertTriangle, Loader } from 'lucide-react';
import { Panel } from '@/components/v2/Panel';

export function LoadingState() {
  return (
    <div className="flex items-center gap-2 text-text-mute py-8 px-5">
      <Loader className="h-4 w-4 animate-spin" />
      <span className="text-[13px]">Loading...</span>
    </div>
  );
}

export function ErrorState({ message }: { message?: string }) {
  return (
    <div className="p-5">
      <Panel>
        <div className="py-12 text-center px-6">
          <AlertTriangle className="h-10 w-10 text-warp-red mx-auto mb-3" />
          <p className="text-[15px] font-medium">{message ?? 'Something went wrong'}</p>
          <p className="text-[13px] text-text-mute mt-1">
            Make sure the Warp backend is running and accessible.
          </p>
        </div>
      </Panel>
    </div>
  );
}

export function EmptyState({ message }: { message: string }) {
  return (
    <div className="text-center text-text-mute py-12 text-[13px]">
      {message}
    </div>
  );
}
