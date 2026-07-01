# HCore — Implementation TODO

> **Generated:** 1/07/2026, 17:40
> **Source of truth for the data plane design:** [DATA_PLANE_DESIGN.md](DATA_PLANE_DESIGN.md)
> **Status conventions:** ☐ not started · ◻ micro-decision needed first · ✱ design pending · ⏸ deferred

---

## Current state of the code

The source is clean — only **two** real TODOs exist today:

- `HCore.Main/Program.cs:81` — `//Todo: Handle if mpd does not exist` (modpack descriptor missing).
- `HCore.Modules.Base/IModuleHost.cs:86` — `Kill` capability gap (documented; needs the capability model).

Services are built (`FS/etc/services/*.svc`, `ServiceCommand.cs`, init boots them). Shell has
FileSystem / Help / Process / Service commands. **Zero data-plane code exists** — no `ExposeData`,
no `Subscribe`, no `DataEvent`. The data plane is greenfield.

---

## A. Decided — implementable now (the local data plane)

Everything in [DATA_PLANE_DESIGN.md](DATA_PLANE_DESIGN.md) Parts II–VIII is decided enough to write.
Blocked only by the micro-decisions in §B (especially B1).

### A1. Contracts — `HCore.Modules.Base/` (ships first; ALC shared identity)

- ☐ New `IDataHost` interface (peer to `Vfs`/`Host`): `ExposeData<T>`, `ReadData<T>`, `Subscribe<T>`.
      *(Subject to B1 — confirm `IDataHost` vs bloating `IModuleHost`.)*
- ☐ New `DataEvent<T>` readonly struct: `{ T Data; long Sequence; long? InterFrameDelta }`.
- ☐ New `ISubscription : IDisposable`: `.State` (`Active`/`Tripped`/`Disposed`), `.DisconnectReason`.
- ☐ New enums: `SubscriptionState`, `DisconnectReason` (`Overload`/`HandlerException`/`ProducerKilled`/`Disposed`).
- ☐ New `DispatchPolicy` enum (`WaitForAll`/`Coalesce`/`OrderedQueue`/`ParallelUnordered`).
- ☐ New `FacetKind` enum (`Cell`/`Stream`).
- ☐ New `IExposedData<T>` producer handle: `Publish(T)` (+ `Set` alias for V2 parity).
- ☐ `BaseImplement`: `AttachData(IDataHost)` + `Data` field, mirroring `AttachVfs`/`AttachHost`.
- ☐ `EmptyDataHost` no-op default (throws `NotAttached()`, like `EmptyModuleHost`).
- ☐ `AssemblyInfo.cs` — confirm `InternalsVisibleTo("HCore.Main")` covers any new `protected internal` hooks.

### A2. Implementation — `HCore.Main/Internal/`

- ☐ `DataHost.cs` (new): per-facet registry, per-subscriber queues, thread-pool dispatch,
      breaker state machine, per-facet sequence counter, monotonic `InterFrameDelta` source
      (`Stopwatch.GetTimestamp()`; **not** `DateTime.Now`).
- ☐ Wire `DataHost` into `ModuleHost.Create` (inject into each instance, like Vfs/Host/Logger).
- ☐ `ScopedModuleHost` (or a `ScopedDataHost`) — owner-binding for any owner-scoped data op
      (mirrors the `ScopedModuleHost` pattern for `SpawnChild`/`KillChild`).
- ☐ `ModuleHost.KillLocked`: when reaping a producer, fire `OnDisconnected(ProducerKilled)` to
      all subscribers of its facets — extends cascade kill to cross-tree subscribers.
- ☐ Dispatch policies implemented (Part V):
  - ☐ Option 1 — Blocking (`WaitForAll`): opt-in via `Set(v, Dispatch.WaitForAll)`.
  - ☐ Option 2 — Coalesce-to-newest (Cell): **default**.
  - ☐ Option 3 — Ordered queue, bounded, drop-oldest on overflow (Stream).
  - ☐ Option 4 — Parallel unordered, bounded `ActionBlock`-style (independent items).
- ☐ Worker model: **per-subscriber queue + thread-pool dispatch** (no thread explosion, no
      cross-subscriber HOL blocking). *(Subject to B4.)*
- ☐ Handler exception policy (per-facet, Part V):
  - ☐ Cell → one-strike-and-out → `OnDisconnected(HandlerException)`.
  - ☐ Stream → tolerate-and-continue, separate consumer-skipped counter, trip on sustained throws.
- ☐ Breaker (Part VII): trip on `Overload` (queue depth ≥ threshold sustained over T) /
      `HandlerException` / `ProducerKilled`; drop queue on disconnect; re-subscribe starts fresh.
- ☐ `ModuleLogger` warnings on queue-depth threshold and on skip-count rise.
- ☐ Zero-copy local: publish the reference directly; **documented freeze-after-publish contract**
      (producer must not mutate after `Publish`). No enforcement.
- ☐ Synthetic `/proc/<m>/<facet>` read-only `data` file in `ProcFileSystem` (like `info` from
      `DescribeForProc()`), serialized for `cat`. *(Subject to B5 — format.)*

### A3. Demo

- ☐ Extend `HCore.Packages.Usb` (or add a sensor stub pack): producer `ExposeData<ScanFrame>("scan_data")`.
- ☐ A consumer module that `Subscribe`s and logs frames + sequence + delta.
- ☐ Verify end-to-end: `cat /proc/lidar/scan_data`; sequence gaps on coalesce; breaker trip on
      sustained overload; `kill /proc/lidar` fires `ProducerKilled` to the consumer.

---

## B. Decided in principle — micro-decision needed before coding

Settle these first; each blocks part of §A.

- ◻ **B1. Injection point.** The design doc shows `Host.ExposeData<T>(...)` (on `IModuleHost`),
      but the accepted rule is "one injected handle per concern, don't bloat `IModuleHost`."
      **Recommendation: new `IDataHost` peer to `Vfs`/`Host`.** Pin this before writing any contract.
- ◻ **B2. `ReadData` on a stream facet** — returns latest? oldest unprocessed? (Cell is obvious: current.)
- ◻ **B3. Bound default + overflow policy** — per-facet bound value (e.g. stream default 64) and
      confirm drop-oldest as the overflow default.
- ◻ **B4. Thread-pool choice** — `Task.Run`/`ThreadPool` vs dedicated `Channel<T>`-per-subscriber.
      Affects backpressure semantics and cancellation.
- ◻ **B5. `cat` serialization format** — JSON? the type's own `ToString`? a formatter hook on the facet?
- ◻ **B6. Consumer-skipped counter shape** for stream tolerate-and-continue — concrete field on `ISubscription`.

---

## C. Pending decision — design-level (not implementable yet)

Design work remains; these are additive layers on top of §A, not blockers for the first slice.

- ✱ **C1. AFCP transport + 9P-style remote mounts** (`DATA_PLANE_DESIGN.md` Part IX). North star;
      no protocol/packet design yet. Biggest pending piece.
- ✱ **C2. MKCall / `ModuleProxy`** — required for remote method calls (V2's was deleted; remote
      calls need a marshalling proxy back). Not designed.
- ✱ **C3. Capability model** — needed before remote mounts are production-safe; also closes the
      `Kill` gap (`IModuleHost.cs:86`). Not designed.
- ✱ **C4. Config system** — needed for mount points + per-module config injection (V2's
      `IModuleConfigManager`). Absent entirely.
- ✱ **C5. Dynamic invocation** (`[Exposed]` + `Host.Call(name, member, args)`) — DESIGN.md
      future-work #1. Agreed to design *with* data expose (same "publish a named facet" axis);
      decide whether it ships in the same pass as §A or immediately after.
- ✱ **C6. Full process lifecycle** (`start`/`stop`/`exit`/self-reap on `Run()` return) —
      DESIGN.md future-work #2. Orthogonal, but affects how a producer stops cleanly and
      interacts with `ProducerKilled`.

---

## D. Deferred (decided to defer — do not implement yet)

- ⏸ **D1. Pull face** (`IAsyncEnumerable<T>` adapter over the same per-subscriber queue) — after
      push ships. The `ISubscription` handle is already rich enough for the adapter to sit on top.
- ⏸ **D2. Pool/loaned messages** (ROS2-style pre-allocated frames, ref-counted) — after a profiler
      demands it. Ship allocate-immutable-per-frame first.
- ⏸ **D3. Remote case entirely** — ship local first; `(Sequence, InterFrameDelta)` is
      forward-compatible and will work unchanged when AFCP arrives.
- ⏸ **D4. Backoff policy specifics** — re-subscribe backoff is the consumer's job; the kernel API
      for it is not designed (and may not need to be — consumer-side concern).

---

## Existing source TODOs (small, standalone)

- ☐ `HCore.Main/Program.cs:81` — handle missing `mpd` file gracefully (currently warns/skips imperfectly).
- ☐ `HCore.Modules.Base/IModuleHost.cs:86` — `Kill` capability gap → resolved by C3 (capability model).

---

## Recommended first slice

1. **Settle B1** (injection point → `IDataHost`).
2. **Implement §A as a local-only push plane**: cell + stream, the four dispatch policies,
   `ISubscription` with the breaker, `ProducerKilled` wired into `KillLocked`, the `cat`
   inspection file, and the USB/sensor demo.
3. **No AFCP, no pull face, no pool, no capability model.**

That is a coherent, buildable first PR delivering the functional core (sensor streams between
modules) and leaving every §C item as an additive layer on top.

---

## Order of operations (suggested)

```
B1 → A1 (contracts) → A2 (impl) → A3 (demo) → [ship local data plane]
                                                ↓
                              B2–B6 (micro-decisions, fold in as encountered)
                                                ↓
                              C5 (dynamic invocation, same axis) — optional parallel
                                                ↓
                              C3 (capability) → C4 (config) → C1 (AFCP mount) → C2 (MKCall proxy)
                                                ↓
                              D1 (pull) / D2 (pool) — on demand
```
