# AFCP — HCore Data-Plane Protocol & Kernel Bridge

> **Status:** Layer 1 (mount + Sync + Read) **implemented & verified**. Layer 2
> (subscribe-push over the wire) and Layer 3 (MKCall proxy) deferred.
> **Related:** [DATA_PLANE_DESIGN.md](DATA_PLANE_DESIGN.md) Part IX (the 9P-style
> mount model), [TODO.md](TODO.md) §C1.

---

## Overview

AFCP (the **A**... **F**... **C**... **P**... protocol — name inherited from V2)
is the wire protocol that lets two HCore instances expose and consume each other's
`/proc` data-plane facets over TCP. It is the remote layer of the data plane
described in [DATA_PLANE_DESIGN.md](DATA_PLANE_DESIGN.md) Part IX: remoteness is
**a path prefix**, never a first-class kernel identity (9P/Plan-9 style, not V2's
`Guid`-keyed peer registry).

Instance B serves its `/proc` tree; instance A mounts it at `/other`; a caller on
A addresses B's lidar scan as `/other/proc/lidar/scan_data` — ordinary `ls`/`cat`
through the VFS, no new API.

It is **three layers**, each with its own transport (do NOT route the 1 kHz stream
through file-read semantics):

| Layer | What | Status |
|---|---|---|
| **1 — Mount/snapshot** | `ls`, `cat`, snapshot `ReadData` over the wire | ✅ done |
| **2 — Subscribe-push** | live `Subscribe` event stream | ☐ deferred (provider rejects for now) |
| **3 — MKCall proxy** | remote method calls (`GetModuleInterface<T>(remotePath)` → proxy) | ☐ deferred |

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
| **Subscribe** | `SubscribeRequest` → `SubscribeResponse` | Push; **rejected** in this build (Layer 2 deferred) |
| **Unsubscribe** | `UnsubscribeRequest` → empty | Cancel a subscription |
| **Event** / **ProducerGone** / **SubscriptionError** | Notify-only | Push frames for active subscriptions |

`AfcpServer` accepts connections, runs a `PeerSession` per peer, dispatches
requests to an `IAfcpProvider` (the host's backing implementation).
`AfcpClient` connects, runs the handshake, and exposes `SyncAsync`/`ReadAsync`/
`SubscribeAsync`. Subscription pushes dispatch to `IAfcpSubscription` handles.

`IAfcpProvider` is the contract a host implements to back a server:
`Connect`/`Sync`/`Read`/`Subscribe`/`Unsubscribe`. For subscriptions the provider
receives an `IAfcpSubscriptionSink` to push `EventNotify` frames back.

---

## The kernel-space bridge (HCore.Main)

The bridge wires AFCP to the kernel's `DataHost` + `ModuleHost` + `FileSystem`.
It is **kernel-space** for now (HCore.Main references AFCP directly) — a shortcut
to get Layer 1 testable fast. The architecturally-clean target is an
`HCore.Packages.Afcp` package, which requires moving `IVirtualFileSystem` to
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
- **Subscribe** — rejected (`Accepted = false`); Layer 2 deferred.

This is the key simplification over the earlier facet-metadata design: since the
kernel `FileSystem` already mounts the live `/proc` alongside everything else,
serving `/` over AFCP is just proxying VFS ops. No `DataHost`, no facet metadata,
no `InstanceInfo`/`FacetInfo` on the wire.

### `RemoteFileSystem` (HCore.Main/Vfs)
A read-only `IVirtualFileSystem` backed by an `AfcpClient`. It is **lazy**
(9P-style): each `RemoteDirectory` fetches its entries via a fresh `SyncAsync` on
access, and each `RemoteFile` fetches its bytes via `ReadAsync` on read. Nothing
is cached — `ls` walks into one directory per round-trip, and `cat` on a live
`/proc` facet always sees the latest frame. This mirrors `ProcFileSystem`'s
live-view model, but per-directory instead of rebuilding the whole tree at once.

### DataHost accessors (retained for Layer 2)
The earlier facet-metadata accessors on `DataHost` (`GetFacetSnapshot()`,
`ReadFormatted(path)`, `FindFacet(path)`, and the `ValueType`+`Kind` fields on the
`FacetInfo` record) are **retained** but currently unused — the generic VFS proxy
doesn't need them. They'll be needed when Layer 2 (subscribe-push) lands, since
the provider will have to enumerate facets and subscribe to them directly through
`DataHost` rather than through the VFS.

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
8. Cleanup: unmount, stop serve, kill the lidar (if SelfTest spawned it).

Verified passing: frame index advances between the two reads (9 → 15), confirming
live lazy reads over the whole-root VFS proxy.

---

## How to use it (two-instance manual test)

On instance **B** (the producer):
```
service start sensor      # spawns + runs lidar (exposes /proc/lidar/scan_data)
afcp serve 8000
```

On instance **A** (the consumer) — the whole root is visible, not just `/proc`:
```
afcp mount 127.0.0.1 8000 /other
ls /other                       # the entire root: proc/, etc/, dev/, packs/, tmp/ ...
ls /other/etc/services          # real host-FS files
ls /other/proc/lidar            # the live /proc facet view
cat /other/proc/lidar/scan_data # fresh on every read
afcp status
afcp unmount /other
```

---

## Migration path: kernel-space → package

The bridge is kernel-space now; the clean target is an `HCore.Packages.Afcp`
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
| `HCore.Main/Internal/AfcpKernelService.cs` | `IAfcpKernel` impl + `VfsAfcpProvider` (generic VFS proxy) |
| `HCore.Main/Vfs/RemoteFileSystem.cs` | Mount-side lazy read-only VFS (`RemoteDirectory` + `RemoteFile`) |
| `HCore.Main/Internal/ModuleHost.cs` | Kernel-service registry (`@`-prefixed names) |
| `HCore.Main/Internal/DataHost.cs` | Added `GetFacetSnapshot`/`ReadFormatted`/`FindFacet` |
| `HCore.Main/Vfs/FileSystem.cs` | Added `Unmount` |
| `HCore.Packages.HShell/Shell/Commands/AfcpCommand.cs` | `afcp` shell command |
| `FS/etc/services/sensor.svc` | Spawns + runs the lidar demo |
