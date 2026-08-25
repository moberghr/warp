---
sidebar_position: 3
---

import Screenshot from '@site/src/components/Screenshot';

# Handlers

`jobstat:` keyed by **handler** — the same execution counts, sliced by the code that ran rather than the
type that was published.

For an ordinary `IJob` the two tabs agree: one type, one handler. For a routed `IMessage` they do not. One
message fans out to every subscribed handler, so a single row on [Job types](./job-types) becomes several
rows here, one per subscriber that consumed it.

That difference is the point of the tab. When a message type looks slow, this is where you find out
*which* subscriber is slow instead of blaming the publish.

<Screenshot light="/img/screenshots/38-counters-handlers.png" dark="/img/screenshots/38-counters-handlers-dark.png" alt="The Handlers counter tab" />
