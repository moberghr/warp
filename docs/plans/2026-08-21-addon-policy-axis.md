# Plan — Addon policy axis (handler-declared policy, jobs + message handlers)

Spec: `docs/specs/2026-08-21-addon-policy-axis.md` (+ `.json` sidecar). Scope: new-feature. Branch: `feat/addon-policy-axis`.

## Batch 1 — Resolver + startup validation (NoDb)

**Files:** `Handlers/AddonAttributeResolver.cs` (new), `ServiceConfiguration.cs`, `Timeout/TimeoutStartupDefaults.cs` (new), `Timeout/TimeoutServiceConfiguration.cs`; tests `Core/AddonAttributeResolverTests.cs` (new), `Core/AddonAttributeHandlerValidationTests.cs`.

**Boundary:** validation + resolver only — no pipeline behaviour changes yet. The resolver is added but not yet called by any behaviour.
**Depends:** nothing.

1. `AddonAttributeResolver` (internal static, `Warp.Core.Handlers`): `Resolve<TAttr>(Type? handlerType, Type requestType)` — cached per `(typeof(TAttr), type)` in one `ConcurrentDictionary<(Type, Type), Attribute?>`; handler rung first, contract rung second, `inherit: false` on both.
2. `TimeoutStartupDefaults` (internal sealed record: `HasDefault`, `DefaultScope`); `AddTimeout` applies `configure` to a scratch `TimeoutOptions` and registers the record as a singleton instance (replace prior registration on repeat calls).
3. Rewrite `ValidateAddonAttributesOnHandlers`:
   - families: Concurrency = {Mutex, Semaphore}; RateLimit; Timeout; Retry; CircuitBreaker.
   - shape legality: `IJobHandler<>`/`IMessageHandler<>` → handler axis legal; `IRequestHandler<,>` legal iff request is `IJob`/`IMessage`; `IStreamRequestHandler<,>` illegal. Illegal shape + any family attr on handler → #242-style throw (message updated to name the legal placements).
   - legal shape: family attr on BOTH handler and request → "pick one axis" throw naming both types + family.
   - handler `[Timeout]` with `Scope = Total` → throw.
   - handler `[Timeout]` (any scope) while `TimeoutStartupDefaults` says `HasDefault && DefaultScope == Total` → throw.
   - self-handling exemption unchanged.
4. Tests: resolver rung order + caching (SC10); validation matrix (SC3, SC4-startup, SC15) — keep existing #242 tests that must still pass (stream/in-memory), flip the ones that asserted job/message-handler placement throws.

**Checkpoint:** `dotnet build src/Warp.slnx` + NoDb tests (`--filter-trait "Category=NoDb"`).

## Batch 2 — Timeout scope-split

**Files:** `Timeout/TimeoutPublishBehavior.cs`, `Timeout/TimeoutPipelineBehavior.cs`; tests: `TimeoutPublishBehaviorTests.cs`, `TimeoutPipelineBehaviorTests.cs`, `TimeoutIntegrationTests.cs`.

**Boundary:** Timeout addon only. Guard widening to `IMessage` happens here for Timeout (behaviour is inert for messages until Batch 4 only in the sense that nothing else changes — it is correct on its own).
**Depends:** Batch 1 (resolver).

1. `TimeoutPublishBehavior`: stamp the global default **only when** `Default is not null && DefaultScope == Total`. Contract-attribute stamping and Total-deadline stamping unchanged.
2. `TimeoutPipelineBehavior`: guard → `request is not IJob and request is not IMessage`; when `meta.TimeoutSeconds == null`: resolve via `AddonAttributeResolver.Resolve<TimeoutAttribute>` — if found and `Scope == Total && meta.TimeoutDeadlineUtc == null` → one-time Warning per request type (new `ILogger<...>` ctor dep, static warned-set), no stamp, no timeout; if found otherwise → stamp seconds/mode/scope into metadata; if not found and options `Default` set with `DefaultScope == PerAttempt` → apply transiently (never stamped, Retry precedent). Existing enforcement below unchanged.
3. Tests: default no longer stamped at publish for PerAttempt (change 4) and still stamped for Total; handler rung stamps + enforces; handler attr beats PerAttempt default (SC6); Total-without-deadline warns + no-ops (SC5b); handler-declared timeout integration parity (SC2).

**Checkpoint:** build + Timeout-filtered tests.

## Batch 3 — Concurrency + RateLimit handler axis

**Files:** `Concurrency/ConcurrencyPipelineBehavior.cs`, `RateLimit/RateLimitPipelineBehavior.cs`; tests: `MutexIntegrationTests.cs`, `RateLimitIntegrationTests.cs`, `Scheduling/RecurringJobTests.cs`.

**Boundary:** the two behaviours + their tests. No Retry/CB changes.
**Depends:** Batch 1.

1. Concurrency: guard widens; when `meta.ConcurrencyKey == null` resolve Mutex then Semaphore via resolver (handler→contract) and stamp the trio; `BuildRequeueOutcome` gains a `clearHandlerType` param = `request is not IMessage`.
2. RateLimit: guard widens; when `meta.RateLimitKey == null` resolve attr and stamp all five fields (or none); both reschedule outcomes get conditional `ClearHandlerType`.
3. Tests: handler-axis mutex barrier N=2 both providers (SC1) + metadata-on-row assertion (SC7) + dispatcher-mode variant (SC8); handler-axis rate-limit parity (SC2); recurring contract mutex serializes (SC5).

**Checkpoint:** build + Concurrency/RateLimit/Scheduling-filtered DB tests, both providers.

## Batch 4 — Retry + CircuitBreaker widening + message coverage

**Files:** `Retry/RetryPipelineBehavior.cs`, `CircuitBreaker/CircuitBreakerPipelineBehavior.cs`; tests: `Messaging/MessagePolicyTests.cs` (new), `Features/Retry/RetryTests.cs`.

**Boundary:** the two behaviours + message/in-memory tests.
**Depends:** Batches 1–3 (message mutex tests exercise Batch 3 code).

1. Retry: drop `, IJob` constraint; top guard `if (request is not IJob and request is not IMessage) return await next(...)`; `GetRetryAttribute` → shared resolver; reschedule outcome `ClearHandlerType = request is not IMessage`; rewrite the constraint comment block (§8.14 now enforced conditionally).
2. CircuitBreaker: drop constraint; same top guard (before any store read); attribute lookup → shared resolver.
3. `MessagePolicyTests` (new, `[GenerateDatabaseTests]`): contract mutex — children share key and serialize (SC11); handler mutex — only that handler serialized (SC12); Wait-mode requeue keeps `HandlerType` and completes (SC13); message handler retry per handler `[Retry]` + global options, per-child counts (SC14).
4. Retry tests: in-memory `Send` isolation (SC16).

**Checkpoint:** build + Messaging/Retry/CircuitBreaker-filtered tests.

## Batch 5 — Docs

**Files:** 5 attribute XML docs, `.claude/rules/project-specific.md` (§8.8, §8.14), `website/docs/features/{mutex,semaphore,rate-limit,timeout,recurring-jobs}.md`, `website/docs/releases.md`.
**Depends:** Batches 1–4 (docs describe shipped behaviour).

**Checkpoint:** build (XML docs are analyzer-checked).

## Finish

- Full suite: `dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj` (~1m30s).
- `dotnet format --verbosity quiet`.
- Behavioural diff written; spec-drift check against the JSON sidecar; two-stage review.

## Parallelism

Batches are sequential (2–4 depend on 1; 4's tests exercise 3). Within a batch, file edits are independent and test authoring can interleave.
