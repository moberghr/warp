import { Dialog as BaseDialog } from '@base-ui/react/dialog';
import { X } from 'lucide-react';
import { cn } from '@/lib/utils';

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  children: React.ReactNode;
  title?: string;
}

/**
 * Slide-in navigation drawer for mobile/tablet (< lg breakpoint).
 * Built on @base-ui/react/dialog primitives — focus trap, Escape close,
 * and backdrop click come for free. Closes on route change is handled
 * by the caller (MainLayout watches location and toggles `open`).
 */
export default function MobileDrawer({ open, onOpenChange, children, title = 'Navigation' }: Props) {
  return (
    <BaseDialog.Root open={open} onOpenChange={onOpenChange}>
      <BaseDialog.Portal>
        <BaseDialog.Backdrop
          className={cn(
            'fixed inset-0 z-50 bg-black/50',
            'data-[starting-style]:opacity-0 data-[ending-style]:opacity-0',
            'transition-opacity duration-200',
          )}
        />
        <BaseDialog.Popup
          aria-label={title}
          className={cn(
            'fixed inset-y-0 left-0 z-50 w-72 max-w-[85vw]',
            'bg-card border-r border-border shadow-lg',
            'flex flex-col',
            'data-[starting-style]:-translate-x-full data-[ending-style]:-translate-x-full',
            'transition-transform duration-200 ease-out',
            'focus:outline-none',
          )}
        >
          <div className="flex items-center justify-between h-14 px-4 border-b">
            <BaseDialog.Title className="text-lg font-bold">Warp</BaseDialog.Title>
            <BaseDialog.Close
              className="p-2 -mr-2 rounded-md hover:bg-accent text-muted-foreground"
              aria-label="Close navigation menu"
            >
              <X className="h-5 w-5" />
            </BaseDialog.Close>
          </div>
          <div className="flex-1 overflow-y-auto p-3 space-y-6">{children}</div>
        </BaseDialog.Popup>
      </BaseDialog.Portal>
    </BaseDialog.Root>
  );
}
