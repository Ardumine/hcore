# HCore — Implementation TODO

> **Generated:** 1/07/2026, 17:40
> **Source of truth for the data plane design:** [DATA_PLANE_DESIGN.md](data-plane/DATA_PLANE_DESIGN.md)
> **§B micro-decisions (settled 1/07/2026):** [DATA_PLANE_DECISIONS.md](data-plane/DATA_PLANE_DECISIONS.md)
> **Status conventions:** ☐ not started · ◻ micro-decision needed first · ✱ design pending · ⏸ deferred · ✅ done

---

## Current state of the code

The source is clean — only **one** real TODO remains:

- ✅ `HCore.Main/Program.cs` — missing `mpd` descriptor now handled (skip + clear warning, no
  empty-file side effect). Resolved 1/07/2026.
- `HCore.Modules.Base/IModuleHost.cs:86` — `Kill` capability gap (documented; needs the capability model).

Services are built (`FS/etc/services/*.svc`, `ServiceCommand.cs`, init boots them). Shell has
FileSystem / Help / Process / Service / **AFCP** commands. **The local data plane (§A) is
implemented and verified.** **AFCP Layer 1 (§C1) — remote mounts + Sync + Read — is implemented
and verified** via the `afcp test` loopback self-test (see [AFCP.md](afcp/AFCP.md)). **§C7a — remote
VFS writes (Write/MkDir/Remove) — is implemented and verified**, same self-test plus a manual
loopback shell check (`mkdir`/`write`/`append`/`cat`/`mv`/`touch`/`rm`/`rmdir` through a mounted
peer). Move/rename works over a remote mount by composing the existing `Write`+`MkDir`+`Remove`
primitives — no dedicated wire message (see §C7a below). Fixed a latent bug surfaced by this work:
the AFCP serializer's unmanaged-array fast path threw `IndexOutOfRangeException` serializing a
zero-length array (e.g. an empty file's `byte[]`) — `Ldelema` on element 0 of an empty array always
bounds-checks even though the body write is skipped for zero length; fixed in
`AFCP/Serializer/Serializers/ArraySerializer.cs`. Also fixed a latency bug found via manual
testing over `/remote`: no TCP connection ever set `NoDelay`, so every AFCP frame's two writes
(length prefix, then payload) sat behind Nagle's algorithm waiting on the peer's delayed ACK —
100-250ms per shell command on loopback, down to single digits after setting
`TcpClient.NoDelay = true` in `TcpConnection`'s constructor (the one place both the connect and
accept paths construct a connection). **AFCP Layer 2 (subscribe-push, §C7b) — transparent
remote `Data.Subscribe<T>` over a mount — is implemented and verified** via the extended
`afcp test` (raw-client push + the `RemoteSlam` demo consumer over a loopback mount +
`ProducerKilled` on kill). AFCP Layer 3 (MKCall), the capability model, and the config system
remain.

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

Everything in [DATA_PLANE_DESIGN.md](data-plane/DATA_PLANE_DESIGN.md) Parts II–VIII is implemented (local-only;
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

- ✅ **C1. AFCP transport + 9P-style remote mounts** (`DATA_PLANE_DESIGN.md` Part IX) —
      **Layer 1 DONE** (mount + Sync + Read over TCP, verified via the `afcp test`
      loopback self-test). The standalone `AFCP/` library (5-layer stack: Transport
      → Framing → Multiplex → IL-emit Serializer → Protocol) and the kernel-space
      bridge (`AfcpKernelService` + `VfsAfcpProvider` + lazy `RemoteFileSystem`,
      driven by the `afcp` shell command) are implemented. The serve side is a
      generic VFS proxy over the kernel `FileSystem` — it serves the **entire root**
      (`/proc`, `/etc`, `/dev`, `/packs`, ...), and live `/proc` facets stay fresh
      because `ProcFileSystem` rebuilds them on every server-side read. See
      [AFCP.md](afcp/AFCP.md). The bridge is kernel-space for now; migrating it to an
      `HCore.Packages.Afcp` package needs `IVirtualFileSystem` moved to Base + a
      proc-view/mount contract (documented in AFCP.md "Migration path"). The
      follow-up gaps are tracked under C7.
- ✱ **C2. MKCall / `ModuleProxy`** — required for remote method calls (V2's was deleted; remote
      calls need a marshalling proxy back). Not designed. (= AFCP Layer 3.)
- ✱ **C3. Capability model** — needed before remote mounts (especially **writes**) are
      production-safe; also closes the `Kill` gap (`IModuleHost.cs:86`). Not designed.
      Today everything is wide-open trusted-LAN (documented gap, same stance as `Kill`).
- ✱ **C4. Config system** — needed for mount points + per-module config injection (V2's
      `IModuleConfigManager`). Absent entirely.
- ✱ **C5. Dynamic invocation** (`[Exposed]` + `Host.Call(name, member, args)`) — DESIGN.md
      future-work #1. Agreed to design *with* data expose (same "publish a named facet" axis);
      decide whether it ships in the same pass as §A or immediately after.
- ✱ **C6. Full process lifecycle** (`start`/`stop`/`exit`/self-reap on `Run()` return) —
      DESIGN.md future-work #2. Orthogonal, but affects how a producer stops cleanly and
      interacts with `ProducerKilled`.

### C7. AFCP follow-ups (Layer 1 shipped — these are the remaining gaps)

- ✅ **C7a. Remote VFS writes.** Added `Write` (create/overwrite a file), `MkDir`,
      `Remove` message types (`AFCP/Protocol/MessageType.cs`, `Messages.cs`); server side
      (`VfsAfcpProvider`) delegates to `FileSystem.CreateFile`/`MkDir`/`DeleteFile`; mount
      side (`RemoteDirectory`/`RemoteFile` in `HCore.Main/Vfs/RemoteFileSystem.cs`) wires
      `CreateDirectory`/`TryDelete`/`CreateFile`/`Write`/`GetStream` to the new
      `AfcpClient.MkDirAsync`/`RemoveAsync`/`WriteAsync`; `RemoteFileSystem.IsReadOnly` is
      now `false`. **No dedicated `Move` message** — `HCore.Main.Vfs.FileSystem.Move`
      already decomposes every move into `CreateFile`/`CreateDirectory`/`TryDelete`/
      `ReadAllBytes` against the generic VFS primitives (same-mount only, a pre-existing
      constraint), so `mv`/`rename` over a remote mount works for free once
      `Write`/`MkDir`/`Remove` are wired — at the cost of N round-trips per file instead
      of one, acceptable for a trusted-LAN first cut. `Remove` maps 1:1 onto
      `IVirtualDirectory.TryDelete` (single node, refuses a non-empty directory, no
      recursion — matches local `HostDirectory` semantics). No typed errors (`Success`
      bool + `Error` string, same minimalism as `Read`/`Sync`) — that's C7d. No capability
      model for this cut (trusted-LAN gap, same stance as `Kill` — tracked by C3); a
      mounting peer can now write anywhere under the served root, not just read it.
      Verified via the extended `afcp test` self-test (mkdir/write/cat/rm/rmdir on a
      loopback mount) plus a manual shell check including `mv` and `touch` (empty file).
- ✅ **C7b. Layer 2 — subscribe-push over the wire.** DONE (Option A, transparent).
      A remote consumer subscribes with the ordinary `Data.Subscribe<T>("/mnt/proc/…/facet")`
      and neither knows nor cares the facet is remote (9P-style: remoteness is a path
      prefix). Server side: `VfsAfcpProvider.Subscribe` resolves the facet via
      `DataHost.FindFacet`, opens a non-generic `IFacet.SubscribeRaw` subscription, and
      streams serialized `EventNotify` frames through the `IAfcpSubscriptionSink`;
      `ProducerKilled` → `sink.ProducerGone`. Client side: `DataHost` consults
      `FileSystem.TryResolveMount`, and for a remote mount delegates to
      `RemoteFileSystem.SubscribeData<T>` (via the internal `IRemoteDataSource`), which
      wraps the `AfcpClient` subscription in a `RemoteSubscription<T>` adapter (bounded
      channel + single consumer loop, so the handler is never invoked concurrently —
      matching the local single-consumer contract). Fixed a subscription leak: a peer
      dropping the TCP link without `Unsubscribe` now tears down its server-side
      subscriptions (`PeerSession.DisposeAllSubscriptions`). Verified via the extended
      `afcp test`: a raw client gets live pushed frames (advancing `Sequence`, non-empty
      `Data`); the new `HCore.Packages.Sensor.RemoteSlam` demo consumer receives typed
      `ScanFrame`s over the loopback mount through the transparent path; and killing the
      lidar trips the consumer with `ProducerKilled`. **Gotcha surfaced:** this is the
      first time a facet value is ever serialized (the local plane passes by reference,
      `cat` uses the text formatter), so `ScanFrame` needed a parameterless constructor —
      the AFCP `ClassSerializer` requires one. Any facet value type that crosses a remote
      subscribe must be AFCP-serializable.
- ✱ **C7c. Layer 3 — MKCall proxy** (= C2). `GetModuleInterface<T>(remotePath)` returns a
      marshalling proxy. Not designed.
- ☐ **C7d. Typed errors over the wire.** A missing file, a permission error, and a
      "not a directory" all collapse to `Exists=false` / empty listing. No error response
      type for `Sync`/`Read` — add one so failures are distinguishable.
- ☐ **C7e. Large-file streaming.** `Read` returns the whole file in one frame. Fine for
      `/proc` facets and small config; a chunked/streaming read is needed for big files.
- ☐ **C7f. Reconnection.** If the TCP link drops, `RemoteFileSystem` throws on next access;
      no auto-reconnect / mount health tracking.

---

## D. Deferred (decided to defer — do not implement yet)

- ⏸ **D1. Pull face** (`IAsyncEnumerable<T>` adapter over the same per-subscriber queue) — after
      push ships. The `ISubscription` handle is already rich enough for the adapter to sit on top.
- ⏸ **D2. Pool/loaned messages** (ROS2-style pre-allocated frames, ref-counted) — after a profiler
      demands it. Ship allocate-immutable-per-frame first.
- ⏸ **D3. Remote case — Layer 3** — Layer 1 (mount/snapshot, §C1) and Layer 2
      (subscribe-push, §C7b) are done; Layer 3 (MKCall) remains. `(Sequence,
      InterFrameDelta)` proved forward-compatible: Layer 2 carries them unchanged over
      the wire (`EventNotify`).
- ⏸ **D4. Backoff policy specifics** — re-subscribe backoff is the consumer's job; the kernel API
      for it is not designed (and may not need to be — consumer-side concern).

---

## Existing source TODOs (small, standalone)

- ✅ `HCore.Main/Program.cs` — missing `mpd` file now handled gracefully (`_vfs.Exists` guard +
  `FileMode.Open`/`FileAccess.Read`; skip with named warning, no empty-file side effect).
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
                               C1 (AFCP Layer 1) ✅ → [remote mount + Sync + Read SHIPPED]
                                                                     ↓
                               C7a (remote writes) ✅ → [Write/MkDir/Remove SHIPPED]
                                                                     ↓
                               C7b (Layer 2 subscribe-push) ✅ → [transparent remote Subscribe SHIPPED]
                                                                     ↓
                                                          C7c/C2 (MKCall proxy)
                                                                     ↓
                               C7d (typed errors) / C7e (streaming) / C7f (reconnect) — on demand
                                                                     ↓
                               C3 (capability — gates production writes) / C4 (config) / C5 (dynamic invocation)
                                                                     ↓
                               D1 (pull) / D2 (pool) — on demand
```
