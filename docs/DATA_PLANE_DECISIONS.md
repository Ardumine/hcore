# HCore — Data Plane Micro-Decisions (§B)

> **Generated:** 1/07/2026
> **Source of truth for the design:** [DATA_PLANE_DESIGN.md](DATA_PLANE_DESIGN.md)
> **Implementation plan:** [TODO.md](TODO.md) §A

These are the §B micro-decisions that blocked the first slice of the local data
plane (§A). Each was settled before coding; the "Decision" line is what ships.

---

## B1 — Injection point

**Question.** Where do `ExposeData` / `ReadData` / `Subscribe` live? The design
doc's examples show `Host.ExposeData<T>(...)` (on `IModuleHost`), but the
project's rule is "one injected handle per concern, don't bloat `IModuleHost`."

**Decision.** A **new `IDataHost` interface**, peer to `Vfs`/`Host`/`Logger`.

- `BaseImplement` gains `IDataHost Data` + `AttachData(IDataHost)`, mirroring
  `AttachVfs` / `AttachHost` / `AttachLogger`.
- An `EmptyDataHost` no-op default (throws `NotAttached()`) is the capability
  shape for "this module doesn't get data" — same pattern as
  `EmptyModuleHost` / `EmptyModuleFileSystem`.
- A `ScopedDataHost` (bound to the owner's instance name) is what each instance
  actually receives, exactly as `ScopedModuleHost` binds owner for child ops.
  `ExposeData` is owner-bound (facet under `/proc/<owner>/<facet>`); `ReadData`
  / `Subscribe` are path-based and pass straight through to the kernel `DataHost`.

**Why.** A handle is a capability (DESIGN.md future-work #7 needs the seam); one
injected handle per concern scales (ISP) instead of a god-object; the design
doc's `Host.ExposeData` was pre-decision notation.

---

## B2 — `ReadData` on a stream facet

**Question.** `ReadData<T>` is the one-shot pull (inspection / "cat" path). For a
cell it returns the current value; for a stream — latest, or oldest unprocessed?

**Decision.** **Latest, non-draining, for both cell and stream.**

`ReadData` is a *snapshot*, not a *consume*. A one-shot reader is not a
subscriber with its own queue, so "oldest unprocessed" would require a throwaway
queue or stealing from a subscriber's queue — neither makes sense. Ordered
consumption is what `Subscribe` is for. `ReadData`'s meaning is uniform: "the
most-recent value of this facet."

---

## B3 — Bound default + overflow policy

**Question.** Default stream bound value, and confirm drop-oldest as the
overflow default.

**Decision.** **Stream default bound 64, drop-oldest, per-facet configurable.**

- `ExposeData<T>(name, kind, policy, bound: N)` — `bound` overrides; `-1`/default
  means "per kind" (cell = 1, stream = 64).
- Overflow policy = `BoundedChannelFullMode.DropOldest` for every bounded facet
  (a stale frame is worthless; blocking the producer is fatal).
- A **cell** is inherently 1-deep coalesce (bound ignored; keeps newest, drops
  intermediates). A **stream** is a bounded ordered queue, drop-oldest on
  overflow. 64 absorbs a transient stall without masking a sustained mismatch
  for too long.

---

## B4 — Thread-pool / dispatch mechanism

**Question.** `Task.Run` / `ThreadPool` vs a dedicated `Channel<T>`-per-subscriber.

**Decision.** **`Channel<T>`-per-subscriber**, bounded, `DropOldest`, with a
single async consumer loop per subscriber on the thread pool.

- `System.Threading.Channels.Channel<T>` gives the bound + overflow mode for
  free, and `ReadAllAsync(ct)` gives cancellation for free.
- A capacity-1 channel with `DropOldest` **is** the cell coalesce policy (drop
  the stale item, keep newest, deliver when the handler finishes). A capacity-N
  channel is the stream ordered queue. One mechanism unifies cell and stream.
- No thread explosion: one consumer task per subscriber (not per frame), all on
  the thread pool. No cross-subscriber head-of-line blocking: each subscriber
  has its own channel.

---

## B5 — `cat` serialization format

**Question.** How does `/proc/<m>/<facet>` serialize data for `cat`? JSON?
`ToString()`? a hook?

**Decision.** **An optional formatter hook on the facet, defaulting to
`ToString()`.**

- `ExposeData<T>(..., formatter?: Func<T, string>)`. If null, uses
  `v => v?.ToString() ?? ""`.
- The producer owns its data's inspection representation (mirrors
  `DescribeForProc()`). A producer that wants JSON passes
  `v => JsonSerializer.Serialize(v)`.
- Avoids forcing JSON serialization on arbitrary types (which may fail without
  attributes); avoids surprising `cat` failures.

---

## B6 — Consumer-skipped counter

**Question.** Shape of the counter for stream tolerate-and-continue (handler
threw → frame skipped, trip only on sustained throws).

**Decision.** **`long ConsumerSkippedCount { get; }` on `ISubscription`.**

- Counts frames skipped because the handler threw.
- Kernel overflow drops remain observable via `Sequence` gaps (compare
  consecutive deliveries: a gap = the kernel dropped something between them).
- One counter, minimal surface. A separate `KernelDroppedCount` was rejected as
  redundant (already derivable from `Sequence`).

---

## Consequences for §A

- B1 ⇒ `AttachData` is wired in `ModuleHost.Create` next to `AttachVfs` /
  `AttachHost` / `AttachLogger`; `KillLocked` calls `DataHost.NotifyProducerKilled`
  for each reaped instance (extends cascade kill to cross-tree subscribers).
- B3 + B4 ⇒ one `Channel<DataEvent<T>>` per subscriber, capacity = facet bound
  (1 for cell, 64 for stream), `DropOldest`. The consumer loop is where the
  breaker (sustained depth over T) and the sustained-throw trip are measured.
- B5 ⇒ `ProcFileSystem` adds a read-only file named `<facetName>` per facet,
  content = `formatter(currentValue)`; rebuilt on every access like `info`.
