import { describe, it, expect } from 'vitest';
import { cappedChildSlots } from './traceLayout';

const CAP = 24;

describe('cappedChildSlots', () => {
  it('reserves nothing for a childless container', () => {
    expect(cappedChildSlots(0, CAP)).toEqual({ visible: 0, hidden: 0, slots: 0 });
  });

  it('renders all children when under the cap', () => {
    expect(cappedChildSlots(10, CAP)).toEqual({ visible: 10, hidden: 0, slots: 10 });
  });

  it('renders all children exactly at the cap (no collapse)', () => {
    expect(cappedChildSlots(24, CAP)).toEqual({ visible: 24, hidden: 0, slots: 24 });
  });

  it('collapses one child past the cap into a summary slot', () => {
    expect(cappedChildSlots(25, CAP)).toEqual({ visible: 24, hidden: 1, slots: 25 });
  });

  it('bounds the reserved layout slots regardless of fan-out size', () => {
    // The whole point of the collapse (and the review-caught layout bug): a huge fan-out must never
    // reserve more than cap + 1 slots, so dagre height and the group box stay bounded.
    const big = cappedChildSlots(1000, CAP);
    expect(big).toEqual({ visible: 24, hidden: 976, slots: 25 });
    expect(cappedChildSlots(100_000, CAP).slots).toBe(25);
  });
});
