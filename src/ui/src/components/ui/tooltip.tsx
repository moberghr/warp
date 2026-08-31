import type * as React from "react"
import { Tooltip as TooltipPrimitive } from "@base-ui/react/tooltip"

import { cn } from "@/lib/utils"

function TooltipProvider(props: TooltipPrimitive.Provider.Props) {
  return <TooltipPrimitive.Provider delay={300} {...props} />
}

// The hover delay lives on Provider in this version of Base UI, not on Root — so TooltipProvider
// has to be mounted above any Tooltip for the 300ms to apply (it is, at the app root in App.tsx).
function Tooltip(props: TooltipPrimitive.Root.Props) {
  return <TooltipPrimitive.Root data-slot="tooltip" {...props} />
}

function TooltipTrigger(props: TooltipPrimitive.Trigger.Props) {
  return <TooltipPrimitive.Trigger data-slot="tooltip-trigger" {...props} />
}

function TooltipContent({ className, children, sideOffset = 6, ...props }: TooltipPrimitive.Popup.Props & { sideOffset?: number }) {
  return (
    <TooltipPrimitive.Portal>
      <TooltipPrimitive.Positioner sideOffset={sideOffset}>
        <TooltipPrimitive.Popup
          data-slot="tooltip-content"
          className={cn(
            "z-50 max-w-xs rounded-md border border-border bg-popover px-2 py-1 text-xs text-popover-foreground shadow-md outline-none transition-[opacity,transform] duration-150 data-[starting-style]:opacity-0 data-[ending-style]:opacity-0 data-[starting-style]:scale-95 data-[ending-style]:scale-95",
            className
          )}
          {...props}
        >
          {children}
        </TooltipPrimitive.Popup>
      </TooltipPrimitive.Positioner>
    </TooltipPrimitive.Portal>
  )
}

// One-line replacement for a native `title` attribute: wrap the element, drop its title. The
// element itself is what triggers (Base UI's `render` merges the trigger's props into it rather
// than adding a wrapper node), so layout and classNames are untouched.
//
// A null/empty text renders the child bare — no empty popup, and no hover affordance on an element
// that has nothing to say. Note for ICON-ONLY triggers: a native title doubles as the accessible
// name, but a tooltip only sets aria-describedby, so those need their own aria-label.
function Hint({ text, children }: { text?: string | null; children: React.ReactElement }) {
  if (!text) {
    return children
  }

  return (
    <Tooltip>
      <TooltipTrigger render={children} />
      <TooltipContent>{text}</TooltipContent>
    </Tooltip>
  )
}

export { Tooltip, TooltipProvider, TooltipTrigger, TooltipContent, Hint }
