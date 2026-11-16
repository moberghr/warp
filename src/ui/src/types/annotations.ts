// DTOs for metric annotations (#333) — Warp.Core/Models/MetricAnnotationModels.cs, serialized camelCase.
// Enums are numeric on the wire (§8.11, start at 1).

/** What a note is attached to (MetricAnnotationTarget). */
export const MetricAnnotationTarget = {
  Instrument: 1,
  Slo: 2,
} as const;
export type MetricAnnotationTarget = (typeof MetricAnnotationTarget)[keyof typeof MetricAnnotationTarget];

/** One stored note (GET /api/annotations item). */
export interface MetricAnnotation {
  id: number;
  target: MetricAnnotationTarget;
  instrument: string | null;
  tagFilter: string | null;
  sloDefinitionId: number | null;
  note: string;
  /** The dashboard user who wrote it, or "anonymous" on an open dashboard. */
  author: string;
  startsAt: string;
  /** Null = ongoing. */
  endsAt: string | null;
  acknowledgedNoise: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface MetricAnnotationList {
  items: MetricAnnotation[];
  isTruncated: boolean;
}

/** POST /api/annotations body (id 0 creates). The author is never sent: the server takes it from the signed-in user. */
export interface MetricAnnotationUpsert {
  id: number;
  target: MetricAnnotationTarget;
  instrument: string | null;
  tagFilter: string | null;
  sloDefinitionId: number | null;
  note: string;
  startsAt: string;
  endsAt: string | null;
  acknowledgedNoise: boolean;
}
