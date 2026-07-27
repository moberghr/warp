// Unified trace view (§8.28) — everything for a trace id, unioned from the rows Warp already persists.
// Mirrors Warp.Core/Models/TraceOverviewModel.cs, serialized camelCase.

export type TraceSpanSource = 'client' | 'endpoint' | 'job' | 'adapter';

export interface TraceSpan {
  source: TraceSpanSource;
  id: string;
  name: string;
  startTime: string;
  durationMs: number | null;
  status: string;
  isError: boolean;
  parentId: string | null;
}

export interface TraceOverview {
  traceId: string;
  spans: TraceSpan[];
  jobCount: number;
  endpointCount: number;
  adapterCount: number;
  clientCount: number;
  errorCount: number;
}
