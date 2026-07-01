# HCore — Implementation TODO

> **Generated:** 1/07/2026, 17:40
> **Source of truth for the data plane design:** [DATA_PLANE_DESIGN.md](DATA_PLANE_DESIGN.md)
> **§B micro-decisions (settled 1/07/2026):** [DATA_PLANE_DECISIONS.md](DATA_PLANE_DECISIONS.md)
> **Status conventions:** ☐ not started · ◻ micro-decision needed first · ✱ design pending · ⏸ deferred · ✅ done

---

## Current state of the code

The source is clean — only **two** real TODOs remain:

- `HCore.Main/Program.cs:81` — `//Todo: Handle if mpd does not exist` (modpack descriptor missing).
- `HCore.Modules.Base/IModuleHost.cs:86` — `Kill` capability gap (documented; needs the capability model).

Services are built (`FS/etc/services/*.svc`, `ServiceCommand.cs`, init boots them). Shell has
FileSystem / Help / Process / Service commands. **The local data plane (§A) is now implemented
and verified** — contracts, `DataHost`, dispatch policies, breaker, `ProducerKilled`, the `cat`
inspection file, and the `HCore.Packages.Sensor` demo. No AFCP / remote / capability / config yet.

---

## B. Micro-decisions — SETTLED (see DATA_PLANE_DECISIONS.md)

All six settled 1/07/2026; each informed §A:

- ✅ **B1. Injection point** → new `IDataHost` peer to `Vfs`/`Host`; `BaseImplement.AttachData` +
  `EmptyDataHost` default; `ScopedDataHost` owner-bound for `ExposeData`.
- ✅ **B2. `ReadData` on a stream facet** → latest, non-draining, for both cell and stream.
- ✅ **B3. Bound default + overflow policy** → stream bound 64, drop-oldest, per-facet configurable;
  cell always 1-deep coalesce.
- ✅ **B4. Thread-pool choice** → `Channel<T>`-per-subscriber, bounded, `DropOldest`, one async
  consumer loop on the thread pool (unifies cell cap-1 + stream cap-N).
- ✅ **B5. `cat` serialization format** → optional `Func<T,string>` formatter hook on the facet,
  defaulting to `ToString()`.
- ✅ **B6. Consumer-skipped counter** → `long ConsumerSkippedCount` on `ISubscription`; kernel
  overflow drops stay observable via `Sequence` gaps.

---

## A. The local data plane — IMPLEMENTED & VERIFIED

Everything in [DATA_PLANE_DESIGN.md](DATA_PLANE_DESIGN.md) Parts II–VIII is implemented (local-only;
§C remote layers are additive on top). Verified end-to-end with a headless smoke test:
`ReadData` snapshot, `Subscribe`/`Publish` push (seq + inter-frame delta), `cat /proc/<m>/<facet>`,
cell coalesce with sequence gaps, stream overload breaker trip, and `kill` → `ProducerKilled`.

### A1. Contracts — `HCore.Modules.Base/` ✅

- ✅ `IDataHost` interface (peer to `Vfs`/`Host`): `ExposeData<T>`, `ReadData<T>`, `Subscribe<T>`.
- ✅ `DataEvent<T>` readonly struct: `{ T Data; long Sequence; long? InterFrameDelta }`.
- ✅ `ISubscription : IDisposable`: `.State`, `.DisconnectReason`, `.ConsumerSkippedCount`.
- ✅ Enums: `SubscriptionState`, `DisconnectReason` (`Overload`/`HandlerException`/`ProducerKilled`/`Disposed`).
- ✅ `DispatchPolicy` (`Default`/`WaitForAll`/`Coalesce`/`OrderedQueue`/`ParallelUnordered`), `FacetKind` (`Cell`/`Stream`).
- ✅ `IExposedData<T>` producer handle: `Publish(T)` + `Set(T)` (V2 parity alias).
- ✅ `BaseImplement`: `AttachData(IDataHost)` + `Data` field, mirroring `AttachVfs`/`AttachHost`.
- ✅ `EmptyDataHost` no-op default (throws `NotAttached()`).

### A2. Implementation — `HCore.Main/Internal/` ✅

- ✅ `DataHost.cs`: per-facet registry, path parsing (`/proc/<instance>/<facet>`), `NotifyProducerKilled`.
- ✅ `DataFacet.cs` (`Facet<T>`): per-facet sequence counter, monotonic `InterFrameDelta`
  (`Stopwatch.GetTimestamp()`; not `DateTime.Now`), latest-value snapshot, subscriber list, `ProducerKilled`.
- ✅ `DataSubscription.cs` (`DataSubscription<T>`): per-subscriber bounded `Channel<T>` + single
  async consumer loop on the thread pool; breaker state machine.
- ✅ Wired `DataHost` into `ModuleHost.Create` (injects `ScopedDataHost` per instance, like Vfs/Host/Logger).
- ✅ `ScopedDataHost` — owner-binding for `ExposeData`; `ReadData`/`Subscribe` pass through.
- ✅ `ModuleHost.KillLocked` → `NotifyProducerKilled` for each reaped instance (extends cascade kill
  to cross-tree subscribers), leaf-first, outside the process-table lock.
- ✅ All four dispatch policies (Part V):
  - ✅ `WaitForAll` — blocking backpressure (synchronous fan-out, opt-in).
  - ✅ `Coalesce` — cap-1 channel, `DropOldest` (cell default).
  - ✅ `OrderedQueue` — cap-N channel, drop-oldest (stream default).
  - ✅ `ParallelUnordered` — cap-N channel + `SemaphoreSlim`-capped thread-pool fan-out.
- ✅ Handler exception policy (per-facet): cell = one-strike-and-out; stream = tolerate-and-continue,
  trip on sustained throws (separate `ConsumerSkippedCount`).
- ✅ Breaker (Part VII): trips on `Overload` (sustained queue depth, with hysteresis so a one-frame
  drain dip doesn't reset the window) / `HandlerException` / `ProducerKilled`; drops the queue on
  disconnect; re-subscribe starts fresh; recovery is explicit (kernel never auto-resumes).
- ✅ `ModuleLogger` warnings on breaker trip and sustained-throw trip.
- ✅ Zero-copy local by reference (freeze-after-publish contract documented on `IExposedData<T>`; not enforced).
- ✅ Synthetic `/proc/<m>/<facet>` read-only file in `ProcFileSystem` (formatter hook; rebuilt on access like `info`).

### A3. Demo ✅

- ✅ `HCore.Packages.Sensor` — `LidarImplement` (producer: `ExposeData<ScanFrame>("scan_data", Stream)`,
  background publish loop) + `SlamImplement` (consumer: `ReadData` snapshot + `Subscribe`, logs frames).
- ✅ `ScanFrame` shared type (package-local; both modules same ALC).
- ✅ Verified end-to-end: `cat /proc/lidar/scan_data`; sequence gaps on coalesce; breaker trip on
  sustained overload; `kill /proc/lidar` fires `ProducerKilled` to the consumer.

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

## Recommended first slice — DONE ✅

1. ✅ **Settled B1–B6** (see DATA_PLANE_DECISIONS.md).
2. ✅ **Implemented §A as a local-only push plane**: cell + stream, the four dispatch policies,
   `ISubscription` with the breaker, `ProducerKilled` wired into `KillLocked`, the `cat`
   inspection file, and the `HCore.Packages.Sensor` demo.
3. ✅ **No AFCP, no pull face, no pool, no capability model.**

The functional core (sensor streams between modules) ships; every §C item remains an additive
layer on top.

---

## Order of operations (suggested)

```
B1–B6 ✅ → A1 (contracts) ✅ → A2 (impl) ✅ → A3 (demo) ✅ → [local data plane SHIPPED]
                                                                     ↓
                               C5 (dynamic invocation, same axis) — optional parallel
                                                                     ↓
                               C3 (capability) → C4 (config) → C1 (AFCP mount) → C2 (MKCall proxy)
                                                                     ↓
                               D1 (pull) / D2 (pool) — on demand
```
