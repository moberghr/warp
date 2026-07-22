// #241: bound how many children of a container are rendered individually in the trace graph. A large
// fan-out (hundreds/thousands) is collapsed to `cap` visible children plus one "+N more" summary node.
// This single source of truth is consumed by BOTH the dagre height reservation and the manual group
// layout in TracePage — a divergence between the two was the exact regression a review caught, so they
// must derive their slot count from here.
export interface ChildSlots {
  /** Children rendered as individual nodes. */
  visible: number;
  /** Children folded into the "+N more" summary node (0 when not collapsed). */
  hidden: number;
  /** Total layout slots to reserve: visible children + 1 for the summary node when collapsed. */
  slots: number;
}

export function cappedChildSlots(childCount: number, cap: number): ChildSlots {
  const collapsed = childCount > cap;
  const visible = collapsed ? cap : childCount;
  const hidden = childCount - visible;
  const slots = visible + (hidden > 0 ? 1 : 0);

  return { visible, hidden, slots };
}
