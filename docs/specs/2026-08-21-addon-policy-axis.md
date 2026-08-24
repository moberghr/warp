# Addon policy axis — handler-declared `[Mutex]` / `[Semaphore]` / `[RateLimit]` / `[Timeout]`, for jobs and message handlers

- **Date:** 2026-08-21 (revised 2026-08-22 — message handlers brought in scope; `Total`-scope global default kept at publish; conflict check defined per metadata family and extended to Retry/CircuitBreaker)
- **Scope classification:** additive capability + latent bug fixes (recurring jobs, message handlers, Timeout default shadowing) + one doc-vs-behaviour correction
- **Security impact:** none — no new persisted field, no new log/metric dimension, no PII surface (§1.2)
- **Schema impact:** none — no new entity, no new column, no migration. `Job.Metadata` gains no new keys; existing keys are written from one additional site.
- **Supersedes:** the #242 rule as written in §8.8 ("read off the request/job type, never the handler impl")

---

## Governing principle

**A job's policy is resolved once, written onto the job, and never re-derived — at the earliest point the declaring axis is knowable.**

That is what the current design is reaching for. What it actually implements is *"resolved at publish"*, which silently hard-codes one answer to "where is the policy declared?" — the contract. Publish is not the principle; it is the earliest binding point **for a contract attribute**. A handler attribute has a different earliest binding point, and the invariant survives being applied there.

## Problem

§8.8 / #242 forbids `[Timeout]`, `[Mutex]`, `[Semaphore]`, `[RateLimit]` on a handler class, and `AddWarp` throws at registration (`ServiceConfiguration.cs:119,369-419`). The reason is mechanical, not conceptual: a **publish** behaviour resolves the attribute off `typeof(T)` and stamps it into `Job.Metadata` (`ConcurrencyPublishBehavior.cs:13-32`, `RateLimitPublishBehavior.cs:12-28`, `TimeoutPublishBehavior.cs:23-49`), and the execution behaviour reads **only** metadata (`ConcurrencyPipelineBehavior.cs:37-40`, `RateLimitPipelineBehavior.cs:42-45`, `TimeoutPipelineBehavior.cs:32-35`). At publish the handler type is genuinely unknowable — a publisher-only process, or a different app in a shared-DB deployment (§8.23), never loads the handler assembly.

But every one of these four policies is *evaluated* at execution, and mutual exclusion is a property of the code doing the work, not of the message describing it. "This handler talks to a single-connection legacy endpoint, so serialize it" is a statement about the handler. Forcing it onto the contract means every publisher of that contract has to know an implementation detail of one consumer.

### The stated intent is already only 3-of-5 true

| Addon | Resolved where | Recorded on the job | Reflects at execution |
|---|---|---|---|
| Mutex / Semaphore | publish | yes | no |
| RateLimit | publish | yes | no |
| Timeout | publish (attribute + global default + `Total` deadline) | yes | no |
| **Retry** | publish (contract attr) **and** execution (handler attr, global default) | partly | **yes, every attempt** |
| **CircuitBreaker** | execution only | **never** | **yes, every attempt** |

`RetryPipelineBehavior.cs:121-133` and `CircuitBreakerPipelineBehavior.cs:141-154` both resolve handler-then-contract on **every attempt**, and CircuitBreaker records nothing at all. So the codebase already contains the mechanism this design generalises — twice — and the reflection it performs is a `ConcurrentDictionary<Type, TAttr?>` hit after the first call per process. The invariant's value was never CPU.

### The rule is already dishonest in three places

1. **`RetryAttribute`'s own XML doc is false.** It states `per-enqueue metadata > handler attribute > job attribute > global RetryOptions`. But `RetryPublishBehavior.cs:26` stamps the *contract* attribute into metadata, and execution reads `meta.MaxRetries ?? attr?.MaxRetries` (`:36-38`). A contract `[Retry]` therefore **shadows** a handler `[Retry]`; the handler axis only ever applies when the contract declares nothing. The documented precedence has never held.

2. **Recurring jobs silently ignore all four policies.** `RecurringJobScheduler.cs:143-150` stages firings via `JobHelper.CreateJob` **without** the `metadata` argument, and `RecurringJob` has no `Metadata` column at all — so every firing carries `Metadata = null`. Publish behaviours never run on that path (the same bypass §8.20 documents for `NoRestartPublishBehavior`). Consequence today: `[Mutex]`, `[Semaphore]`, `[RateLimit]`, `[Timeout]` on a recurring job type are **inert**, while `[Retry]` and `[CircuitBreaker]` work — precisely and only because those two resolve at execution. A recurring job is the single most likely thing a user wants to mutex.

3. **`IMessage` handlers ignore all six policies.** `IMessage` does not implement `IJob` (`IMessage.cs`, `IJob.cs`). Concurrency, RateLimit and Timeout early-return on `request is not IJob` at execution; Retry and CircuitBreaker are constrained `where TRequest : IJob` at compile time, so DI's open-generic constraint check skips them for message-routed jobs entirely — **routed message handlers get no retry and no circuit breaker either, from any axis, including the global `RetryOptions` default**. And the contract half already flows end-to-end for nothing: `Publisher.CreateMessage` runs the publish pipeline (`Publisher.cs:143`), so a contract `[Mutex]` on a message type is stamped into the Message row's metadata and `MessageRouter.cs:178` copies that blob onto every child job — where the execution guards then ignore it. (In scope — see *Design §7*.)

Points 2 and 3 are the tell: the constraint is not protecting an invariant, it is a gap that happens to be shaped like one.

## What "resolve at publish" is actually worth

Three separable properties get bundled under that phrase. Only the first conflicts with handler attributes:

1. **Publisher-side resolvability** — the policy is computable in a process that has no handler registered. *Inherently incompatible with a handler attribute.* Nothing preserves it; a handler policy is late-bound by nature.
2. **Pinning** — the job carries the policy that governed it, so a mid-flight deploy cannot reshape in-flight jobs and the row explains why a job was skipped or delayed. Same stance as the self-contained `WebhookDelivery` row (§8.20). *Fully preservable.*
3. **One read path at execution** — metadata only, trivially testable. *Fully preservable.*

The design keeps (2) and (3) by moving the resolve-once point rather than deleting it, and gives up (1) only for policies declared on the handler axis — where it was never available in the first place.

## Design

### 1. The axis rule

> **Contract axis** = policy that must be known before, or independently of, execution.
> **Handler axis** = policy evaluated at execution, by the code doing the work.

`[Mutex]`, `[Semaphore]`, `[RateLimit]` and `[Timeout(Scope = PerAttempt)]` are evaluated at execution and belong on **either** axis — for jobs **and** for messages.

**For messages the two axes are not even redundant — they mean different things**, which is the strongest argument that the axis distinction is real:

- **On the message contract:** the policy stamps into the Message row's metadata at publish and `MessageRouter` copies it to **every** handler's child job. All handlers of that message type share the declared key — they contend on one lock / one bucket. Use when the *event itself* must be processed under a shared constraint.
- **On a message handler:** the policy applies to **that handler's child jobs only**. The other handlers of the same message are untouched. This is the natural home: the handler is the code touching the resource — it knows whether duplicate runs must be avoided or serialized — and the message contract shouldn't know that any consumer has such a constraint at all.

Two policies stay contract-only, for reasons that survive scrutiny:

- **`TimeoutScope.Total`** — a wall-clock budget measured from enqueue. The deadline must exist before the first execution: both worker paths read it *pre-execution* to feed the §8.31 deadline-attainment counter (`WarpWorkerService.cs:180-187`, `WarpDispatcherWorker.cs:257-263`), and `TimeoutPublishBehavior.cs:42-47` is its only writer. Declaring `Scope = Total` on a handler is a startup error.
- **`[NoRestart]` / `[Restart]`** — consumed by `StaleJobRecovery` (`:33,431`), a server task that decides whether to requeue a job that is **not** executing, in a process that may not host the handler at all. There is no binding moment to hang it on.

### 2. Binding-time resolution

The moment a job binds to a handler is *already* a resolve-once-and-persist site:

- **direct jobs** — `WarpWorkerService.cs:463-465` / `WarpDispatcherWorker.cs:588-590` discover the handler (`JobDispatcher.DiscoverJobHandler`, a DI lookup) and write `job.HandlerType` onto the row
- **routed messages** — bind *earlier* than direct jobs: `MessageRouter.cs:176` stamps `job.HandlerType` onto every child at routing, so the handler axis is knowable before the first execution, and `:178` copies the message's metadata blob, so the contract axis is already carried per child
- **`IJobContext.HandlerType` is therefore always populated at execution**, either from the row or from that discovery — which is exactly why Retry and CircuitBreaker can do this today

So: each execution behaviour, on the path where its metadata slot is empty, resolves the attribute, stamps it into metadata, and proceeds. The existing write-back persists it (`WarpWorkerService.cs:221` / `WarpDispatcherWorker.cs:302` on the outcome path, `:363` / `:425` on the failure-with-outcome path). Every subsequent attempt reads metadata and is byte-for-byte today's path.

**No new seam, no worker change, nothing added to the fetch/claim loop (§0.2/§6.1)** — the resolution happens inside the handler scope, where all other addon work already lives.

### 3. One resolver, four rungs

A single cached helper (`AddonAttributeResolver`, `Warp.Core.Handlers`) serves all five policy families, replacing the hand-rolled `ConcurrentDictionary<Type, TAttr?>` caches (Retry's, CircuitBreaker's, and the ones Concurrency/RateLimit/Timeout would otherwise each grow):

```
metadata (explicit WithMutex/WithTimeout/… at publish)
  → handler attribute        (typeof handler, from IJobContext.HandlerType)
    → contract attribute     (typeof(TRequest))
      → global options       (TimeoutOptions.Default, RetryOptions, …)
```

**The contract rung at execution is not redundant** — it is what fixes the recurring-job gap (problem 2) and every other direct-staging site that bypasses the publish pipeline. That fix is the same code, not extra code.

### 4. Precedence dissolves instead of being ranked

Declaring the **same policy family on both axes** for the same request/handler pair is a **startup error**. `ValidateAddonAttributesOnHandlers` (`:369`) already walks exactly the right set — registered handler descriptors paired with their request type, with the self-handling exemption (`:399-403`) — so it is repurposed rather than deleted: from *"handler placement is wrong"* to *"pick one axis"*. `NoRestartPublishBehavior.cs:41-47` is the in-repo precedent for rejecting a both-attributes conflict.

Three definitions make the check sound rather than approximate:

- **Conflicts are per metadata family, not per attribute type.** `[Mutex]` and `[Semaphore]` both write the `IConcurrencyMetadata` slot — a contract `[Mutex]` plus a handler `[Semaphore]` is the same shadowing this design exists to kill (the publish-stamped contract value fills the slot, the handler rung is never reached) and must be rejected as one conflict. Families: Concurrency (`Mutex`+`Semaphore`), RateLimit, Timeout, Retry, CircuitBreaker.
- **Retry and CircuitBreaker join the conflict check.** Today both-axes `[Retry]` is legal and the handler side is silently dead (problem 1). Making it a startup error is what turns the documented precedence from *false* into *unobservable-and-therefore-true* — but it is a **new startup failure for configurations that run today**, and gets its own release note (behaviour change 5). Exempting them for back-compat was considered and rejected: a silently-dead attribute is exactly the bug class #242 exists to make loud. *Implementation deviation, recorded (review finding S1):* Retry/CircuitBreaker are **not** rejected on unsupported shapes (a `[Retry]` on an in-memory request handler stays tolerated dead code) — they were always legal there, and rejecting them now would be a second unspecced breaking change; only the both-axes conflict is new for them.
- **The handler axis is only legal where the execution path can honour it.** The walk covers four handler shapes; they do not all gain the new capability, and the ones that don't **keep the #242 rejection** — otherwise repurposing the check would silently reintroduce the silent no-op for three of the four shapes:
  - `IJobHandler<T>` and `IMessageHandler<T>` pairs — handler placement **legal** (this design).
  - `IRequestHandler<TRequest,>` pairs — legal **iff `TRequest` is `IJob` or `IMessage`**; rejected otherwise (an in-memory request has no job row, no requeue semantics, no metadata record — every policy outcome is meaningless there).
  - `IStreamRequestHandler<,>` pairs — **still rejected** (same reason).

Consequence worth stating plainly: **once double declaration cannot compile-and-run, handler-vs-contract precedence is unobservable.** `metadata → handler → contract` and `metadata → contract → handler` become the same function. That means:

- the publish behaviours keep stamping contract attributes exactly as today — no back-compat change, properties (1)/(2)/(3) intact for contract-declared policy
- `RetryAttribute`'s documented precedence becomes true without touching `RetryPublishBehavior`
- the loud-failure property #242 bought is preserved, aimed at a real conflict instead of a legal placement

### 5. Metadata stays the record

Handler-resolved values are stamped into metadata, not just used transiently. This is deliberate and is what preserves property (2): the resolved policy is visible on the job row for the first time (today a handler-declared `[Retry]` and *all* CircuitBreaker policy are invisible), and a deploy that changes a handler attribute does not reshape jobs already bound — identical semantics to contract attributes.

**Do not overclaim exactly-once.** The failure path only persists metadata when an outcome was set (`WarpWorkerService.cs:350-363`), so a plain first-attempt failure drops the stamp and the next attempt re-resolves. Idempotent, so harmless — the honest claim is *at most once per attempt, until it sticks*.

### 6. Per-addon specifics

- **Concurrency (`[Mutex]` / `[Semaphore]`)** — clean. No global default, nothing frozen at publish; admin overrides already resolve at execution (`ConcurrencyLimitResolver`). Stamp `ConcurrencyKey` / `ConcurrencyLimit` / `ConcurrencyMode` together.
- **RateLimit** — clean, with one trap: the execution gate requires **`RateLimitKey`, `RateLimitCount` and `RateLimitWindowSeconds` all non-null** (`:42-45`). A partial stamp silently no-ops. Stamp all five fields or none.
- **Timeout** — needs one move regardless of the handler axis: `TimeoutPublishBehavior.cs:36-40` stamps the **global default** (`TimeoutOptions.Default`) into metadata when no contract attribute is found, so with any global default configured the metadata slot is always full and a handler attribute is unreachable. This is the identical shadowing bug Retry already fixed as #236 (`RetryPublishBehavior.cs:6-13`) — currently latent for Timeout, load-bearing once the handler rung exists. **The move is scope-split, not wholesale:**
  - A **`PerAttempt`-scoped default** (the common case) moves to `TimeoutPipelineBehavior` — applied at execution as the resolver's last rung, exactly like `RetryOptions`.
  - A **`Total`-scoped default keeps publish stamping.** `Total`'s deadline must exist before the first execution (both worker paths read it *pre-execution* for the §8.31 attainment counter, and it measures from enqueue, not from first attempt) — moving it to execution would silently change what the budget means and blind the attainment counter for every default-timeout job. Under a `Total`-scoped default the metadata slot is therefore always full at publish and a handler `[Timeout]` is unreachable — so **that combination is rejected at startup**, consistent with "reject instead of rank": `AddTimeout` applies its `configure` delegate to a scratch `TimeoutOptions` at registration to learn `DefaultScope`, and `ValidateAddonAttributesOnHandlers` rejects any handler `[Timeout]` when it is `Total`. (Caveat: a host that *additionally* configures `TimeoutOptions` outside `AddTimeout` escapes the scratch read — same registered-state-only caveat the rest of the validation already carries.)
  - `Scope = Total` on a handler is rejected at startup regardless (see §1).
  - **Recurring-job limitation, stated honestly:** the contract rung at execution fixes `PerAttempt` timeouts for recurring firings, but a contract `[Timeout(Scope = Total)]` on a recurring job type stays inert — the deadline's only writer is the publish behaviour, which the scheduler's direct staging bypasses, and the execution-time fallback (`TimeoutPipelineBehavior`'s else-branch) would quietly degrade it to a fresh full budget per attempt. Rather than invent a second deadline writer with different semantics (measured from first bind, not enqueue), the resolver **refuses to stamp a `Total`-scoped contract attribute when no deadline exists in metadata** and logs a one-time Warning naming the type. The refusal declines the *attribute*, not timeouts generally: the job is then effectively attribute-less, so a configured `PerAttempt` global default still applies to it like any other unattributed job (pinned by `ContractTotalTimeout_NoDeadline_FallsBackToPerAttemptGlobalDefault`). Release note 2 carries the qualification.
- **Retry** — doc corrected, cache moved to the shared resolver; constraint widened for messages (see §7).
- **CircuitBreaker** — constraint widened for messages (see §7). It could stamp its resolved group for the same visibility win; deferred (see *Out of scope*).

### 7. Messages join both axes

Message handlers are in scope for **all** policy addons — Concurrency, RateLimit, Timeout (`PerAttempt`), Retry, CircuitBreaker — on both axes, with the semantics from §1: contract-declared policy is copied to every handler's child job (shared key — the handlers contend with each other); handler-declared policy applies to that handler's children only.

**Almost everything already exists.** The contract axis flows end-to-end today and is merely ignored at the last step: `Publisher.CreateMessage` runs the publish pipeline (`Publisher.cs:143`), so contract attributes are already stamped into the Message row's metadata, and `MessageRouter.cs:178` already copies that blob onto every child. The handler axis is *better* positioned than for direct jobs: `MessageRouter.cs:176` stamps `job.HandlerType` at routing, so the resolver's handler rung works from the first attempt with zero discovery. Each child owns its metadata copy after routing, so per-child stamping and per-child retry counters need no new mechanism.

What actually changes — three mechanical unlocks plus one correctness fix:

1. **Runtime guards widen.** `ConcurrencyPipelineBehavior.cs:37`, `RateLimitPipelineBehavior`, `TimeoutPipelineBehavior.cs:32`: `request is not IJob` → early-return only when the request is neither `IJob` nor `IMessage`.
2. **Compile-time constraints split, not dropped** *(revised per review finding S9)*: `RetryPipelineBehavior` and `CircuitBreakerPipelineBehavior` become unconstrained (directly constructible, runtime-guarded as defense-in-depth), but what DI registers are two internal constraint-carrying shims each (`RetryJobPipelineBehavior` / `RetryMessagePipelineBehavior`, same for the breaker) — `where TRequest : IJob` and `where TRequest : IMessage`. In-memory sends and stream requests therefore never compose the behaviours at all, which matters for the breaker: an unconstrained registration would have resolved the DbContext-backed `ICircuitBreakerStore` on every in-memory `Send` in a Warp.Http host before the guard could early-return.
3. **The §8.14 fix, which was the deferred prerequisite and is now in scope:** four sites set `ClearHandlerType = true` unconditionally on requeue outcomes — `ConcurrencyPipelineBehavior.BuildRequeueOutcome`, `RateLimitPipelineBehavior` (lock-contention and Wait-mode reschedules), and `RetryPipelineBehavior`'s reschedule (whose own comment says *"If that constraint is ever widened, this line has to become conditional"* — this is that day). All four become `ClearHandlerType = request is not IMessage`. This is not just rule compliance: a routed child's `HandlerType` **is the routing decision** — clearing it sends the next attempt to `JobDispatcher.DiscoverJobHandler(messageType)`, which looks up `IJobHandler<T>` for a type that only has `IMessageHandler<T>` registrations and throws `"No handler registered"`. A Wait-mode mutex on a message handler would kill the child on its first requeue. `CircuitBreakerPipelineBehavior` never sets the flag and needs nothing.
4. **Validation pairs extend naturally.** `ValidateAddonAttributesOnHandlers` already walks `IMessageHandler<>` descriptors, so the per-family conflict check (§4) covers a message contract vs. each of its handlers with no new walk. Two different handlers of the same message may each declare their own policy — that is not a conflict, it is the point.
5. **Deleted children now settle their parent** *(added during implementation — the SC11/SC12 tests caught it)*: the Orchestrator's parent-readiness query treated a `Deleted` child as *pending*, so a message (or batch) with any deleted child could never finalize — its parent sat `Processing`/`Awaiting` forever. Unreachable before this change (no policy could delete a routed child; only a manual child delete could trigger it — a latent bug), but a Skip-mode `[Mutex]`/`[RateLimit]` on a message handler makes it an ordinary outcome. Fix in `Orchestrator.FinalizeParentsAsync`: `Deleted` counts as settled on both sides of the readiness check (blocking-set and has-any-settled-child), and a deleted child does not mark the parent `Failed` — deliberately-skipped work is not failed work. Final-state rule (review finding W1): any `Failed` child → `Failed` per continuation options; else any `Completed` child → `Completed`; else (settled children ALL `Deleted`) → **`Deleted`** — a cancelled batch (`CancelBatch` deletes the descendants but leaves the batch row) must finalize, and finalizing it `Completed` would be a false green. Release note 3 carries both halves; `OrchestrationTaskTests` pins all three outcomes.
6. **Saga proxies are exempt** *(added during implementation — a pinned test caught it)*: `SagaHandlerProxy<TSaga, TMessage>` is an `IMessageHandler`, so the widened guards would wrap it — but sagas manage their own execution policy (per-correlation mutex, busy/version-conflict reschedules, saga-state commits inside handler scope, §8.17), and `TimeoutPipelineBehaviorTests.IMessageRequest_PassesThroughWithoutTimeout` pins that an outer timeout must not race that machinery (documented in the sagas Limitations section). The general seam: a new marker `IPolicyExemptHandler` (`Warp.Core.Handlers`), implemented by the saga proxy; every policy behaviour skips an execution whose `IJobContext.HandlerType` implements it (cached check in `AddonAttributeResolver.IsPolicyExempt`) — declared attributes, stamped metadata and global defaults alike. Saga semantics stay byte-for-byte today's, including under global Retry/Timeout defaults.

**Consequence to state loudly:** global defaults now reach message handlers. `RetryOptions.MaxRetries` and a `PerAttempt`-scoped `TimeoutOptions.Default` are execution-applied resolver rungs, and message-routed executions now run those behaviours — a deployment with `MaxRetries > 0` configured will start retrying message-handler failures that previously failed on first attempt (behaviour change 3).

**Contract `Total` timeout on messages works, and is the one place `Total` gains ground:** the deadline is stamped at message publish and the router copies it to every child, so all handlers of a firing share one wall-clock budget measured from publish — exactly `Total`'s semantics. (Handler-declared `Total` stays rejected, same as jobs.)

## Behaviour changes (release notes)

1. `[Mutex]`, `[Semaphore]`, `[RateLimit]`, `[Timeout(Scope = PerAttempt)]` are now legal on **job handler and message handler** classes (still rejected on stream handlers and on handlers of in-memory requests, where no execution path can honour them). `AddWarp` no longer throws for that placement; it throws when the same policy family is declared on **both** the request and its handler, when `Scope = Total` is declared on a handler, and when a handler `[Timeout]` coexists with a `Total`-scoped global default.
2. **Recurring jobs now honour contract-declared `[Mutex]` / `[Semaphore]` / `[RateLimit]` / `[Timeout(Scope = PerAttempt)]`.** They silently did not before. A user who added `[Mutex]` to a recurring job type and never noticed it was inert will see serialization begin at upgrade — including `Skip`-mode surplus firings landing in `Deleted`. This can alter existing production behaviour without a code edge and needs a prominent release note. Qualification: `[Timeout(Scope = Total)]` on a recurring job type remains inert (no publish-time deadline exists on that path) — now with a one-time execution-side Warning naming the type instead of silence.
3. **Message handlers now honour all policy addons.** Three distinct activations, each a live behaviour change at upgrade: (a) contract-declared `[Mutex]` / `[Semaphore]` / `[RateLimit]` / `[Timeout]` on message types — silently inert today despite being stamped and copied to every child — become active; (b) handler-declared policy becomes possible at all; (c) **global defaults reach message handlers for the first time** — a deployment with `RetryOptions.MaxRetries > 0` or a `PerAttempt` `TimeoutOptions.Default` will see message-handler failures retried / message handlers timed out where they previously were not. Same prominence as change 2.
4. A globally-configured **`PerAttempt`** default timeout is applied at execution rather than stamped at publish. Same effective timeout; the value no longer appears in `Job.Metadata` at enqueue for jobs relying on the default. A **`Total`**-scoped default keeps publish stamping (its deadline must pre-exist execution for §8.31).
5. **Declaring `[Retry]` or `[CircuitBreaker]` on both the contract and its handler becomes a startup error.** This configuration runs today (the handler side is silently dead, shadowed by the publish-stamped contract value); it now fails loudly at `AddWarp`. The fix is deleting whichever attribute was not winning — behaviour is unchanged after the edit.
6. `RetryAttribute`'s documented precedence (`handler > contract`) is now true — by making the conflicting case unrepresentable rather than by reordering anything.

## Success criteria

1. A handler-declared `[Mutex]` serializes two concurrently-claimed jobs of the same type, on both providers, with the request type carrying no attribute — pinned with `BarrierSignal` at N=2 (§0.4/§4.7).
2. A handler-declared `[RateLimit]` and `[Timeout]` produce the same outcome, `JobLog` message and `OutcomeReason` as the contract-declared equivalent.
3. Declaring the same policy family on both axes throws at `AddWarp`, naming both types and the family — including the cross-attribute case (contract `[Mutex]` + handler `[Semaphore]`) and the newly-conflicting `[Retry]`/`[CircuitBreaker]` double declarations; the self-handling job case (request == handler) still does not throw.
4. `Scope = Total` on a handler throws at `AddWarp`; a handler `[Timeout]` under a `Total`-scoped global default throws at `AddWarp`; contract-declared `Total` still stamps the deadline at publish and still reaches the §8.31 attainment counter unchanged.
5. A recurring job whose **contract** carries `[Mutex]` serializes — the regression guard for problem 2. A recurring job type carrying `[Timeout(Scope = Total)]` logs the one-time Warning and stamps nothing.
6. With a `PerAttempt` `TimeoutOptions.Default` configured, a handler-declared `[Timeout]` wins over the global default (the #236 shape, for Timeout).
7. Metadata written by handler-axis resolution round-trips through `MetadataSerializer` and is present on the row after the first attempt (§8.16 — non-primitive metadata needs explicit roundtrip tests; `TimeoutScope`/`ConcurrencyMode`/`RateLimitStyle` are enums).
8. One dispatcher-mode (`UseDispatcher = true`) variant of criterion 1 — the two worker paths carry `HandlerType` wiring and metadata write-back separately, and §8.29/§8.33 both record a path being missed once.
9. Fetch/claim path unchanged: no new query, no new round-trip, no addon reference added to `Warp.Worker`.
10. Zero attribute resolution per attempt after the first per (process, type) — the resolver is cached, asserted by a counting test or by construction review.
11. **Message, contract axis:** a message type carrying `[Mutex]` with two registered handlers produces children that all carry the key and serialize against each other — pinned with `BarrierSignal` at N=2, both providers.
12. **Message, handler axis:** with two handlers of the same message, `[Mutex]` on one handler serializes only that handler's children; the other handler's child runs concurrently with them (barrier-pinned).
13. **§8.14 guard:** a Wait-mode concurrency requeue of a routed message child keeps `HandlerType` on the row and completes on re-fetch — the regression guard for the `ClearHandlerType` fix, on both worker paths.
14. **Message retry:** a failing message handler retries per handler-declared `[Retry]` and per global `RetryOptions`, with per-child `RetriedTimes` counting; a second handler of the same message is unaffected.
15. **Validation negatives:** `[Mutex]` on an `IStreamRequestHandler` implementation and on a handler of a plain in-memory `IRequest<T>` still throw the #242 error.
16. **In-memory isolation:** an in-memory `Send` of a request type whose handler carries `[Retry]`/`[Mutex]` gets no policy outcome and no metadata stamping — the widened behaviours' guard is asserted directly.

## Out of scope

- **CircuitBreaker stamping its resolved group** into metadata for dashboard visibility.
- **Compile-time resolution.** `WarpMediatorGenerator` already sees every request/handler pair and could emit a static policy table plus a compile-time diagnostic for double declaration (the `WHTTP001` precedent), letting publish stamp handler-declared policy with zero reflection. Attractive, but empty whenever the handler is not in the publisher's compilation, so binding-time resolution is needed underneath regardless. Layer it on later if the diagnostic is wanted at build time instead of startup.
- Dashboard surfacing of "effective policy and where it came from" on job detail.

## Risks

- **Changes 2 and 3 (recurring jobs, message handlers) are live behaviour changes at upgrade.** Mitigation: prominent release notes; the fixes are correct and the alternative (keep contract attributes inert on two first-class features) is indefensible once written down. The message change has the wider blast radius — it includes global `RetryOptions`/`TimeoutOptions` defaults reaching message handlers for the first time.
- **Change 5 (Retry/CircuitBreaker double declaration) breaks startup for configurations that run today.** The broken configs contain a silently-dead attribute, and the error message tells the user exactly which one to delete — but it is still an upgrade-blocking throw and belongs beside changes 2 and 3 in the notes.
- **A contract-declared key on a message is shared by all its handlers.** `[Mutex("k")]` on the message serializes every handler of every firing against one lock — a slow handler starves its siblings. This is the declared semantics (§1: the *event* is constrained), but it is the sharpest edge of the message support and the docs must steer resource-level concerns to the handler axis.
- **A handler attribute now applies to every request type that handler handles.** For a class implementing several `IRequestHandler<>`/`IJobHandler<>`/`IMessageHandler<>` interfaces, one `[Mutex]` covers all of them. This is the intended reading ("this handler is serialized") but must be documented, and it is asymmetric with the contract axis.
- **The widened behaviours now compose into message-type pipelines.** Retry and CircuitBreaker lose their compile-time `IJob` constraint, so DI instantiates them for message executions (and in-memory sends, where the runtime guard early-returns). Cost is one type-check per execution — but the guard placement is now load-bearing in five behaviours instead of three, and criterion 16 pins it.
- **Inheritance.** All four attributes are `Inherited = false`. A handler base class declaring `[Mutex]` will not apply to derived handlers. Keep `Inherited = false` for consistency with the contract axis; document it. (`NoRestartPublishBehavior` deliberately walks the base chain — do not copy that here without deciding it explicitly.)
- **Startup validation only sees registered pairs.** A handler resolved dynamically, or registered outside the scanned descriptor shapes, escapes the double-declaration check and falls back to resolver order — which, by design §4, is observationally harmless.

## Rejected alternatives

- **Rank the axes (`handler > contract`, or the reverse) instead of rejecting both.** Requires distinguishing "explicit `WithMutex()` at publish" from "contract attribute stamped at publish" — indistinguishable in metadata today — so it needs a provenance marker per addon, new metadata keys, and a precedence rule every user must memorize. Rejecting the conflict deletes the question.
- **Stop stamping contract attributes at publish; resolve everything at execution.** Cleaner on paper and would make the resolver's ordering directly observable, but it discards properties (1)/(2) for the contract axis, changes what `Job.Metadata` shows on a pending job, and rewrites `TimeoutPublishBehaviorTests` / `RetryPublishBehaviorTests` — all to make a distinction that §4 renders unobservable.
- **Discover the handler at publish via DI.** `JobDispatcher.DiscoverJobHandler` is just `provider.GetService(IJobHandler<T>)` (`JobDispatcher.cs:33-41`), so it resolves in any process hosting the handler. Rejected: the effective policy would then depend on *which process published*, so the same call in a monolith and in a publisher-only API would produce differently-policed jobs. Determinism traded for a dictionary lookup.
- **Resolve and stamp at claim time in the worker.** Would give true exactly-once stamping committed with the claim, but it puts reflection plus metadata serialization in the fetch/claim hot path (§0.2/§6.1) and forces `Warp.Worker` to depend on the addon packages — which it deliberately does not (`WarpWorkerService.cs:188-190`).
- **Leave it alone and document "declare on the contract".** Defensible until problems 2 and 3 are on the table: the rule is not merely inconvenient, it leaves two first-class Warp features (recurring jobs, message handlers) with no working concurrency, rate-limit or timeout policy on **either** axis.
- **Exempt Retry/CircuitBreaker from the both-axes conflict check for back-compat.** Would avoid behaviour change 5's startup break, but preserves exactly the silently-dead-attribute state this design exists to kill, and makes the conflict rule inconsistent across families ("both axes is an error, except for the two addons where it's merely useless"). One loud throw with a self-explanatory fix beats a permanent asymmetry.
- **Bring messages in scope for attributes only, keeping global defaults job-only.** Would shrink behaviour change 3's blast radius, but requires the execution behaviours to know *why* a resolver rung matched (attribute vs. options — the provenance problem again), and leaves "why doesn't my global retry policy apply to message handlers?" as a permanent FAQ. Defaults are global or they are not defaults.
- **Stamp a first-bind deadline for `Total`-scoped timeouts on recurring firings.** Would make `Total` work on the recurring path, but measured from first pickup rather than enqueue — a second, subtly different deadline semantic hiding under one attribute. Refuse-and-warn keeps `Total` meaning one thing.

## Docs & rules impact

- §8.8 — replace the #242 paragraph with the axis rule and the four-rung precedence; note the `Total` and `[NoRestart]` exceptions and why; note that messages carry policy on both axes and what each axis means there.
- §8.14 — the rule ("routed `IMessage` jobs must keep `HandlerType` on requeue") is unchanged, but note the enforcement moved from *constraint-excludes-messages* to *conditional `ClearHandlerType` in every requeue-outcome site* — new behaviours that requeue must follow it.
- §2.12 — unchanged (pipeline ordering is orthogonal).
- XML docs on `MutexAttribute`, `SemaphoreAttribute`, `RateLimitAttribute`, `TimeoutAttribute` (each currently says "on a handler it is a silent no-op and `AddWarp` rejects it") and `RetryAttribute` (precedence now true). `RetryPipelineBehavior`'s `ClearHandlerType` comment block (`:73-78`) rewritten — the constraint it documents is gone.
- `website/docs/features/` — concurrency, rate-limit, timeout, retry, recurring-jobs **and messages** pages; a shared "where do I declare a policy?" section is probably better than six copies, with the message contract-vs-handler semantics as its centerpiece.
- Release notes — the six behaviour changes above, with changes 2, 3 and 5 called out.

---

## Change manifest

Production (all in `Warp.Core` — criterion 9: nothing added to `Warp.Worker`):

| # | File | Action | Purpose |
|---|---|---|---|
| 1 | `src/core/Warp.Core/Handlers/AddonAttributeResolver.cs` | create | Shared cached handler→contract attribute resolver (design §3) |
| 2 | `src/core/Warp.Core/ServiceConfiguration.cs` | modify | Rewrite `ValidateAddonAttributesOnHandlers`: legality map + per-family both-axes conflict + `Scope=Total` on handler + handler `[Timeout]` under `Total` global default (design §4) |
| 3 | `src/core/Warp.Core/Timeout/TimeoutStartupDefaults.cs` | create | Internal marker carrying the scratch-read `DefaultScope` for validation (design §6) |
| 4 | `src/core/Warp.Core/Timeout/TimeoutServiceConfiguration.cs` | modify | `AddTimeout` applies `configure` to a scratch `TimeoutOptions`, registers the marker |
| 5 | `src/core/Warp.Core/Timeout/TimeoutPublishBehavior.cs` | modify | Stamp the global default **only when `DefaultScope == Total`**; contract-attr stamping unchanged (design §6) |
| 6 | `src/core/Warp.Core/Timeout/TimeoutPipelineBehavior.cs` | modify | Guard widens to `IMessage`; resolver rungs (handler→contract, stamped) + transient `PerAttempt` global default; `Total`-without-deadline refuse+warn (new `ILogger` dep) |
| 7 | `src/core/Warp.Core/Concurrency/ConcurrencyPipelineBehavior.cs` | modify | Guard widens; resolve+stamp Mutex/Semaphore trio when slot empty; `ClearHandlerType = request is not IMessage` |
| 8 | `src/core/Warp.Core/RateLimit/RateLimitPipelineBehavior.cs` | modify | Guard widens; resolve+stamp all five fields or none; conditional `ClearHandlerType` (2 sites) |
| 9 | `src/core/Warp.Core/Retry/RetryPipelineBehavior.cs` | modify | Drop `IJob` constraint + runtime guard; shared resolver; conditional `ClearHandlerType`; rewrite the constraint comment (design §7) |
| 10 | `src/core/Warp.Core/CircuitBreaker/CircuitBreakerPipelineBehavior.cs` | modify | Drop `IJob` constraint + runtime guard; shared resolver |
| 11 | `src/core/Warp.Core/Concurrency/MutexAttribute.cs`, `SemaphoreAttribute.cs`, `RateLimit/RateLimitAttribute.cs`, `Timeout/TimeoutAttribute.cs`, `Handlers/RetryAttribute.cs` | modify | XML docs: axis rule replaces the #242 wording |
| 12 | `.claude/rules/project-specific.md` | modify | §8.8 axis rule; §8.14 enforcement note |
| 13 | `website/docs/features/mutex.md`, `semaphore.md`, `rate-limit.md`, `timeout.md`, `recurring-jobs.md`, `website/docs/releases.md` | modify | Shared "where do I declare a policy?" section; six release-note changes |

No worker files change (`WarpWorkerService` / `WarpDispatcherWorker` / `MessageRouter` / `RecurringJobScheduler` untouched — the fixes ride the existing write-back and metadata copy).

## Test manifest

| File | Action | Covers |
|---|---|---|
| `src/tests/Warp.Tests/Core/AddonAttributeResolverTests.cs` | create | SC10 (rung order, caching) |
| `src/tests/Warp.Tests/Core/AddonAttributeHandlerValidationTests.cs` | modify | SC3, SC4 (startup halves), SC15 (legality matrix) |
| `src/tests/Warp.Tests/Features/Timeout/TimeoutPublishBehaviorTests.cs` | modify | change 4 (Total-only default stamping) |
| `src/tests/Warp.Tests/Features/Timeout/TimeoutPipelineBehaviorTests.cs` | modify | SC6 (#236 shape), handler rung, transient PerAttempt default, Total-refuse+warn (SC5b) |
| `src/tests/Warp.Tests/Features/Timeout/TimeoutIntegrationTests.cs` | modify | SC2 (handler-declared parity) |
| `src/tests/Warp.Tests/Features/Concurrency/MutexIntegrationTests.cs` | modify | SC1 (handler mutex, barrier N=2), SC7 (metadata on row), SC8 (dispatcher variant) |
| `src/tests/Warp.Tests/Features/RateLimit/RateLimitIntegrationTests.cs` | modify | SC2 (handler-declared parity) |
| `src/tests/Warp.Tests/Scheduling/RecurringJobTests.cs` | modify | SC5 (recurring contract mutex; recurring Total warns) |
| `src/tests/Warp.Tests/Messaging/MessagePolicyTests.cs` | create | SC11–SC14 (message contract/handler axes, §8.14 requeue, message retry) |
| `src/tests/Warp.Tests/Features/Retry/RetryTests.cs` | modify | SC16 (in-memory isolation) |

New test job/message/handler types go in the test project's existing pattern locations (beside the tests or `TestData/`), never in `src/demo`.

## Implementation batches

- **Batch 1 — resolver + validation (NoDb):** manifest 1–4 + resolver/validation tests. Checkpoint: build + `NoDb` category.
- **Batch 2 — Timeout scope-split:** manifest 5–6 + the three Timeout test files. Checkpoint: build + Timeout-filtered tests.
- **Batch 3 — Concurrency + RateLimit handler axis:** manifest 7–8 + Mutex/RateLimit integration tests + recurring tests. Checkpoint: build + filtered DB tests (both providers).
- **Batch 4 — Retry/CircuitBreaker widening + messages:** manifest 9–10 + `MessagePolicyTests` + Retry tests. Checkpoint: build + filtered tests.
- **Batch 5 — docs:** manifest 11–13. Checkpoint: build (XML docs are analyzer-relevant).
- **After all batches:** full suite (`dotnet test`, ~1m30s) + `dotnet format --verbosity quiet`.
