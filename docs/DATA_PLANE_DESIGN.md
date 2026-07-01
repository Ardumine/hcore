# HCore — Data Plane & Exposed-Data Design

> **Status:** design (not yet built). This document records the full design debate
> and the decisions reached, spanning: the V2→V3 feature gap that motivates it,
> the "data as a facet" model, the event/dispatch semantics, cross-CPU timing,
> and the AFCP remote-mount story.
> **Related:** [V2_V3_COMPARISON.md](V2_V3_COMPARISON.md) (exhaustive feature table),
> [DESIGN.md](DESIGN.md), [MODULE_HIERARCHY.md](MODULE_HIERARCHY.md).

---

## Part I — Context: what V2 had, what V3 lost

The second iteration (`Ardumine/kernel`, evaluated at `9d8526c` — the state before
`8627f42` stripped data channels) was a **distributed, networked robotics
micro-kernel**. V3 (HCore) is a **correct, single-process micro-kernel** with a
real VFS and a clean process hierarchy — but it shed the entire distributed /
data-plane / configuration story. The features V2 had and V3 lacks, in priority
order:

| Feature | V2 | V3 |
|---|:--:|:--:|
| AFCP TCP networking + binary serializer | ✅ | ❌ |
| Multi-kernel / remote kernels (`KernelDescriptor`, `RemoteRunningKernel`) | ✅ | ❌ |
| **Data channels — streaming pub/sub data plane** | ✅ (removed by `8627f42`) | ❌ |
| MKCalls — module↔kernel RPC syscall boundary | ✅ | ❌ (direct injection) |
| Transparent dispatch proxy (`ModuleProxy`/`DispatchProxy`) | ✅ | ❌ (direct dispatch) |
| Permissions / capability model (embryonic) | ✅ | ❌ (documented gap) |
| `config.json` declarative boot + per-module config | ✅ | ❌ (hardcoded + scripts) |
| Real robotics drivers (YDLidar, hectorSlam, USB) | ✅ | ❌ (USB stub only) |

V3 gains over V2: ALC isolation, a unified VFS + `/proc`, init/shell/services
PID-1 architecture, and a structurally-correct module hierarchy with cascade
kill. Full table in [V2_V3_COMPARISON.md](V2_V3_COMPARISON.md).

**The porting pivot:** V2's last commit (`8627f42`) deleted data channels from
the kernel with the note *"will get replaced by a module."* That is the deferred
TODO this document picks up — but reimagined. V2's channels were a **parallel
namespace** (`ChannelManager`, `DataChannelDescriptor`, `ConnectChannelPermission`,
channel paths distinct from module paths), a relic of its D-Bus/ROS-topic
heritage. V3's design (DESIGN.md) says the opposite: **one namespace, it ends at
the module, and a module has facets.** So the data plane is not rebuilt as V2 had
it; it is rebuilt as **exposed data facets of ordinary modules**.

---

## Part II — Core decision: data is a facet, not a channel, not a "data module"

### Kill the parallel channel namespace

V2's separate `ChannelManager`/channel-path space clashes with V3's
"one-namespace-ends-at-the-module" rule (DESIGN.md: *"the namespace contains
only entities something deliberately published; it is not the transitive closure
of everything reachable"*). A separate "channel" entity would re-introduce the
two-namespace problem the design spent paragraphs killing.

### Don't make it a "data module" either

A `DataModule : BaseImplement` species is the same mistake in a different hat:
two kinds of namespace citizens (method-modules and data-modules) instead of
one. Worse, it loses the case that matters most — **a sensor driver is both.** A
lidar module wants `SetFrameRate(int)` *and* a frame stream. Splitting one
physical device into a `LidarCtl` module and a `LidarData` module is V2's
parallel-namespace disease with extra steps.

### Data is a fourth facet of an ordinary module

A module already has three axes (MODULE_HIERARCHY.md): exposed members (callable
methods), own facets (`info`), and sub-modules. Data is a **fourth facet** — the
`data` half of the `ctl`/`data` pair DESIGN.md future-work #5 already points at:

```
/proc/lidar/
├── ctl        ← exposed methods (write commands)        [expose axis]
├── data       ← exposed stream (read/subscribe samples) [data axis]
├── info       ← status facets
└── scan0/     ← sub-modules
```

One module, four facets. No new module type, no parallel namespace.

### Reaching data: through `Host`, not `Vfs`

DESIGN.md's syscall table draws the line:

| Module sees | Kernel provides | Syscall group |
|---|---|---|
| `Vfs : IModuleFileSystem` | `ModuleFileSystemProxy` → `FileSystem` | **file calls** (bytes in the mount tree) |
| `Host : IModuleHost` | `ModuleHost` | **process/IPC calls** (module↔module) |

A lidar's `scan_data` is *live module-owned data*, not a byte store on a mount.
So you **address** it like a path (`/proc/lidar/scan_data`) but you **read** it
through `Host`, not `Vfs`. Unified addressing (Plan-9-style), separate syscall
group (DESIGN.md's addressing-vs-invocation split). Routing it through `Vfs`
would force `/proc` to become a writable, callback-driven filesystem instead of
the read-only synthetic view it is today — blurring the boundary the docs keep
clean.

### `Vfs` and `Host` stay separate (do not merge)

Temptation: merge into one syscall surface for "fewer concepts." Rejected:

- **A handle is a capability.** `EmptyModuleFileSystem`/`EmptyModuleHost` no-op
  defaults are the *shape* for "this module doesn't get that capability." Merging
  deletes the seam the future capability model (DESIGN.md future-work #7) needs.
- **You can't eliminate injection.** To call `Host.GetModuleInterface<...>(...)`
  you need `Host` already injected. `Host` is the bootstrap seed. You can only
  reduce to one injected handle, not zero — and one + lookup-every-call is worse
  on hot paths; one + lookup-once-and-cache reinvents today's `AttachVfs`.
- **It doesn't scale.** Data, config, logging, timers are coming. "Merge related
  syscalls" ends in a god-object. The rule "one injected handle per concern, add
  handles to scale" is strictly better (ISP).

**Clean rule:** *kernel-space bridges* (`Vfs`, `Host`) → injected. *User-space
services* (logger, config, a channel broker) → modules reached through `Host`.
The "reach it via `GetModuleInterface`" instinct is right for user-space
services, wrong for kernel bridges.

### Typed, not bytes

Strongly favor the typed form (`ReadData<ScanFrame>`, `Subscribe<ScanFrame>`),
consistent with DESIGN.md's *"compile-time-checked, ordinary C# dispatch, zero
overhead."* A pure Plan-9-bytes model pays a serialization tax on every in-process
frame — the hot path. **Cost:** the streamable type (`ScanFrame`) must live in
`HCore.Modules.Base` (or a shared contract assembly), same hard rule as child
interfaces (`IUsbDevice`): different `AssemblyLoadContext` = different `Type`, so
a cross-package subscriber can't compile `Subscribe<ScanFrame>` unless the type
is shared. For the remote-kernel future, the typed proxy then sits over a
serializer unchanged — same as `GetModuleInterface<T>` will.

### Design data-expose together with dynamic invocation

`[Exposed]` for dynamic method calls (DESIGN.md future-work #1) and a data
stream are the **same axis**: "publish a named surface of this module without
forcing the caller to import the interface." `Host.Call("/proc/lidar","SetFrameRate",60)`
and `Host.Subscribe<ScanFrame>("/proc/lidar/data", handler)` are siblings. Design
them in one pass or build two publishing mechanisms; do not separate them.

---

## Part III — The primitives: cell vs stream

Two distinct primitives, with different rate-mismatch semantics. V2's
`DataChannelInterface<T>` was actually a **cell** (held `LocalValue`, fired on
`Set`), not a FIFO queue — "point to a variable" describes a cell, not a stream.

| Primitive | Semantics | Right for | Overflow policy |
|---|---|---|---|
| **Cell** (latest value) | holds current value; read = current; subscribe = on-change | IMU orientation, temperature, "current scan" | coalesce to newest (drop intermediates) |
| **Stream/queue** | ordered sequence; don't drop frames | lidar→SLAM, video | bounded ring, drop-oldest |

A third primitive — a **shared key-value blackboard** (modules read/write named
slots, ROS-parameter-server style) — is a different abstraction (random-access
named state, not temporal data) and is out of scope here; do not conflate it
with the data plane.

---

## Part IV — API surface

### Producer (lidar)

A new injected capability — not a broker module (a broker would re-introduce a
central registry and the parallel-namespace problem). Following the `Vfs`/`Host`
pattern (DESIGN.md: *"adding a kernel capability = add a method to an injected
interface, implement in `ModuleHost`"*):

```csharp
public sealed class LidarImplement : ContainerImplement, ILidar, IRunnable
{
    public void Run()
    {
        var scan = Host.ExposeData<ScanFrame>("scan_data", DispatchPolicy.Cell);
        // ...hardware loop...
        scan.Publish(frame);   // fans out to every subscriber
    }
}
```

`Host.ExposeData<T>(facetName, policy)` registers a facet at
`/proc/<instance>/<facetName>` and returns a handle the module pushes to. The
kernel fans out `Publish` to every subscriber per the facet's dispatch policy.

### Consumer (another module)

Two access patterns, one address, both on `Host`, typed:

```csharp
// 1) SNAPSHOT — one-shot pull of the current/latest frame (the "cat" path)
ScanFrame latest = Host.ReadData<ScanFrame>("/proc/lidar/scan_data");

// 2) SUBSCRIBE — the real consumer hot path, pushed per frame.
//    Returns a handle; the callback is OPTIONAL — the handle's .State is
//    always observable, so a consumer that didn't wire a callback still
//    notices a trip on its next interaction (no silent footgun).
ISubscription sub = Host.Subscribe<ScanFrame>(
    "/proc/lidar/scan_data",
    (DataEvent<ScanFrame> e, CancellationToken ct) => { /* called per frame */ },
    onDisconnected: reason => { /* optional: breaker tripped / handler threw / producer killed */ });
```

`ReadData` is pull/one-shot (inspection, "current value"). `Subscribe` is
push/ongoing (the hot path). Same address, different verb — *how you interact*
is a separate axis from *what you address*.

### Shell / inspection path

For humans and scripts, `ProcFileSystem` synthesizes a read-only `data` file
node (just as it already synthesizes `info` from `DescribeForProc()`):

```
$ cat /proc/lidar/scan_data
{ "angle_min": -3.14, "ranges": [0.12, 0.13, ...] }
```

`cat` → `Vfs.OpenFileStream` → synthetic `/proc` node → calls back into the
module for the current value → serializes to bytes (JSON). Slow, snapshot,
serialized — fine for a human, wrong for a 1 kHz consumer. The programmatic path
goes through `Host`, typed and zero-copy in-process. **Two doors, one address.**

### V2's `Set`/`Get` interception model — affirmed

V2's `DataChannelInterface<T>.Set/Get` is the right *API shape*: the value is
never mutated behind the kernel's back — you always go through `Set`, which
gives HCore the interception point to fire subscribed events. Maps onto the
facet model directly: `Set` → `Publish`, `Get` → `ReadData`, event fan-out →
`Subscribe` callbacks. Keep V2's *API*; replace its *dispatch* (see Part V).

### Event payload & subscription handle

Every delivered event carries:

```csharp
public readonly struct DataEvent<T>
{
    public T Data { get; init; }                // the frame
    public long Sequence { get; init; }         // PER-FACET firing count (gap detection)
    public long? InterFrameDelta { get; init; } // publish-to-publish duration (see Part VIII)
}
```

- **`Sequence` is per-facet, not per-producer.** A subscriber to
  `/proc/lidar/scan_data` wants a contiguous sequence on *that* stream, not one
  interleaved with another facet the lidar publishes (e.g. `/proc/lidar/imu_data`
  has its own independent sequence).
- **`InterFrameDelta` earns its place as the rate-mismatch diagnostic** (see
  Part VIII): when a consumer is backed up and draining a queue, its measured
  inter-arrival times reflect its *drain* rate, not the *publish* rate — so
  `InterFrameDelta` is the only signal that reveals "producer at 5 Hz, I'm
  draining at 2 Hz → sustained mismatch." It is *not* carried for AFCP
  forward-compatibility (the wire format is designed when AFCP is designed); it
  is carried because it is non-derivable exactly in the overload case the
  breaker exists to detect.

`Subscribe` returns a handle, not `void`:

```csharp
public enum DisconnectReason { Overload, HandlerException, ProducerKilled, Disposed }

public enum SubscriptionState { Active, Tripped, Disposed }

public interface ISubscription : IDisposable
{
    SubscriptionState State { get; }            // always pullable — the mandatory signal
    DisconnectReason? DisconnectReason { get; } // null while Active
}
```

**The handle dissolves the silent-stop contradiction.** Part VII's motivation is
that a silent cut-off is the enemy — but an *optional* callback alone recreates
it for any consumer that didn't wire one. The fix: the *signal* is mandatory
(the handle's `.State` is always updated and observable), the *interruption* is
optional (the callback). A consumer that didn't wire a callback still discovers
the trip by polling `.State` on its next interaction. Reframed: *behavior*
(state updated + observable) is mandatory; *handling* (asked to be interrupted)
is optional.

**Lifecycle:**
- `Dispose()` = unsubscribe, idempotent, best-effort. On dispose, stop
  dispatching *new* frames, let any in-flight callback finish, then retire the
  worker — do **not** block dispose, do **not** interrupt mid-callback (can't
  safely in .NET).
- **Re-subscribe = call `Subscribe` again.** A tripped subscription is already
  dead; disposing it is a no-op. Re-subscribe starts **fresh** — the subscriber
  does *not* get the backlog it missed during the dead period (catch-up is a
  facet policy, not a reconnect behavior).
- **Backoff on re-subscribe is the consumer's job**, not the kernel's — the
  consumer is the one that knows when it's un-stuck. The kernel can't detect
  recovery, so it must not auto-resume.

### Push vs pull — two faces of one primitive

`Subscribe` inherits V2's callback (push) model. The pull alternative
(`await foreach` / `IAsyncEnumerable<T>`) was considered and **reframed**: push
and pull are not rival primitives, they are two faces of the *same* per-subscriber
queue/worker (already designed in Part V). Push exposes it via callback; pull
exposes it via `IAsyncEnumerable<T>`.

Key insight: **pull does not eliminate the buffer/overflow problem.** If the
consumer doesn't pull, frames still have to be buffered or dropped at the
producer — so pull is a backpressure *expression*, not a backpressure *solution*.
The queue is still there. Therefore the primitive is chosen in Part III
(cell vs stream); push/pull is just the face.

- **Ship the push face first.** It's the hot path, lower-latency for tight
  control/SLAM loops (no async state machine to resume), and matches ROS lineage.
- **The pull face is a thin adapter later** — an `await foreach` over a
  `Channel<T>` that a push-subscription writes into. A one-day add-on, not an
  architectural commitment. The `ISubscription` handle above is already rich
  enough (dispose + state + callback) for the adapter to sit on top.

---

## Part V — Dispatch policies (the four options)

### Axis framing

The four options are not orthogonal; they are combinations of three axes plus a
fourth (the bound) that turns unbounded options into memory leaks:

- **Axis A** — does `Set` block the producer? (Option 1 = yes; 2/3/4 = no)
- **Axis B** — what happens to values a slow consumer missed? (2 = coalesce; 3/4 = queue)
- **Axis C** — if queued, is order preserved? (3 = ordered; 4 = unordered/parallel)
- **Axis D** — is the queue bounded? (must be yes; overflow policy per-facet)

### The four policies

**Option 1 — Blocking (backpressure).** `Set` waits for all consumers' handlers
to finish, then continues. Producer rate collapses to the slowest consumer. Right
when a slow consumer *should* slow the producer (actuator back-pressuring a
planner); catastrophic for a sensor (one slow consumer stalls the driver). V2
did this (`Parallel.For` in `Set`). **Opt-in via `Set(v, Dispatch.WaitForAll)`,
never the default.**

**Option 2 — Fire-and-forget, coalesce to newest (cell).** `Set` returns
immediately; the consumer's callback runs on its own worker; if a new `Set`
arrives while the handler is still running, only the latest is retained and
delivered when the handler finishes. Correct for "current value" (IMU, temp) —
dropping intermediates isn't loss, the consumer only wants the current state.
**Default for general exposed data.**

**Option 3 — Fire-and-forget, ordered queue, bounded (stream).** `Set` returns
immediately; each frame is enqueued per-subscriber; the consumer drains in
order. On overflow: drop-oldest (a stale frame is worthless; blocking the
producer is fatal). Right for lidar→SLAM (transient stalls buffer, then catch
up). **Stream facets.**

**Option 4 — Fire-and-forget, parallel unordered, bounded (independent items).**
Each item dispatched to a bounded-parallelism worker pool (e.g. `ActionBlock<T>`
with `MaxDegreeOfParallelism = N`, **not** "new thread per `Set`" — that's a
thread explosion). For independent items where order doesn't matter (DB writes,
per-frame independent ML inference). It's Option 3 + "unordered + parallel";
the genuinely new axis is **ordered vs unordered**.

### Producer never blocks (default); per-subscriber isolation

By default `Set`: swaps the value, snapshots the subscriber list, dispatches
each callback to that subscriber's **own** queue/worker, returns immediately.
Each subscriber has its own queue — a slow/throwing consumer cannot stall the
producer or other subscribers; fan-out is parallel.

### Worker model

- **Per-subscription dedicated worker** is wrong at scale (N subscribers = N
  threads).
- **Single shared queue** risks cross-subscriber head-of-line blocking.
- **Chosen: per-subscriber queue + thread-pool dispatch.** Each subscriber has
  its own queue (no cross-subscriber HOL blocking), and each frame is dispatched
  to a shared thread pool (no thread explosion). This is the synthesis — the
  per-subscriber queue is the isolation unit; the pool is the execution unit.

### Handler exception policy (per-facet)

Part V specifies *overflow* policy but must also specify *error* policy: what
happens when a callback throws? The policy is **per-facet**, mirroring the
cell/stream split:

- **Cell facets — one-strike-and-out.** A throwing handler stops the event,
  warns, and fires `OnDisconnected(HandlerException)`. The handler is just
  observing current state; a throw means something is wrong — fail loud, don't
  swallow.
- **Stream facets — tolerate-and-continue.** Sensor data is noisy; a single
  malformed frame shouldn't permanently sever SLAM's subscription. A throw is
  logged, that frame is skipped, and delivery continues. Trip only on
  *sustained* throws (same threshold×duration shape as the overload breaker).

  Observability cost: `Sequence` gaps only reveal *kernel* drops, not *consumer*
  throws — so a skipped-by-throw needs a separate "consumer-skipped" counter on
  the `ISubscription` handle, not a gap in `Sequence`.

In both cases, the follow-throughs are the same as the overload breaker: typed
`DisconnectReason`, drop the subscriber's queue on disconnect (leak prevention),
re-subscribe starts fresh.

### V2's `Parallel.For` flaw — copy the API, not the dispatch

V2's `LocalDataChannelInterface.Set` did `Parallel.For(0, Events.Count, i =>
Events[i](Data))` — which **blocks the producer's thread until every handler
returns**, coupling producer rate to the slowest consumer. V2's channel *API*
(`Set`/`Get`/events) is good; V2's *dispatch* is wrong for real-time. Replace
`Parallel.For` with async per-subscriber dispatch.

---

## Part VI — Rate mismatch, zero-copy, and backpressure

### The mutation trilemma

For zero-copy (subscribers hold a reference to the producer's object), you can
have at most **two** of:

1. **Zero-copy** — subscribers hold a reference, no copy on `Set`.
2. **Buffer reuse** — producer reuses one buffer and mutates it in place each frame.
3. **No torn reads** — subscribers never see a half-overwritten value.

Failure (zero-copy + buffer-reuse ⇒ torn reads):
```csharp
ScanFrame frame = new ScanFrame();        // ONE buffer, reused
while (running) {
    hardware.ReadInto(frame.Ranges);      // mutates SAME object in place
    channel.Set(frame);                   // subscribers got a REF to this object
}
// subscriber's handler(frame) may see frame.Ranges mid-overwrite → garbage
```

### Decision: zero-copy local, copy-over-AFCP remote

**Local:** zero-copy by reference, with a **freeze-after-publish contract** (not
enforced): after `Set(x)`, the producer treats `x` as immutable and never touches
it again. The producer therefore **allocates a fresh immutable frame per publish**
(gives up buffer-reuse). This is exactly V2's effective semantics (reference
stored + passed to events; caller's responsibility not to mutate after). The
contract is documented, not enforced — if a producer breaks it, torn reads are
its own bug. This is how Go channels, Erlang terms, and most actor systems
handle it.

The allocation cost is usually negligible: a 1 kHz lidar with a 360-float scan
= ~1.5 KB × 1000/s = 1.5 MB/s of Gen0 allocation — effectively free for modern
GC. Only huge-payload/high-rate streams (4K @ 60 fps ≈ 240 MB/s) need
**pool/loaned messages** (pre-allocated frames, ref-counted, returned to pool) —
deferred until a profiler demands it.

**Remote (AFCP):** serialize once on the wire per subscriber. Unavoidable.
Consistent with `GetModuleInterface<T>` (local = real object; remote = proxy).

### Catch-up vs stay-current (per-facet)

When a consumer falls behind then speeds up:
- **Catch-up** (ordered queue, Option 3): the slow phase buffers; the fast phase
  drains the backlog. Right for **offline/mapping** SLAM (completeness wins).
  Wrong for **real-time localization** SLAM — draining a backlog is a *latency
  spike* (processing N-second-old frames while the robot is *now* elsewhere).
- **Stay-current** (coalesce, Option 2, or 1-deep latest): drop stale, stay on
  recent. Right for real-time.

Per-facet choice at `ExposeData` time. The bound is the tuning knob (big = more
backlog held, bigger latency spike; small = drops sooner).

### Warnings

HCore logs when a subscriber's queue depth or coalesce-skip count crosses a
threshold. Per-subscriber, not global. Cheap, invaluable for debugging.

### The SLAM hard truth

A **sustained** rate mismatch (5 Hz in, 2 Hz out forever) is not solvable by any
queue policy: drop → lose frames; block → stall sensor → system failure; queue
unbounded → OOM. The real fix is **architectural**: decimate (subscribe every
Nth frame), publish at consumer rate, or process summaries. "Don't drop" is a
policy for *transient* stalls, not sustained mismatch. And for SLAM specifically,
**stale frames processed as-if-current corrupt the map** (integrating an old scan
at a wrong pose) — so SLAM usually wants *bounded staleness* (fresh data more
than complete data), not "all frames forever."

---

## Part VII — Circuit breaker

For sustained overload (or handler failure, or producer death), fail loud rather
than silently degrade forever.

### Three trip causes → same disconnect path

The breaker is not only for overload. Three conditions funnel into the same
`OnDisconnected` path, distinguished by the typed `DisconnectReason` (Part IV)
so the consumer can tell them apart:

1. **`Overload`** — sustained queue overflow (the original breaker case).
2. **`HandlerException`** — a callback threw (cell: one-strike; stream: sustained
   throws — see Part V).
3. **`ProducerKilled`** — the producing instance was reaped (cascade `Kill` or
   direct `Kill`). When `ModuleHost.KillLocked` removes the producer, every
   subscriber to any of its data facets receives `OnDisconnected(ProducerKilled)`.
   This extends cascade kill (MODULE_HIERARCHY.md) to cross-tree subscribers.

### Escalation policy (ordered-queue facet, Overload case)

1. **Normal:** deliver each frame in order to each subscriber's queue.
2. **Subscriber falls behind:** buffer up to the bound.
3. **Bound overflow:** drop-oldest **and warn** (queue depth high).
4. **Sustained overload** (queue depth ≥ threshold sustained over T seconds):
   **trip the breaker** — stop delivering, set `ISubscription.State = Tripped`,
   fire `OnDisconnected(Overload)`.
5. **Consumer recovers** → explicitly re-subscribes → delivery resumes.

### Design rules

- **Trip threshold** = queue depth sustained over a duration (e.g. "≥ 90% full
  for > 2 s"), not a single spike — a one-frame spike isn't overload.
- **The cut-off is observable by construction, not by callback.** The
  `ISubscription` handle's `.State` is always updated and pullable (Part IV), so
  a consumer that didn't wire `onDisconnected` still discovers the trip on its
  next interaction. This dissolves the original "mandatory behavior, optional
  handling" contradiction — the *signal* is mandatory, the *interruption* is
  optional. No silent footgun.
- **On disconnect, drop the subscriber's queue.** Otherwise it's a leak — frames
  keep arriving from the producer into a queue no one drains.
- **Re-subscribe starts fresh.** The subscriber does *not* get the backlog it
  missed during the dead period. Catch-up is a facet policy, not a reconnect
  behavior.
- **Recovery is explicit re-subscribe, not auto-resume.** The kernel cannot
  detect when the consumer is un-stuck; only the consumer knows. Auto-resume
  has no verifiable resumption condition.
- **Backoff on re-subscribe is the consumer's job** (Part IV) — the kernel must
  not auto-resume, and should not impose its own backoff (it can't judge
  readiness). Prevents thrash (subscribe → overload → cut → instant resubscribe
  → overload…).

---

## Part VIII — Timestamps & cross-CPU timing

### The cross-CPU clock problem is fundamental

`Stopwatch.GetTimestamp()` is monotonic *per machine*; on CPU B it means nothing
relative to CPU A's. But so is wall-clock without PTP/NTP. **Absolute
cross-machine time is unsolvable without clock sync, period.** The goal is not
"pick a better clock"; it's "don't pretend to give cross-machine absolute time —
give the consumer only what's *portable*."

### Portable vs non-portable fields

| Field | Gives the consumer | Portable across AFCP? |
|---|---|---|
| **Sequence** (firing count) | completeness — "did I miss frames? #5→#8 = missed 6,7" | yes (integer, exact) |
| **InterFrameDelta** (publish-to-publish duration) | production cadence — "sensor is at 5 Hz" | yes (a *duration*, not an absolute time; clock drift is ppm-noise at sensor rates) |
| **Arrival gap** (measured on consumer's own clock) | liveness — "no frame in 500 ms" | local-only (fine — it's the consumer's clock) |
| Absolute age / one-way transit latency | "this frame is 300 ms old" | **no — impossible without clock sync** |

This is the ROS2/DDS approach: sequence numbers + per-source arrival tracking;
PTP only if you truly need it. The consumer gets everything it can *portably*
know, and measures liveness on its own clock.

### Rules

- **First frame has no predecessor** → `InterFrameDelta` is null on frame #0.
- **Delta is publish-to-publish**, not delivery-to-delivery (else it mixes in
  transport jitter and stops being a producer property).
- **Physical capture time** (when the lidar fired, vs when the module called
  `Set`) is producer-domain — the producer puts `CapturedAt` inside the payload
  if it cares. The kernel does not manage it.
- **Local clock:** use a high-res monotonic source (`Stopwatch.GetTimestamp()`
  style) exposed as a `long`. Do **not** use `DateTime.Now` (wall-clock, subject
  to NTP jumps/leap seconds — wrecks SLAM's time deltas).

### Why `InterFrameDelta` is kept (the overload diagnostic)

A review challenged `InterFrameDelta` as gold-plating: "derive cadence from
`Sequence` + your own arrival times; add the field when AFCP lands." That
derivation is **valid only when the consumer is keeping up** — arrival interval
≈ production interval when transport is ~0 locally. It is **false when the
consumer is backed up and draining a queue**: the inter-arrival times the
consumer measures are its *drain* rate, not the *publish* rate. Both look like
2 Hz to the consumer's clock when the producer is at 5 Hz and the consumer is
falling behind.

That is exactly the scenario the breaker exists to detect. `InterFrameDelta` is
the one signal that reveals "producer cadence 200 ms, my drain 500 ms →
sustained mismatch" — non-derivable from arrivals in the overload case. So it is
not carried for AFCP forward-compatibility (the wire format is designed when AFCP
is designed, and a struct gaining a field later is free for in-process
recompile); it is carried because it is the **rate-mismatch diagnostic for the
core failure mode the breaker handles**, and it is most useful precisely when
arrivals cannot derive it. The AFCP-forward-compat framing was the weak
justification and is dropped; the local-diagnostic framing earns its place.

### Defer the remote case

Ship local-only first (where `Stopwatch` is perfectly fine). The sequence+delta
scheme carries only portable fields, so it will work unchanged when AFCP arrives
— but that is a side benefit, not the reason the fields exist. No corner to
paint out of.

---

## Part IX — AFCP: remote modules as namespace mounts (9P-over-AFCP)

### The mount model

Instead of V2's first-class "remote kernels with a `Guid` registry," make
remoteness **just a path prefix**: HCore instance B serves its `/proc`; instance
A mounts it at `/other_hcore`. A caller on A addresses B's lidar as
`/other_hcore/proc/lidar/scan_data` — same `Host.Subscribe<T>(...)` verb, no new
API. This is essentially **9P over AFCP**, and 9P is exactly the prior art
DESIGN.md already cites (Plan 9). Remoteness is not a first-class concept; it's
a path prefix. More elegant than V2's `RemoteRunningKernel`.

### It is three layers, not one

A mount gives you **addressing**. Three different things ride on that addressing,
each needing its own transport:

| What you do over the mount | Nature | Needs |
|---|---|---|
| `ls /other_hcore/proc`, `cat .../info`, snapshot `ReadData` | file-like, pull, read-only | a `RemoteFileSystem : IVirtualFileSystem` translating VFS ops → AFCP packets. **The NFS part.** Reuses the existing longest-prefix mount substrate. |
| `Subscribe` to a live `data` facet | push, ongoing stream | AFCP event-stream (V2's `AddEventRemote`/`NoitifyChannelDataChanged` bones). **Not** file-read over the wire. |
| Call `ILidar.SetFrameRate(60)` on B's module | RPC, request/response | a marshalling proxy — `GetModuleInterface<T>(remotePath)` returns a proxy. **The MKCall/`ModuleProxy` layer V3 deleted.** You can't `cat` your way into a method call. |

The mount is **layer 1** (presence + listing + snapshot reads). Layer 2
(subscribe-push) and layer 3 (method-call proxy) are separate and mandatory for
streaming and remote calls. DESIGN.md already separates addressing from
invocation — this is consistent, not contradictory. **Do not** let the elegance
of "mount everything" tempt you into routing the 1 kHz stream through file-read
semantics over the wire; streams stay subscribe-push even when the path is remote.

### Gaps the mount model surfaces

- **Capability.** If A mounts B's `/proc`, can any module on A call any method
  on B? Wide-open trust boundary — the NFS `root_squash`/export-list problem. V3
  has no capability model yet. A trusted-LAN first cut is fine; production needs
  the mount to carry authority. Remote mounting amplifies the capability-model
  need.
- **Config.** Mounts need mountpoints ("mount B at `/other_hcore`"), decided
  somehow — a `mount` shell command or a config file. V3 has neither (no config
  system; boot is hardcoded). This pulls in the config gap.

### Verdict

The NFS/9P-style mount is the right model for remote modules — the most
HCore-consistent way to do it, and better than V2's first-class remote-kernel
concept. Build it as layer 1; know that layers 2 and 3 must follow for streaming
and calls; go in expecting a capability story and a mount-config story. It is a
good north star, not a small task.

---

## Part X — Open questions / deferred

- **Pool/loaned messages** (ROS2-style pre-allocated frames, ref-counted, returned
  to pool) — deferred until a profiler points at a specific stream's allocation
  cost. Ship allocate-immutable-per-frame first.
- **Capability model** for `Kill` and for remote mounts — today unrestricted
  (documented gap). Needed before remote mounting is production-safe.
- **Config system** — for mount points and for per-module config injection (V2's
  `IModuleConfigManager`). Currently absent.
- **Dynamic invocation** (`[Exposed]` + `Host.Call(name, member, args)`) — design
  together with data expose; they are the same "publish a named facet" axis.
- **Full process lifecycle** (`start`/`stop`/`exit`/self-reap) — orthogonal to
  the data plane; V3 currently has `Spawn`+`Kill` only.

---

## Summary — the minimal defensible data plane

- **Model:** data is a facet of an ordinary module at `/proc/<m>/<facet>`, not a
  parallel channel namespace and not a "data module" type.
- **Access:** through `Host` (IPC syscall group), typed; `ReadData<T>` (snapshot)
  + `Subscribe<T>` (push, returns `ISubscription`). `cat` works via a synthetic
  `/proc` file for inspection.
- **Subscription handle:** `ISubscription : IDisposable` with always-pullable
  `.State` / `.DisconnectReason` — the *signal* is mandatory, the *callback* is
  optional. No silent footgun. Push ships first; pull (`IAsyncEnumerable<T>`) is
  a thin adapter over the same per-subscriber queue, later.
- **Primitives:** cell (coalesce-to-newest) vs stream (bounded ordered queue,
  drop-oldest). Per-facet. `Sequence` is per-facet.
- **Dispatch:** four policies (blocking / coalesce / ordered-queue / parallel-
  unordered). Default = fire-and-forget coalesce. Producer never blocks by
  default; per-subscriber queue + thread-pool dispatch (no thread explosion, no
  cross-subscriber HOL blocking). Handler-exception policy is per-facet (cell:
  one-strike-and-out; stream: tolerate-and-continue with a separate skip
  counter, trip on sustained throws).
- **Zero-copy:** local, by reference, freeze-after-publish contract (allocate
  immutable per frame). Remote copies over AFCP. Pool/loaned deferred.
- **Backpressure / breaker:** bound every queue; warn on overflow; circuit-break
  on sustained overload *or* handler failure *or* producer death — three trip
  causes funneling into one typed `DisconnectReason`
  (`Overload`/`HandlerException`/`ProducerKilled`). Drop the queue on disconnect;
  re-subscribe starts fresh; recovery is explicit (kernel never auto-resumes);
  backoff is the consumer's job.
- **Timing:** per-frame `(Sequence, InterFrameDelta)`. `InterFrameDelta` is kept
  as the **rate-mismatch diagnostic** — non-derivable from arrivals when the
  consumer is backed up (drain rate ≠ publish rate), i.e. exactly the overload
  case. Absolute cross-machine time is impossible without PTP and not attempted.
- **Remote:** AFCP mount (9P-style) — remoteness as a path prefix; three layers
  (mount/snapshot, subscribe-push, MKCall-proxy).
- **Defer:** remote case ships after local; pool/loaned ships after a measured
  need; capability/config ship before remote is production-safe.
