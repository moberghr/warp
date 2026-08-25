---
sidebar_position: 7
---

# Trace

Click the trace link on any job detail page to open the full trace visualization.

The trace page renders a directed acyclic graph (DAG) of all jobs sharing a `TraceId`. Each node shows the job type and current state. Edges show the spawned-by relationship — who created whom. The legend distinguishes Jobs, Messages, and Batches.

Click any node to navigate to that job's detail page.

For background on how tracing works, see [Job Tracing](/docs/features/tracing).

import Screenshot from '@site/src/components/Screenshot';

<Screenshot light="/img/screenshots/12-trace.png" dark="/img/screenshots/12-trace-dark.png" alt="Trace visualization" />
