# AFCP Organization — Protocol vs. HCore Connector (Nexus)

> **Status:** PLAN — not yet implemented. This doc fixes the target architecture
> and the phased extraction. Authored in response to the upstream
> `github.com/Ardumine/afcp` being recognized as the *protocol* and the in-tree
> `AFCP/` + kernel bridge being the *HCore connector* that should become a module.
>
> **Related:** [AFCP.md](AFCP.md) (current kernel-space bridge, Layer 1/2/3),
> [TODO.md](../TODO.md) §C (the AFCP follow-ups), `AGENTS.md` "AFCP remote data plane".

---

## 1. Repo structure

Each `HCore.Packages.*` module (including Nexus) will live in its **own GitHub repo**
under `github.com/Ardumine/`. This monorepo (`hcore/`) will eventually hold only
`HCore.Main` (the kernel) + `HCore.Modules.Base` (the shared contract). Packages clone
alongside the kernel repo and mount into the workspace as peers — same pattern as the
upstream `ardumine/afcp` and `ardumine/kaserializer` repos (Phase 0).

```
ardumine/
  hcore/            ← kernel + Base (this repo)
  nexus/            ← HCore.Packages.Nexus (AFCP↔HCore connector)
  hsensors/         ← HCore.Packages.Sensor
  hell/             ← HCore.Packages.HShell
  hinit/            ← HCore.Packages.HInit
  testdemo/         ← HCore.Packages.TestDemo
  ...
```

Each module repo references only `HCore.Modules.Base` (via a NuGet package or a local
`<ProjectReference>` to a cloned `hcore/` peer). The kernel repo references **none**
of them — it loads them at runtime from `FS/packs/`.

## 2. The problem: three concerns, two wrong-shaped things

| Concern | What it is | Today |
|---|---|---|
| **Protocol** | A composable, HCore-free **byte-stream transform stack**: transport → framing → checksum → crypto → camouflage. Stackable layers; rides any duplex byte stream (TCP, serial, in-memory). | The upstream `Ardumine/afcp` repo — two parallel experiments (`testApp`/`Streamy`, `testeMulti`/`IConnection`+`ICountableStream`), very simple, no hardening. **Not** in the workspace. |
| **Connector** | The HCore-shaped verbs (`Sync`/`Read`/`Write`/`MkDir`/`Remove`/`Subscribe`/`Call`) + a serializer + a multiplex, riding the protocol stack. Binds AFCP to HCore's `/proc`+data-plane+module-call semantics. | The in-tree `hcore/AFCP/` lib (V2-port: own transport/framing/checksum **duplicated**, plus multiplex+serializer+path-messages). HCore-free, but mis-labeled "AFCP". |
| **Integration** | Serve `/` over AFCP; mount a remote tree; transparent `Subscribe` and `GetModuleInterface<T>` redirect on remote paths. | Kernel-space in `HCore.Main` (`AfcpKernelService` + `Vfs/RemoteFileSystem` + `Vfs/RemoteModuleProxy` + `FastMethodInfo`). `HCore.Main` references `AFCP` directly — a shortcut. |

The upstream protocol and the in-tree connector are **both called "AFCP"** but share no
code. The connector duplicates the protocol's transport/framing/checksum layers with its
own. And the integration is baked into the kernel instead of being a loadable module.

> **User's framing:** "AFCP itself is a protocol, where you can stack layers… there
> should be a module that integrates AFCP + HCore. AFCP→HCore is not part of HCore,
> just like VFS in Linux." The kernel owns the VFS *abstraction*; filesystem *drivers*
> are modules. AFCP↔HCore is a driver.

---

## 3. Target architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│  HCore.Main (kernel)                                                │
│    FileSystem (VFS mount table)  ModuleHost (proc table)            │
│    DataHost (facet broker)                                          │
│    ── exposes contract surfaces in HCore.Modules.Base ────────────  │
│       • IVirtualFileSystem / IVirtualDirectory / IVirtualFile       │
│       • IKernelVfs  (Mount/Unmount/TryResolveMount/List/Get/...)    │
│       • IProcView  (enumerate instances + facets by path)           │
│       • IFacetView (non-generic SubscribeRaw + FindFacet by path)   │
│       • IModuleResolver (non-generic TryResolveInstance)            │
│       • IRemoteMountHook (subscribe/call redirect extension point)  │
└──────────────────────────▲──────────────────────────────────────────┘
                           │ injected into the module (BaseImplement.Host/Vfs/Data)
┌──────────────────────────┴──────────────────────────────────────────┐
│  HCore.Packages.Nexus  (the connector MODULE — loadable)             │
│    Serve side:  AfcpServer backed by VfsAfcpProvider                │
│                 (generic VFS proxy: Sync/Read/Write/MkDir/Remove    │
│                  + Layer 2 Subscribe via IFacetView                 │
│                  + Layer 3 Call via IModuleResolver + FastMethodInfo)│
│    Mount side:  RemoteFileSystem (IVirtualFileSystem over AfcpClient)│
│                 RemoteModuleProxy<T> (DispatchProxy over Call)      │
│                 RemoteSubscription<T>  (Layer 2 adapter)            │
│    Implements:  IAfcpKernel (already in Base) — shell drives it     │
│                 via Host.GetModuleInterface<IAfcpKernel>("@afcp")   │
│    Wires:       HCore verbs (serializer + multiplex + path-messages)│
│                 ON TOP OF the upstream byte-stream stack             │
└──────────────────────────▲──────────────────────────────────────────┘
                           │ references
┌──────────────────────────┴──────────────────────────────────────────┐
│  AFCP  (the PROTOCOL lib — standalone, HCore-free, reusable)        │
│    Unified Streamy + testeMulti:                                     │
│      IConnection (duplex byte stream: TCP / serial / in-memory)      │
│      Streamy (decorator chain: Write/Read spans)                     │
│        → Framing       (length-prefix)                               │
│        → Checksum       (integrity decorator)                        │
│        → Crypto         (ECDH + AES)                                 │
│        → Camouflage     (HTTP-header disguise)                       │
│      ICountableStream (message-oriented over IConnection)            │
│      RequestBasedStream (request/response top layer)                 │
│    Hardened: disconnect/reconnect, multi-transport selection         │
│              (WiFi/Ethernet/serial), link health                     │
└─────────────────────────────────────────────────────────────────────┘
```

### Layering rules

1. **`AFCP` (protocol) knows nothing of HCore.** It is a byte-stream transform stack
   with a request/response top layer. Reusable for any RPC/streaming app, not just
   HCore. This is the upstream `Ardumine/afcp` repo, unified and hardened.

2. **`HCore.Packages.Nexus` (connector module) references only `HCore.Modules.Base` +
   `AFCP`.** It owns the HCore-shaped verbs (`Sync`/`Read`/`Write`/`MkDir`/`Remove`/
   `Subscribe`/`Call`), the serializer, and the multiplex — riding the protocol's
   byte-stream stack instead of duplicating transport/framing/checksum. It implements
   `IVirtualFileSystem` (mount side), `IAfcpProvider` (serve side), and `IAfcpKernel`
   (shell-facing). It is loaded at runtime like any package; the kernel has no
   compile-time dependency on it.

3. **`HCore.Main` (kernel) drops its `AFCP` reference.** It only exposes the contract
   surfaces (§4) and the `@afcp` kernel-service name passthrough. The shell's `afcp`
   command is unchanged — it already reaches `IAfcpKernel` through `GetModuleInterface`.

> The split mirrors Linux: the kernel owns `struct file`/`struct inode` and the mount
> table; an NFS/FUSE *module* implements the wire protocol. HCore's kernel owns the VFS
> + proc table + facet broker; the AFCP *module* implements the remote driver.

---

## 4. Phase 0 — Unify & harden the upstream AFCP protocol lib

**Goal:** make `Ardumine/afcp` the canonical byte-stream stack, HCore-free, with both
experiments unified and production-fit. This is upstream work (separate repo), tracked
here because the connector (Phase 2) depends on it.

> **Status:** Phase 0.A (KASerializer) and Phase 0.B (AFCP remodel) are **DONE**.
> Both repos pushed. The two upstream libs are clean, HCore-free foundations. Phase 1
> (HCore.Base contracts) + Phase 2 (connector module) are next.

### 0.1 Unify `testApp` (Streamy) and `testeMulti` (IConnection+ICountableStream)

**DONE.** The two experiments are unified into one layered classlib at
`github.com/Ardumine/afcp` (cloned at `hcore/afcp/afcp/`):

| Layer | Unified type | Lineage |
|---|---|---|
| Duplex byte transport | `IConnection` + `TcpConnection`/`SerialConnection`/`InMemoryConnection` | `testeMulti` |
| Span byte stream | `Streamy` + `StreamyFromConnection` | `testApp` |
| Length-prefix framing | `Framing : IMessageStream` | `DataCompletionTransformer`/`DataCompletionStream` |
| Integrity | `Checksum : IMessageStream` decorator | `CheckSumBasedStream` |
| Confidentiality | `Crypto : IMessageStream` decorator (ECDH+AES-CFB) | `EncryptionTransformer` |
| Camouflage | `Camouflage : Streamy` (HTTP disguise, optional) | `HTTPTransformer` |
| Request/response | `RequestChannel` (RequestId-demuxed multiplex) | `RequestBasedStream` |

`testeMulti`'s `RequestBasedStream` (one-shot) → `RequestChannel` (true concurrent
multiplex by `RequestId` — what makes AFCP work over a single serial channel). Originals
preserved under `samples/`. `AfcpStackBuilder` composes the canonical order and runs the
role-aware handshake bottom-up. 9/9 end-to-end tests pass (in-memory + TCP loopback).

### 0.2 Harden (current code is "very very simple")

**DONE.** Implemented in the unified lib:

- **Disconnect / reconnect.** `IConnection.IsConnected` + `OnDisconnect` event;
  `TcpConnection` surfaces socket errors; reads return 0 on EOF. `ReconnectingConnection`
  wraps a transport factory and retries with exponential backoff, firing
  `OnDisconnect`/`OnReconnect`.
- **Multi-transport selection.** `TransportRegistry.Open(target)` parses
  `tcp://host:port`, `serial:///dev/ttyUSB0?baud=115200`, `inmem://key`; custom
  schemes via `Register`. WiFi and Ethernet are both TCP (same `TcpConnection`).
- **Link health & timeout.** `RequestChannel.SendRequest` enforces a default 30s
  timeout (TODO §C7f, moved down to the protocol layer) unless the caller cancels.
- **Streaming writes.** `IMessageStream` is message-oriented (whole messages). A
  chunked/streaming variant for large file transfers (§C7e) is deferred — the
  HCore connector can chunk at the verb layer for now.

### 0.3 Where it lives

The unified lib stays in the `Ardumine/afcp` repo (or is vendored into `hcore/AFCP/`
*replacing* the current V2-port transport/framing/checksum — see Phase 2.1). It must
remain a plain classlib with **no HCore reference**.

> **Resolved (Phase 0.A — DONE):** the IL-emit serializer is now its own standalone
> repo, **`github.com/Ardumine/kaserializer`** (cloned at `hcore/afcp/kaserializer/`).
> Namespace `AFCP` → `KASerializer` (kills the CS0118 `Serializer`-vs-namespace
> collision). HCore-free, AFCP-free. 30/30 round-trip tests pass. Documented
> limitations: `Nullable<T>` value types unsupported; `List<>`/`Dictionary<,>` reach
> into private fields; parameterless-ctor required; **abstract bases unsupported as
> the polymorphic declared type** (the derived transformer builds a class transformer
> for the declared type first, and an abstract class has no public parameterless
> ctor — use a concrete non-sealed base). Both `AFCP` (if it needs typed control
> messages) and `HCore.Packages.Nexus` (the HCore verbs) reference it. **Decision:
> AFCP stays a pure byte-stream stack and does NOT reference KASerializer** — only
> the HCore connector serializes; KASerializer is "shared by both" in availability,
> not in that AFCP itself uses it.

---

## 5. Phase 1 — Contract surfaces in `HCore.Modules.Base`

The connector module can't reach kernel internals. Today the bridge is `internal` on
`ModuleHost`/`DataHost`/`FileSystem` and lives in `HCore.Main` to see them. To move it
out, the kernel must expose **contract surfaces** in Base (the "syscall" surface for a
filesystem driver). Five are needed:

### 4.1 VFS contract — move `IVirtualFileSystem` to Base

Currently `IVirtualFileSystem`/`IVirtualDirectory`/`IVirtualFile`/`VirtualNode` live in
`HCore.Main/Vfs/IVirtualFileSystem.cs`. A package **cannot implement** a `RemoteFileSystem`
without them. Move the whole set to `HCore.Modules.Base` (namespace `HCore.Modules.Base`).

### 4.2 `IKernelVfs` — mount table access

The bridge calls `FileSystem.Mount`/`Unmount`/`TryResolveMount`/`ListDirectory`/`GetFile`/
`CreateFile`/`MkDir`/`DeleteFile` directly. Expose a kernel handle (injected like
`IModuleFileSystem`):

```csharp
public interface IKernelVfs
{
    void Mount(string mountPoint, IVirtualFileSystem fs, bool replaceExisting = false);
    void Unmount(string mountPoint);
    bool TryResolveMount(string path, out IVirtualFileSystem fs, out string resolvedPath);
    // Serve-side primitives (the generic VFS proxy):
    IReadOnlyList<DirEntry> ListDirectory(string path);
    IVirtualFile GetFile(string path);
    void CreateFile(string path, ReadOnlySpan<byte> data, bool overwrite = true);
    void MkDir(string path);
    void DeleteFile(string path);
}
```

`IModuleFileSystem` (the existing user-space file API) stays as-is — it's the
*unprivileged* door. `IKernelVfs` is the *driver* door (mount/unmount + raw node ops);
inject it only into the AFCP module (it's the filesystem-driver capability).

### 4.3 `IFacetView` — non-generic facet access (Layer 2 serve side)

The serve side's `Subscribe` needs `DataHost.FindFacet(path)` (non-generic `IFacet`) and
`IFacet.SubscribeRaw(callback)`. Both are currently `internal`. Move to Base:

```csharp
public interface IFacetView
{
    IFacet? FindFacet(string facetPath);
}

public interface IFacet
{
    Type ValueType { get; }
    // Non-generic subscribe: hands the boxed value + Sequence + InterFrameDelta to a callback.
    ISubscription SubscribeRaw(
        Func<object?, DataEventMetadata, CancellationToken, ValueTask> handler,
        Action<DisconnectReason>? onDisconnected = null);
}
```

### 4.4 `IModuleResolver` — non-generic instance resolution (Layer 3 serve side)

`VfsAfcpProvider.Call` resolves a remote instance by path via the non-generic
`ModuleHost.TryResolveInstance`. Expose it:

```csharp
public interface IModuleResolver
{
    bool TryResolveInstance(string instancePath, out object? instance);
}
```

(The `Call` serve side then reflects + invokes via `FastMethodInfo` itself — that stays
in the module, not the kernel.)

### 4.5 `IRemoteMountHook` — the subscribe/call redirect extension point (mount side)

This is the crux. Today `DataHost.Subscribe<T>` and `ModuleHost.GetModuleInterface<T>`
have `internal` branches: if `TryResolveMount` resolves the path to a `RemoteFileSystem`,
they redirect to `IRemoteDataSource.SubscribeData<T>` / build a `RemoteModuleProxy<T>`.
Once `RemoteFileSystem`/`RemoteModuleProxy` move into the package, the kernel can't name
those types. It needs an **extension point**:

```csharp
public interface IRemoteMountHook
{
    // Called by DataHost before the local /proc parse. Returns null if the path isn't remote.
    ISubscription? TrySubscribeRemote<T>(
        string facetPath,
        Func<DataEvent<T>, CancellationToken, ValueTask> handler,
        Action<DisconnectReason>? onDisconnected) where T : class;

    // Called by ModuleHost.GetModuleInterface<T> before the local instance lookup.
    // Returns null if the path isn't remote.
    T? TryGetRemoteInterface<T>(string instancePath) where T : class, IModule;
}
```

The kernel holds a single `IRemoteMountHook?` reference (registered by the AFCP module on
init). `DataHost`/`ModuleHost` consult it; the module decides (via its own
`TryResolveMount`) whether the path is one of its mounts. This keeps the kernel free of
any AFCP type — the hook is a Base interface, the impl is in the package.

> This is the one genuinely new design piece (AFCP.md L528-540 listed the gap but didn't
> specify the hook). Everything else in §4 is "make the existing internal surface public
> in Base."

### 4.6 `IAfcpKernel` — already in Base

No change. The shell-facing contract (`Serve`/`StopServe`/`Mount`/`Unmount`/`Status`/
`SelfTest`) is already in `HCore.Modules.Base/IAfcpKernel.cs`. The `@afcp` kernel-service
registry passthrough stays in `ModuleHost` (it's a generic name→service lookup, not
AFCP-specific).

### 4.7 Phase 1 execution checklist

Concrete file-level steps (no AFCP code moves yet — this only exposes the kernel driver
surface in Base so a package can author a VFS driver / remote mount):

1. **Move VFS contracts.** `HCore.Main/Vfs/IVirtualFileSystem.cs` (`IVirtualFileSystem`,
   `IVirtualNode`, `IVirtualDirectory`, `IVirtualFile`, `VirtualNode`, `DirEntry`) →
   `HCore.Modules.Base/Vfs/`. Update `HCore.Main` VFS impls (`HostFileSystem`,
   `MemoryFileSystem`, `DeviceFileSystem`, `ProcFileSystem`) to the new namespace —
   they stay in `HCore.Main` but implement a Base interface.
2. **Add `IKernelVfs`** (`HCore.Modules.Base/IKernelVfs.cs`) — the driver door:
   `Mount`/`Unmount`/`TryResolveMount` + serve-side `ListDirectory`/`GetFile`/
   `CreateFile`/`MkDir`/`DeleteFile`. Implemented by `HCore.Main/Vfs/FileSystem.cs`
   (extract an explicit interface impl; the existing methods already match).
3. **Add `IFacetView` + move `IFacet`** (`HCore.Modules.Base/IFacetView.cs`) —
   `FindFacet(path) → IFacet?`; `IFacet` gains `ValueType` + `SubscribeRaw(...)`.
   `HCore.Main/Internal/DataHost.cs` + `DataFacet.cs` implement them (the
   `SubscribeRaw` impl already exists internally — make it public-via-interface).
4. **Add `IModuleResolver`** (`HCore.Modules.Base/IModuleResolver.cs`) —
   `TryResolveInstance(path, out object?)`. `ModuleHost` already has this internally;
   expose it.
5. **Add `IRemoteMountHook`** (`HCore.Modules.Base/IRemoteMountHook.cs`) — the new
   extension point (§4.5). `ModuleHost`/`DataHost` gain a single
   `IRemoteMountHook?` field + `RegisterRemoteMountHook` setter; their
   `Subscribe<T>`/`GetModuleInterface<T>` consult it *before* the local `/proc` parse.
   No default impl — null until the AFCP module registers.
6. **Inject the driver handles into the AFCP module.** `BaseImplement` (or the
   `IAfcpKernel` impl) receives `IKernelVfs`/`IFacetView`/`IModuleResolver` — either
   via `BaseImplement` fields (like `Vfs`/`Host`/`Data`) or via the kernel-service
   registration API. Decide: extend `BaseImplement` with optional driver handles, or
   pass them through a new `IAfcpKernel.Init(handles)` method called at registration.
7. **Verify:** `dotnet build hcore.sln` clean; existing `afcp test` still passes
   (the kernel bridge still works — it now goes through the same interfaces it always
   did, just typed against Base). No behavior change.

**No package is created in Phase 1.** The kernel still owns the bridge; Phase 1 only
makes the contract surface available so Phase 2 can move the bridge out.

---

## 6. Phase 2 — Build `HCore.Packages.Nexus` (the connector module)

New package, following the package-creation recipe in `AGENTS.md`:

1. `dotnet new classlib -n HCore.Packages.Nexus --framework net10.0`
2. Reference `HCore.Modules.Base` + the (unified) `AFCP` protocol lib. **Never** `HCore.Main`.
3. PostBuild → copy to `FS/packs/HCore.Packages.Nexus/` + `mpd` file.
4. Module triple: `IAfcpModule`/`AfcpImplement`/`ModDescriptor` (or reuse the existing
   `IAfcpKernel` as the module interface — it already is one).

### 5.1 What moves in (from `HCore.Main` + `hcore/AFCP/`)

| From | File | To |
|---|---|---|
| `HCore.Main/Internal/AfcpKernelService.cs` | `AfcpKernelService` + nested `VfsAfcpProvider` | `HCore.Packages.Nexus/` (split: `AfcpKernelService.cs` + `VfsAfcpProvider.cs`) |
| `HCore.Main/Vfs/RemoteFileSystem.cs` | `RemoteFileSystem` + `RemoteDirectory` + `RemoteFile` + `RemoteWriteStream` + `RemoteSubscription<T>` + `IRemoteDataSource` | `HCore.Packages.Nexus/` (`RemoteFileSystem.cs` + `RemoteSubscription.cs`) |
| `HCore.Main/Vfs/RemoteModuleProxy.cs` | `RemoteModuleProxy<T> : DispatchProxy` | `HCore.Packages.Nexus/RemoteModuleProxy.cs` |
| `HCore.Main/Internal/FastMethodInfo.cs` | compiled `Expression` delegate | `HCore.Packages.Nexus/FastMethodInfo.cs` (server-side Call fast path) |
| `hcore/AFCP/Protocol/` | `Messages.cs` + `MessageType.cs` (path-addressed verbs) | `HCore.Packages.Nexus/Protocol/` (the HCore-shaped message layer) |
| `hcore/AFCP/Serializer/` | IL-emit serializer | **already extracted** → `KASerializer` repo (Phase 0.A). Connector references it. |
| `hcore/AFCP/Multiplex/` | `MultiplexedConnection` + `Frame` | `HCore.Packages.Nexus/Multiplex/` (rides the protocol's `IMessageStream`/`RequestChannel` instead of its own transport) |
| `hcore/AFCP/AfcpServer.cs` + `AfcpClient.cs` + `IAfcpProvider.cs` + `AfcpStack.cs` | server/client over the multiplex | `HCore.Packages.Nexus/` — reworked to build on the unified protocol stack |

### 5.2 What the module drops (duplicated layers)

The current `hcore/AFCP/` ships its own `Transport/` (`TcpConnection`,
`TcpTransportListener/Client`), `Streams/` (`FramedTransport`,
`ChecksumFramedTransport`), and `AfcpStack.Build`. These duplicate the upstream
protocol's `IConnection` + `Framing` + `Checksum` decorators. **Delete them; ride the
protocol stack.** The connector's `AfcpServer`/`AfcpClient` become: build a protocol
`IConnection` (via the `TransportRegistry`), stack `Framing`+`Checksum`+`Crypto`, then
run the multiplex + path-messages on top.

### 5.3 How the module wires to the kernel

On `Run()` (or on first `IAfcpKernel` call), the module:
1. Receives injected `IKernelVfs` + `IFacetView` + `IModuleResolver` + `DataHost` +
   `ModuleHost` (via `BaseImplement.Host`/`.Vfs`/`.Data`, extended to carry the new
   driver handles).
2. Registers itself as the `IRemoteMountHook` on the kernel (so `DataHost.Subscribe<T>`
   and `ModuleHost.GetModuleInterface<T>` can redirect to its mounts).
3. Registers `@afcp` kernel-service name → itself (so the shell's
   `GetModuleInterface<IAfcpKernel>("@afcp")` finds it).

The shell's `afcp` command (`HCore.Packages.HShell/Shell/Commands/AfcpCommand.cs`) is
**unchanged** — it already goes through `IAfcpKernel`.

### 5.4 Self-test

`afcp test` (`AfcpKernelService.SelfTest`) moves verbatim into the module. It uses
`Host.Spawn`/`Host.GetModuleInterface` (kernel syscalls) to spin up the lidar demo and
the loopback mount — all through the Base contracts, no kernel internals.

---

## 7. Phase 3 — Retire `HCore.Main`'s AFCP reference

After Phase 2 the kernel no longer needs AFCP:

- Remove `<ProjectReference Include="AFCP/AFCP.csproj">` from `HCore.Main.csproj`.
- Delete `HCore.Main/Internal/AfcpKernelService.cs`,
  `HCore.Main/Vfs/RemoteFileSystem.cs`, `HCore.Main/Vfs/RemoteModuleProxy.cs`,
  `HCore.Main/Internal/FastMethodInfo.cs`.
- Remove the `@afcp` singleton construction in `Program.Init()`; the name now resolves
  to the loaded module instance (still via the `@`-service registry — the module
  registers itself on init).
- `ModuleHost`/`DataHost` keep only the `IRemoteMountHook` consult (§4.5); the
  `TryResolveMount` branch + `RemoteModuleProxy` construction move out.
- `HCore.Modules.Base/RemoteCallException.cs` stays (it's the user-space catch type).

`AGENTS.md` is updated: the "kernel-space bridge lives in `HCore.Main` (a shortcut)"
paragraph becomes "the AFCP↔HCore connector is the loadable module
`HCore.Packages.Nexus`; the kernel exposes the driver contract surface in Base."

---

## 8. Sequencing &amp; dependencies

```
Phase 0 (upstream AFCP: unify + harden)   ──┐
                                            │  connector rides the protocol stack
Phase 1 (Base contract surfaces)           ──┤
                                            │  module needs both to compile
Phase 2 (build HCore.Packages.Nexus)        ◄─┘
        └─ 2.1 move connector code
        └─ 2.2 drop duplicated transport/framing
        └─ 2.3 wire IRemoteMountHook
Phase 3 (retire HCore.Main AFCP ref)       ◄─ after Phase 2 passes `afcp test`
```

Phase 0 and Phase 1 are **independent** and can proceed in parallel. Phase 2 needs both.
Phase 3 is the cleanup after Phase 2's self-test passes.

**Recommended first cut if Phase 0 is deferred:** do Phase 1 + Phase 2 with the
connector still riding the *current* V2-port transport (don't wait for the upstream
unification). The protocol-stack swap (§5.2) is then a mechanical follow-up once Phase 0
lands. This gets the module extraction done without blocking on upstream work.

---

## 9. What this resolves / doesn't resolve

**Resolves:**
- The "AFCP is kernel-space" shortcut (AGENTS.md gotcha) — connector becomes a module.
- The duplicated transport/framing/checksum between upstream and in-tree.
- The `IVirtualFileSystem`-in-`HCore.Main` blocker for package-authored VFS drivers.

**Does NOT resolve (still open TODOs, tracked separately):**
- **§C3 capability model** — `Kill` + remote writes/subscribe/call remain unrestricted.
  The `IKernelVfs`/`IRemoteMountHook` surfaces in §4 are where a future capability check
  would attach, but the model itself is unbuilt.
- **§C7d typed wire errors** — `CallResponse{Success=false, Error}` stays a string.
- **§C7e large-file streaming** — the upstream `AFCP` lib's `IMessageStream` is
  message-oriented (whole messages); a chunked variant is deferred there too. The
  connector can chunk at the verb layer in the interim. ◐ infra exists upstream, not wired.
- **§C7f reconnect/timeout** — ◐ **partially addressed** by Phase 0.B: the upstream
  `AFCP` lib ships `ReconnectingConnection` (auto-reconnect w/ backoff + `OnReconnect`)
  and `RequestChannel` enforces a default 30s call timeout. The HCore connector still
  needs to wire these (Phase 2) — currently it uses `CancellationToken.None` and no
  reconnect. The protocol-layer infra exists; the integration is Phase 2 work.
- **§C4 config system** — `RemoteSlam`/`Module2` still read targets from `/tmp/*` files.

---

## 10. File map (target state)

**Upstream libs (DONE — Phase 0, HCore-free):**

| Repo / path | Contents |
|---|---|
| `github.com/Ardumine/kaserializer` → `hcore/afcp/kaserializer/` | `KASerializer` classlib — `Serializer`, `KAType`, `FastMethodInfo`, `SerializerTools`, `[IgnoreParse]`, `Serializers/*` (Unmanaged/String/Guid/Class/Nullable/Array/List/Dict/Derived/Span). 30 round-trip tests. |
| `github.com/Ardumine/afcp` → `hcore/afcp/afcp/` | `AFCP` classlib — `Transport/` (`IConnection`, `TcpConnection`, `TcpServer`, `SerialConnection`, `InMemoryConnection`, `ReconnectingConnection`, `TransportRegistry`); `Streamy/` (`Streamy`, `StreamyTransformer`, `StreamyFromConnection`, `StreamyFromStream`, `StreamFromStreamy`, `Camouflage`, `Logger`); `Message/` (`IMessageStream`, `MessageTransformer`, `Framing`, `Checksum`, `Crypto`, `RequestChannel`); `AfcpStackBuilder`. 12 tests. Originals under `samples/`. |

**HCore workspace (Phase 1+2 — pending):**

| File | Layer |
|---|---|
| `HCore.Modules.Base/IVirtualFileSystem.cs` | VFS contract (moved from `HCore.Main/Vfs/`) |
| `HCore.Modules.Base/IKernelVfs.cs` | Mount-table + raw-node driver surface (new) |
| `HCore.Modules.Base/IFacetView.cs` | Non-generic facet access (new; `IFacet.SubscribeRaw` moved here) |
| `HCore.Modules.Base/IModuleResolver.cs` | Non-generic instance resolution (new) |
| `HCore.Modules.Base/IRemoteMountHook.cs` | subscribe/call redirect extension point (new) |
| `HCore.Modules.Base/IAfcpKernel.cs` | Shell-facing contract (unchanged) |
| `HCore.Modules.Base/RemoteCallException.cs` | User-space catch type (unchanged) |
| `HCore.Packages.Nexus/AfcpKernelService.cs` | `IAfcpKernel` impl + serve/mount orchestration (moved from `HCore.Main/Internal/`) |
| `HCore.Packages.Nexus/VfsAfcpProvider.cs` | `IAfcpProvider` over `IKernelVfs`+`IFacetView`+`IModuleResolver` (moved) |
| `HCore.Packages.Nexus/RemoteFileSystem.cs` | Mount-side `IVirtualFileSystem` + `IRemoteDataSource` (moved) |
| `HCore.Packages.Nexus/RemoteModuleProxy.cs` | `DispatchProxy` over `Call` (moved) |
| `HCore.Packages.Nexus/RemoteSubscription.cs` | Layer 2 adapter (moved) |
| `HCore.Packages.Nexus/FastMethodInfo.cs` | Server-side Call fast path (moved) |
| `HCore.Packages.Nexus/Protocol/` | HCore verbs (`Sync`/`Read`/`Write`/`Subscribe`/`Call`) serialized with `KASerializer`, transported over the upstream `AFCP` stack |
| `HCore.Packages.HShell/Shell/Commands/AfcpCommand.cs` | `afcp` shell command (unchanged) |

> **Serializer decision (resolved):** the V2-port IL-emit serializer is now
> `KASerializer` (its own repo). The connector references it for the HCore verbs. The
> upstream `AFCP` lib is a pure byte-stream stack and does **not** reference
> `KASerializer` — "shared by both" in availability, not in that AFCP itself serializes.
