# AFCP — HCore Data-Plane Protocol & Kernel Bridge

> **Status:** Layer 1 (mount + Sync + Read) **implemented & verified**, plus remote
> VFS writes (Write/MkDir/Remove, §C7a) **and Layer 2 (subscribe-push over the wire,
> §C7b) implemented & verified**. **Layer 3 (MKCall proxy, §C7c) implemented &
> verified** — `GetModuleInterface<T>(remotePath)` returns a marshalling proxy.
> **Related:** [DATA_PLANE_DESIGN.md](../data-plane/DATA_PLANE_DESIGN.md) Part IX (the 9P-style
> mount model), [TODO.md](../TODO.md) §C1, §C7a, §C7b.

---

## Overview

AFCP (the **A**... **F**... **C**... **P**... protocol — name inherited from V2)
is the wire protocol that lets two HCore instances expose and consume each other's
`/proc` data-plane facets over TCP. It is the remote layer of the data plane
described in [DATA_PLANE_DESIGN.md](../data-plane/DATA_PLANE_DESIGN.md) Part IX: remoteness is
**a path prefix**, never a first-class kernel identity (9P/Plan-9 style, not V2's
`Guid`-keyed peer registry).

Instance B serves its `/proc` tree; instance A mounts it at `/other`; a caller on
A addresses B's lidar scan as `/other/proc/lidar/scan_data` — ordinary `ls`/`cat`
through the VFS, no new API.

It is **three layers**, each with its own transport (do NOT route the 1 kHz stream
through file-read semantics):

| Layer | What | Status |
|---|---|---|
| **1 — Mount/snapshot** | `ls`, `cat`, snapshot `ReadData`, and now `mkdir`/`write`/`rm` over the wire | ✅ done (reads + writes) |
| **2 — Subscribe-push** | live `Subscribe` event stream, transparent through `Data.Subscribe<T>` | ✅ done |
| **3 — MKCall proxy** | remote method calls (`GetModuleInterface<T>(remotePath)` → proxy) | ✅ done |

---

## The standalone `AFCP/` library

AFCP is a **standalone classlib** (`AFCP/AFCP.csproj`, net10.0, no HCore deps,
`AllowUnsafeBlocks`). It is intentionally decoupled from HCore so it can be reused
outside the kernel. It is layered five-deep:

```
Transport → Framing → Multiplex → Serializer → Protocol
```

### Layer 0 — Transport (`AFCP/Transport/`)
`IConnection` — a bidirectional byte stream. `TcpConnection` wraps a `Socket`.
`TcpTransportListener`/`TcpTransportClient` produce/connect `IConnection`s.
`TcpConnection`'s constructor sets `TcpClient.NoDelay = true` — every
`AfcpKernelService`/`AfcpClient`/`AfcpServer` connection goes through this one
constructor, so it's the single point covering both the outgoing-connect and
accept paths. Without it, `FramedTransport.WriteMessage`'s two separate writes
(length prefix, then payload) each sit behind Nagle's algorithm waiting on the
peer's delayed ACK — ~40ms per frame, compounding into 100-250ms per shell
command on loopback (fixed; see TODO.md §C7a).

### Layer 1 — Framing (`AFCP/Streams/`)
`IFramedTransport` — message-oriented over a byte stream.
- `FramedTransport` — length-prefix framing (`[uint32 length][payload]`).
- `ChecksumFramedTransport` — additive decorator: appends a checksum byte, verifies
  on read. Composable transform slot.

`AfcpStack.Build(conn)` wires `IConnection → FramedTransport → ChecksumFramedTransport`,
shared by client and server so both build an identical stack.

### Layer 2 — Multiplex (`AFCP/Multiplex/`)
`MultiplexedConnection` — a persistent connection with dedicated reader/writer
threads. Each request gets a `RequestId`; the caller awaits a
`TaskCompletionSource` keyed by that id. Server-push uses `FrameKind.Notify`
frames (no response). Wire format:

```
[uint32 RequestId][byte Kind][uint16 MessageType][payload]
```

`FrameKind` = Request / Response / Notify. Outbox is a `BlockingCollection`;
`OnRequest`/`OnNotify` callbacks dispatch inbound frames.

### Layer 3 — Serializer (`AFCP/Serializer/` + root namespace)
A port of V2's **hand-written, reflection-free IL-emit** serializer (not
MemoryPack/JSON — an explicit choice). `Serializer` caches a per-type
`FastMethod` (a compiled transformer) built by `DynamicMethod` IL emit. Supports:
unmanaged primitives, `string`, `Guid`, classes (field-based, recursive), arrays,
`List<T>`, `Dictionary<TKey,TValue>`, nullable *reference* types, and
inheritance/polymorphism.

> **Gotcha:** `Nullable<T>` *value* types are NOT supported (the
> `NullableSerializer` uses `Ldnull`+`Ceq`, reference-type-only). Protocol
> messages work around this with `long X` + `bool HasX` flag pairs (e.g.
> `InterFrameDelta`/`HasInterFrameDelta`).

> **Gotcha:** types live in namespace `AFCP` (root), not `AFCP.Serializer`, because
> the class `Serializer` collided with the namespace `AFCP.Serializer` (CS0118).
> Matches V2's top-level placement.

> **Gotcha:** the `List<T>`/`Dictionary<,>` emitters reach into private fields
> (`_items`/`_size`, `_entries`/`_count`) via reflection — fragile across .NET
> versions; throws a clear error if the fields are absent. Verified on net10.0.

### Layer 4 — Protocol (`AFCP/Protocol/` + root)
Path-addressed message types, each a plain serializable class:

| Verb | Request → Response | Notes |
|---|---|---|
| **Connect** | `ConnectRequest` → `ConnectResponse` | Handshake; `ProtocolVersion.Current = 1` |
| **Sync** | `SyncRequest{Path}` → `SyncResponse{Entries[]}` | List one directory's entries (`DirEntry{Name, IsDirectory}`) |
| **Read** | `ReadRequest{Path}` → `ReadResponse{Data, Exists}` | Fetch any file's raw bytes (live on every call) |
| **Write** | `WriteRequest{Path, Data, Overwrite}` → `WriteResponse{Success, Error}` | Create/overwrite a file, whole-file (no chunking — §C7e) |
| **MkDir** | `MkDirRequest{Path}` → `MkDirResponse{Success, Error}` | Create a directory (and missing parents) |
| **Remove** | `RemoveRequest{Path}` → `RemoveResponse{Success, Error}` | Delete a single file or empty directory (no recursion) |
| **Subscribe** | `SubscribeRequest` → `SubscribeResponse` | Push; server opens a live `DataHost` subscription and streams `Event` frames (Layer 2) |
| **Unsubscribe** | `UnsubscribeRequest` → empty | Cancel a subscription |
| **Call** | `CallRequest{InstancePath, MethodName, ParamTypeNames[], object[] Args}` → `CallResponse{Success, Error, object? ReturnValue}` | Invoke a method on a remote instance (Layer 3 — MKCall). `object[]`/`object?` ride the polymorphic `DerivedSerializer` path |
| **Event** / **ProducerGone** / **SubscriptionError** | Notify-only | Push frames for active subscriptions |

`AfcpServer` accepts connections, runs a `PeerSession` per peer, dispatches
requests to an `IAfcpProvider` (the host's backing implementation).
`AfcpClient` connects, runs the handshake, and exposes `SyncAsync`/`ReadAsync`/
`WriteAsync`/`MkDirAsync`/`RemoveAsync`/`SubscribeAsync`/`CallAsync`. Subscription
pushes dispatch to `IAfcpSubscription` handles; `CallAsync` is used by the
mount-side `RemoteModuleProxy<T>`.

`IAfcpProvider` is the contract a host implements to back a server:
`Connect`/`Sync`/`Read`/`Write`/`MkDir`/`Remove`/`Subscribe`/`Unsubscribe`/`Call`.
For subscriptions the provider receives an `IAfcpSubscriptionSink` to push
`EventNotify` frames back; for `Call` it resolves the instance + method itself.

There is no dedicated `Move` verb: a move/rename over a remote mount is served
by the mount-side `HCore.Main.Vfs.FileSystem.Move`, which already decomposes
every move into `Write`+`MkDir`+`Remove`+`Read` against the generic VFS
primitives (same-mount only — a pre-existing constraint, unrelated to AFCP).
That composition costs one round-trip per file instead of a single
server-side rename, accepted for this trusted-LAN first cut.

> **Gotcha (fixed):** the serializer's unmanaged-array fast path (used for
> `byte[]`) used to throw `IndexOutOfRangeException` on a zero-length array —
> `Ldelema` unconditionally took the address of element 0 even when the array
> was empty. Surfaced by `WriteRequest.Data` being empty when creating an
> empty file (e.g. `touch` over a mount); fixed in
> `AFCP/Serializer/Serializers/ArraySerializer.cs` by skipping the body write
> when the array length is 0.

> **Gotcha (fixed):** the string deserializer pinned the body array with
> `Ldelema` on element 0 for the `string(char*, 0, len)` constructor, which
> throws `IndexOutOfRangeException` for an empty string (`""`). Never surfaced
> before because existing messages used `null` (handled by the null-wrapper),
> never `""`; `CallResponse.Error` defaults to `""`. Fixed in
> `AFCP/Serializer/Serializers/StringSerializer.cs` by short-circuiting to
> `string.Empty` when the body length is 0.

---

## The kernel-space bridge (HCore.Main)

The bridge wires AFCP to the kernel's `DataHost` + `ModuleHost` + `FileSystem`.
It is **kernel-space** for now (HCore.Main references AFCP directly) — a shortcut
to get Layer 1 testable fast. The architecturally-clean target is an
`HCore.Packages.Nexus` package, which requires moving `IVirtualFileSystem` to
`HCore.Modules.Base` and adding a proc-view/mount contract surface (see
"Migration path" below).

### `IAfcpKernel` (HCore.Modules.Base)
The contract the shell uses to drive the bridge — string-in/string-out, so the
shell package needs no AFCP reference (same pattern as `IServiceManager`):

```csharp
public interface IAfcpKernel : IModule
{
    string Serve(int port);
    string StopServe();
    string Mount(string host, int port, string mountPoint);
    string Unmount(string mountPoint);
    string Status();
    string SelfTest();
}
```

The shell reaches it via `Host.GetModuleInterface<IAfcpKernel>("@afcp")`.

### Kernel-service registry (`ModuleHost`)
`@afcp` is **not** a module instance in `/proc` — it is a kernel-space singleton.
`ModuleHost` gained a small named-service registry:

- `RegisterKernelService(string name, object service)` — name must be `@`-prefixed.
- `GetModuleInterface<T>` checks `@`-prefixed names against this registry *before*
  the `/proc` instance table.

This is how a non-module kernel service is reachable through the same
`GetModuleInterface<T>` syscall the shell already uses, without polluting `/proc`.

### `AfcpKernelService` (HCore.Main/Internal)
Implements `IAfcpKernel`. Holds:
- **Serve side:** an `AfcpServer` backed by a `VfsAfcpProvider` (a generic proxy
  over the kernel `FileSystem`).
- **Mount side:** a dictionary of `RemoteFileSystem` mounts, installed into the
  kernel `FileSystem` via `Mount`/`Unmount`.

### `VfsAfcpProvider` (HCore.Main/Internal)
Implements `IAfcpProvider` as a **generic VFS proxy** over the kernel `FileSystem`:
- **Sync** — lists any directory via `FileSystem.ListDirectory(path)`, which folds
  in child mount names (so `/` shows `proc/`, `dev/`, `etc/`, `packs/`, ...). The
  trailing-slash convention distinguishes directories.
- **Read** — returns any file's raw bytes via `FileSystem.GetFile(path).ReadAllBytes()`.
  Because `/proc` is a live `ProcFileSystem` mount inside the kernel VFS, a facet
  file (`/proc/lidar/scan_data`) is rebuilt fresh on every read — the liveness is
  handled server-side for free, with **no facet-specific protocol**.
- **Write** / **MkDir** / **Remove** — delegate straight to `FileSystem.CreateFile`/
  `FileSystem.MkDir`/`FileSystem.DeleteFile` (the same kernel API a local `mv`/
  `mkdir`/`rm` uses), wrapped in a try/catch that reports failure as
  `Success = false, Error = ex.Message` rather than throwing over the wire — no
  typed error taxonomy yet (§C7d). `DeleteFile` deletes whatever node
  `TryDelete` finds, file or directory, despite the name.
- **Subscribe** / **Unsubscribe** — the one non-VFS verb for streams (facets aren't
  files): resolves the facet via `DataHost.FindFacet(path)`, opens a live subscription via
  the non-generic `IFacet.SubscribeRaw`, and streams each frame back as an
  `EventNotify` (value serialized by runtime type via `Serializer.Serialize(stream,
  value, facet.ValueType)`) through the peer's `IAfcpSubscriptionSink`.
  `ProducerKilled` → `sink.ProducerGone`; a breaker trip → `sink.Error`. Active
  subscriptions are tracked per provider (`id → ISubscription`) and disposed on
  `Unsubscribe` or when the connection closes (`PeerSession.DisposeAllSubscriptions`,
  so a dropped link can't leak a server-side subscription).
- **Call** — the non-VFS verb for RPC (Layer 3 — MKCall; a method call isn't a file):
  resolves the instance by `CallRequest.InstancePath` via the non-generic
  `ModuleHost.TryResolveInstance`, reflects the method by `MethodName` +
  `ParamTypeNames` (cached per `(declaring type, name, param signature)` in a
  `ConcurrentDictionary<CallKey, FastMethodInfo>` so resolution + IL compilation
  happen once per method, not per call), and invokes via the compiled `FastMethodInfo`
  delegate (ported from V2's `Kernel.AFCP.FastMethod.cs`). The boxed return becomes
  `CallResponse.ReturnValue` (null for void). Any failure (no such instance / method,
  or a thrown exception) is caught and returned as `Success=false, Error="Type.FullName:
  Message"` rather than thrown over the wire — same minimalism as `Write`/`MkDir`/
  `Remove`, no typed error taxonomy yet (§C7d). `@`-prefixed kernel services are
  deliberately NOT reachable here (they are local singletons, not remotely-callable
  instances).

This is the key simplification over the earlier facet-metadata design: since the
kernel `FileSystem` already mounts the live `/proc` alongside everything else,
serving `/` over AFCP is just proxying VFS ops. No `DataHost`, no facet metadata,
no `InstanceInfo`/`FacetInfo` on the wire.

There is no capability model (§C3) — any peer that mounts here can write
anywhere under the served root, not just `/proc`: `/etc`, `/packs/*/mpd`,
arbitrary host paths. Same documented trusted-LAN gap as `Kill`, but writes
raise the stakes materially over read-only Layer 1.

### `RemoteFileSystem` (HCore.Main/Vfs)
A read-write `IVirtualFileSystem` backed by an `AfcpClient`. It is **lazy**
(9P-style): each `RemoteDirectory` fetches its entries via a fresh `SyncAsync` on
access, and each `RemoteFile` fetches its bytes via `ReadAsync` on read. Nothing
is cached — `ls` walks into one directory per round-trip, and `cat` on a live
`/proc` facet always sees the latest frame. This mirrors `ProcFileSystem`'s
live-view model, but per-directory instead of rebuilding the whole tree at once.

Writes are whole-file, single round-trip operations (no chunking — §C7e):
`RemoteDirectory.CreateDirectory`/`TryDelete`/`CreateFile` call `MkDirAsync`/
`RemoveAsync`/`WriteAsync` directly; `RemoteFile.Write` does the same. A
write-access `RemoteFile.GetStream` returns a `RemoteWriteStream` (an internal
`MemoryStream` subclass) that buffers in memory and fires one `WriteAsync` on
`Dispose()` — `FileMode.Append`/`Open`/`OpenOrCreate` seed the buffer with the
existing remote content first (so an append or a partial rewrite doesn't
clobber the rest of the file); `Create`/`CreateNew`/`Truncate` start empty.

**Subscribe (Layer 2).** `RemoteFileSystem` also implements the internal
`IRemoteDataSource.SubscribeData<T>` — the hook the kernel `DataHost` calls when
`Data.Subscribe<T>` targets a path under this mount. It opens an `AfcpClient`
subscription and wraps it in a `RemoteSubscription<T>` adapter that presents the
kernel's `ISubscription` contract. Because AFCP notify frames dispatch on
thread-pool threads, the adapter funnels them through a bounded (drop-oldest)
`Channel<DataEvent<T>>` + a single consumer loop, so the caller's handler is never
invoked concurrently — matching the local single-consumer guarantee. Each
`EventNotify` is deserialized with the typed `Serializer.Deserialize<T>` (`T` is
known at the `Subscribe<T>` call site); `ProducerGone`/`SubscriptionError` map to
`DisconnectReason.ProducerKilled`/`Overload`/`HandlerException` and trip the handle.

**Call (Layer 3).** `RemoteFileSystem` exposes its `AfcpClient` via an internal
`Client` accessor so the MKCall proxy can round-trip `Call` frames through the same
connection that already serves this mount's VFS + data traffic — one peer, one
connection, three layers. The proxy itself (`RemoteModuleProxy<T> : DispatchProxy`,
`HCore.Main/Vfs/RemoteModuleProxy.cs`) is built by `ModuleHost.GetModuleInterface<T>`
when `TryResolveMount` resolves the path to a `RemoteFileSystem` (mirroring the
Layer 2 redirect in `DataHost.Subscribe<T>`). Each method invocation marshals a
`CallRequest{ InstancePath, MethodName, ParamTypeNames[], object[] Args }` via
`AfcpClient.CallAsync`; the `CallResponse.ReturnValue` is handed straight back to
the caller (the proxy already knows from `MethodInfo.ReturnType` whether to expect
a value, so no void flag is sent). Per-`MethodInfo` signatures are cached so
reflection happens once per method, not per call. Invocation is synchronous
(module interface methods are synchronous in V3); the proxy blocks on the response
with `CancellationToken.None` (no mux-level timeout — §C7f). Failure on the peer
surfaces as a `RemoteCallException` (in `HCore.Modules.Base`, catchable by user
space).

### DataHost accessors (used by Layer 2)
`DataHost.FindFacet(path)` returns the non-generic `IFacet` for a facet path; the
AFCP serve side (`VfsAfcpProvider.Subscribe`) uses it plus the new
`IFacet.SubscribeRaw` (a non-generic subscribe that hands the boxed value +
`Sequence` + `InterFrameDelta` to a callback, wrapping the typed
`Facet<T>.Subscribe` so all breaker/threading/`ProducerKilled` semantics are
intact). The mount side wires transparency through `FileSystem.TryResolveMount`
(returns the backing `IVirtualFileSystem` + the path as the peer sees it) — `DataHost`
holds a `FileSystem` reference and, in `Subscribe<T>`, redirects to a remote mount
before the local `/proc` parse. The older `GetFacetSnapshot()`/`ReadFormatted(path)`
accessors remain unused by the VFS proxy.

### `FileSystem.Unmount`
Added a `Unmount(mountPoint)` so the bridge can tear down a `RemoteFileSystem`.
Previously mounts were install-only.

---

## The `afcp` shell command

`HCore.Packages.HShell/Shell/Commands/AfcpCommand.cs` — registered as `afcp`:

```
afcp serve <port>              Expose local /proc over AFCP
afcp stop                      Stop serving
afcp mount <host> <port> <mp>  Mount a remote peer's tree at <mp>
afcp unmount <mp>              Tear down a mount
afcp status                    Show serving port + active mounts
afcp test                      Run the loopback self-test (see below)
```

Reaches the bridge purely through `IAfcpKernel`; no AFCP types leak into the
shell package.

---

## Self-test

`afcp test` runs `AfcpKernelService.SelfTest()` — a full loopback in one
instance (no second process needed):

1. Spawn + run the lidar demo (if not already running).
2. `Serve` on a loopback port (8765).
3. `Mount 127.0.0.1:8765` at `/selftest` (the instance connects to itself).
4. `ls /selftest` → the entire root (`proc/`, `etc/`, `dev/`, `packs/`, `tmp/` ...).
5. `ls /selftest/etc` → real host-FS directory (`services/`).
6. `ls /selftest/proc/lidar` → `info` + `scan_data` (live /proc view).
7. `cat /selftest/proc/lidar/scan_data` twice → fresh frames each time (proves the
   lazy `RemoteFile` does a live `ReadAsync` per access, no caching).
8. `mkdir /selftest/tmp/afcp_test`, write a scratch file, `cat` it back to confirm
   the round-trip, delete the file, confirm it's gone, delete the directory —
   exercises the §C7a write path (scoped to the in-memory `/tmp` mount so the
   self-test never touches the real host FS).
9. **Layer 2 (§C7b), raw client:** a bare `AfcpClient` subscribes to
   `/proc/lidar/scan_data` on the loopback server, collects pushed `EventNotify`
   frames for ~400ms, unsubscribes, and asserts ≥2 frames with strictly-increasing
   `Sequence` and non-empty `Data`.
10. **Layer 2, transparent typed path:** writes the subscribe target
    (`/selftest/proc/lidar/scan_data`) to `/tmp/remote_slam_target`, spawns the
    `HCore.Packages.Sensor.RemoteSlam` demo consumer (which subscribes via the
    ordinary `Data.Subscribe<ScanFrame>` — oblivious to the mount), waits, then reads
    the consumer's `recv_status` facet and asserts it received ≥2 typed frames.
11. **ProducerKilled over the wire:** kills the lidar and asserts the consumer's
    `recv_status` flips to `state=ProducerKilled` (server `sink.ProducerGone` →
    client `onProducerGone` → adapter trip).
12. Cleanup: kill the consumer, remove the target file, unmount, stop serve, kill the
    lidar (if SelfTest spawned it).

Verified passing: frame index advances between the two `cat` reads, confirming live
lazy reads over the whole-root VFS proxy; the write/read-back/delete round-trip on
step 8 matches byte-for-byte; the raw client receives ~4 pushed frames; the demo
consumer receives typed `ScanFrame`s over the mount and trips with `ProducerKilled`
on kill. (First serialization of a facet value surfaced that `ScanFrame` needed a
parameterless constructor — the `ClassSerializer` requires one; see limitations.)

---

## How to use it (two-instance manual test)

Launch a separate HCore instance in each of two terminals
(`dotnet run --project HCore.Main`); the commands below are typed at the HCore
shell prompt. Only instance B serves a port, so there's no port conflict on one
machine (both instances share the same host-FS root, which is fine).

On instance **B** (the producer + server):
```
service start sensor      # spawns + runs lidar (exposes /proc/lidar/scan_data)
afcp serve 8000
afcp status               # confirm: serving on port 8000
```

### Layer 1 — mount, read, write

On instance **A** (the consumer) — the whole root is visible, not just `/proc`:
```
afcp mount 127.0.0.1 8000 /other
ls /other                       # the entire root: proc/, etc/, dev/, packs/, tmp/ ...
ls /other/etc/services          # real host-FS files
ls /other/proc/lidar            # the live /proc facet view
cat /other/proc/lidar/scan_data # fresh on every read
mkdir /other/tmp/scratch        # remote write (§C7a)
write /other/tmp/scratch/f.txt hello
cat /other/tmp/scratch/f.txt    # "hello"
mv /other/tmp/scratch/f.txt /other/tmp/scratch/g.txt  # compose-only rename
rm /other/tmp/scratch/g.txt
rmdir /other/tmp/scratch
afcp status
```

### Layer 2 — live subscribe-push (§C7b)

`cat` only ever pulls a formatted **snapshot** — the shell can't deserialize a typed
value, so a live typed **push** is driven by a consumer *module*
(`HCore.Packages.Sensor.RemoteSlam`), which subscribes through the ordinary
`Data.Subscribe<ScanFrame>` and is oblivious to whether the facet is local or remote.
Still on instance **A**, with `/other` mounted:
```
write /tmp/remote_slam_target /other/proc/lidar/scan_data   # tell the consumer where to subscribe
spawn HCore.Packages.Sensor.RemoteSlam rslam
run rslam                        # from here it logs each pushed frame: "recv seq=.. frame=.."
cat /proc/rslam/recv_status      # received=N lastSeq=M state=Active
```
The consumer subscribes to `/other/proc/lidar/scan_data`; `DataHost` sees the
`/other` mount and transparently opens a remote subscription (no `afcp`-specific
call at the subscribe site). To watch the `ProducerKilled` signal cross the wire,
kill the producer on instance **B**:
```
kill lidar
```
Then on instance **A** the frame log stops and the subscription trips:
```
cat /proc/rslam/recv_status      # state=ProducerKilled
```
Cleanup — instance **A**: `kill rslam` then `afcp unmount /other`; instance **B**:
`afcp stop`.

> The `afcp test` command runs this entire Layer 1 + Layer 2 sequence as a loopback
> self-test inside a single instance (no second terminal needed), and now also
> exercises Layer 3 (MKCall) — see below.

### Layer 3 — remote method calls (§C7c)

A remote method call is driven by a *module*, not a shell command: the caller does
`Host.GetModuleInterface<T>("/other/proc/<instance>")` and the returned proxy
marshals each method invocation over AFCP. The canonical demo is
`HCore.Packages.TestDemo` Module1/Module2 — Module2's
`Host.GetModuleInterface<IModule1>(target).Func1()` works whether `target` is
`/proc/module1` (local, direct dispatch) or `/remote/proc/module1` (remote mount,
MKCall proxy). The target is read from `/tmp/module2_target` (defaulting to
`/proc/module1`), mirroring `RemoteSlam`'s subscribe-target hook.

On instance **B** (owns Module1, serves):
```
spawn HCore.Modules.TestDemo.Module1 module1      # Module1 isn't IRunnable — just spawn it
afcp serve 8000
```

On instance **A** (mounts B, runs Module2):
```
afcp mount 127.0.0.1 8000 /remote
write /tmp/module2_target /remote/proc/module1
spawn HCore.Modules.TestDemo.Module2 module2
run module2                                          # A: "Ran Module 2! (calling /remote/proc/module1)"
                                                     # B: "Func1 was called!"  (the marshalled call)
```

Drop `/tmp/module2_target` (or point it at `/proc/module1`) and the very same
Module2 code calls a *local* Module1 — `GetModuleInterface<T>` returns the actual
object, no proxy, zero overhead. That's the location-transparency hook: remoteness
is a path prefix, never a code change.

> The `afcp test` self-test exercises Layer 3 reflectively in one loopback instance
> (`ModuleHost.GetRemoteModuleInterface(Type, path)` + `MethodInfo.Invoke` on the
> proxy), because `HCore.Main` cannot reference the `Sensor`/`TestDemo` packages.
> The wire path is identical to a compile-time-typed call.

---

## Layer 3 (MKCall) limitations

- **AFCP-serializable args/returns.** Every argument and return type must be
  AFCP-serializable (unmanaged struct / string / Guid / array / `List<T>` /
  `Dictionary<,>` / a class with a parameterless ctor + public get/set props).
  `Nullable<T>` value types are NOT supported (use the `long`+`bool HasX` flag-pair
  idiom if a nullable value must cross the wire).
- **Both peers must have the type loaded.** `DerivedSerializer` writes the
  assembly-qualified name; the server resolves arg/param/return types via
  `Type.GetType`. Contract types in `HCore.Modules.Base` resolve (shared identity
  across ALCs); custom arg/return types in a package assembly must be loaded on both
  peers — same constraint Layer 2 imposes on facet value types.
- **No out/ref parameters.** The proxy does not marshal them.
- **No exception-type reconstruction.** Server-side exceptions surface as
  `CallResponse{Success=false, Error="Type.FullName: Message"}`; the proxy throws
  `RemoteCallException` carrying that string. Original exception type is lost
  (§C7d territory).
- **No capability check (§C3).** A mounting peer can call any public method on any
  served instance — same trusted-LAN gap as remote writes and `Kill`.
- **No call timeout / reconnect (§C7f).** The proxy uses `CancellationToken.None`
  and blocks synchronously; a silently-dead peer hangs a caller until TCP gives up.
- **Implicit interface implementation only.** `GetMethod` resolves public instance
  methods on `instance.GetType()` by name + param types; explicit interface
  implementations (`void IFoo.Bar()`) are not found. Module interface methods are
  conventionally implicit.
- **The shell can't drive arbitrary calls.** `afcp` is string-based and the shell
  can't bind method arguments by type, so a typed MKCall is exercised by a *module*
  (Module1/Module2), not a shell command. The one shell path that crosses Layer 3 is
  `run /remote/proc/<instance>` (it calls `GetModuleInterface<IRunnable>(path).Run()`).

---

## Layer 2 (subscribe-push) limitations

- **Facet value types must be AFCP-serializable.** Layer 2 is the first path that
  serializes a facet's value (the local plane passes frames by reference; `cat` uses
  the text formatter). The `ClassSerializer` requires a **parameterless constructor**
  and serializable members — a positional `record` needs an explicit `public T() : this(…)`.
- **No remote backpressure.** `sink.Push` enqueues onto the multiplex outbox; a stalled
  TCP link grows the outbox unbounded. The local breaker protects the facet's consumer
  loop, not the network. Bounding the outbox / a remote overload signal is future work.
- **Wire-order vs enqueue-order.** The client adapter guarantees single-consumer,
  non-concurrent handler invocation, but because notify frames dispatch on thread-pool
  threads, enqueue order under concurrent dispatch can differ from wire order — observable
  through `Sequence` gaps, exactly as documented for local overflow drops. Ordered
  reader-thread notify dispatch is a possible hardening.
- **No capability model (§C3).** Any mounting peer can subscribe to any facet — same
  trusted-LAN gap as `Kill` and remote writes.
- **Type identity.** Transparent typed subscribe requires the value type loaded on both
  peers; cross-machine needs it in a shared assembly (the loopback self-test is one
  process/ALC, so identity is trivially satisfied).
- **The shell can't drive it.** `afcp` is string-based and the shell can't deserialize a
  typed value, so a live subscribe is exercised by a consumer *module*
  (`HCore.Packages.Sensor.RemoteSlam`), not a shell command. `cat` remains the shell's
  snapshot view.

## Migration path: kernel-space → package

The bridge is kernel-space now; the clean target is an `HCore.Packages.Nexus`
package. That move requires three contract extensions to `HCore.Modules.Base`
(the module syscall surface) that don't exist yet:

1. **Proc-view injection** — enumerate running instances + facets with metadata
   (currently `internal` on `ModuleHost`/`DataHost`). A package can't list
   `/proc` or discover facets by path.
2. **Untyped/formatted facet read by path** — currently `internal
   DataHost.ReadFormatted`. The public `IDataHost.ReadData<T>` is typed.
3. **Custom VFS mount from a package** — `FileSystem.Mount` is kernel-only, and
   `IVirtualFileSystem`/`IVirtualDirectory`/`IVirtualFile` live in `HCore.Main.Vfs`,
   so a package **cannot implement** a `RemoteFileSystem` today. Those interfaces
   must move to `HCore.Modules.Base`.

Once those three are in place, `AfcpKernelService` + `DataHostAfcpProvider` +
`RemoteFileSystem` move verbatim into a package that references only
`HCore.Modules.Base` + the standalone `AFCP` library, and HCore.Main drops its
AFCP reference. The `IAfcpKernel` contract already lives in Base, so the shell
command needs no changes.

---

## File map

| File | Role |
|---|---|
| `AFCP/` | Standalone protocol library (Transport/Streams/Multiplex/Serializer/Protocol) |
| `AFCP/AfcpServer.cs` / `AfcpClient.cs` | Server + client |
| `AFCP/IAfcpProvider.cs` | Host backing contract + subscription sink |
| `HCore.Modules.Base/IAfcpKernel.cs` | Shell-facing bridge contract (string-based) |
| `HCore.Main/Internal/AfcpKernelService.cs` | `IAfcpKernel` impl + `VfsAfcpProvider` (generic VFS proxy + Layer 2 `Subscribe` + Layer 3 `Call`) |
| `HCore.Main/Vfs/RemoteFileSystem.cs` | Mount-side lazy read-write VFS + `IRemoteDataSource.SubscribeData<T>` + `RemoteSubscription<T>` + `Client` accessor (Layer 3) |
| `HCore.Main/Vfs/RemoteModuleProxy.cs` | **Layer 3** — `RemoteModuleProxy<T> : DispatchProxy` (marshals method calls over `Call`) |
| `HCore.Main/Internal/FastMethodInfo.cs` | Compiled `Expression` delegate per `MethodInfo` (ported from V2; server-side MKCall fast path) |
| `HCore.Main/Internal/ModuleHost.cs` | Kernel-service registry (`@`-prefixed names); `TryResolveMount` branch in `GetModuleInterface<T>` (Layer 3); `TryResolveInstance` (non-generic); `GetRemoteModuleInterface(Type, path)` (self-test) |
| `HCore.Modules.Base/RemoteCallException.cs` | Thrown by the MKCall proxy on a remote failure (catchable by user space) |
| `HCore.Main/Internal/DataHost.cs` | `FindFacet`; `IFacet.SubscribeRaw`; `FileSystem` ref + remote branch in `Subscribe<T>` |
| `HCore.Main/Internal/DataFacet.cs` | `Facet<T>.SubscribeRaw` (non-generic subscribe wrapping the typed path) |
| `HCore.Main/Vfs/FileSystem.cs` | Added `Unmount`; `TryResolveMount` (mount + peer-relative path) |
| `AFCP/AfcpServer.cs` | `PeerSession.DisposeAllSubscriptions` on connection close (leak fix); `Call` dispatch case |
| `AFCP/AfcpClient.cs` | `CallAsync(CallRequest, ct)` round-trip (Layer 3) |
| `HCore.Packages.TestDemo/Module2/Module2Implement.cs` | Reads MKCall target from `/tmp/module2_target` (local-vs-remote demo) |
| `HCore.Packages.Sensor/RemoteSlam/` | Demo consumer for the transparent remote-subscribe path |
| `HCore.Packages.Sensor/ScanFrame.cs` | Added parameterless ctor (required to AFCP-serialize the value) |
| `AFCP/Serializer/Serializers/ArraySerializer.cs` | Fixed zero-length unmanaged-array serialize (§C7a fallout) |
| `AFCP/Serializer/Serializers/StringSerializer.cs` | Fixed zero-length (empty-string) deserialize — `Ldelema` on element 0 of an empty body array (§C7c fallout) |
| `AFCP/Transport/TcpConnection.cs` | Sets `TcpClient.NoDelay = true` (fixed Nagle/delayed-ACK latency) |
| `HCore.Packages.HShell/Shell/Commands/AfcpCommand.cs` | `afcp` shell command |
| `FS/etc/services/sensor.svc` | Spawns + runs the lidar demo |
