# HCore V3 vs V2 (Ardumine/kernel) — Feature Comparison

This document is a close, file-level comparison between the **third iteration**
(HCore V3 — this repository) and the **second iteration** (`Ardumine/kernel`,
https://github.com/Ardumine/kernel), evaluated at commit **`9d8526c`** — the last
state of V2 *before* commit `8627f42` ("Remove data channels from the kernel
itself. It will get replaced by an module.") stripped the data-channel plane.

V2 was a **distributed, networked robotics micro-kernel**. V3 is a **correct,
single-process micro-kernel** with a real VFS and a clean process hierarchy, but
it has shed the entire distributed / data-plane / configuration story. This
document enumerates every feature present in V2 and absent in V3 (and vice-versa),
with the exact files and mechanics so a porting effort can be planned.

---

## 1. AFCP — the wire protocol / networking stack  *(V2: yes · V3: none)*

The single largest gap. `Kernel.AFCP/` in V2 is a complete TCP networking and
serialization layer — the foundation that made V2 *distributed*.

| V2 piece | File | Purpose |
|---|---|---|
| TCP transport | `Kernel.AFCP/AfcpTCPClient.cs`, `ChannelManagerServer.cs`, `ChannelManagerClientConector.cs`, `Kernel/ChannelManagerAFCPServerConector.cs` | Kernel-to-kernel TCP sockets, request/response framing |
| Packet types | `Kernel.AFCP/Packets/PacketChannel.cs`, `PacketChannelManagement.cs`, `PacketConnect.cs`, `PacketModFunc.cs`, `PacketSync.cs` | Typed messages: channel data, channel mgmt, connect/handshake, module-func calls, descriptor sync |
| Binary serializer | `Kernel.AFCP/Serializer/Serializer.cs` + `Serializers/{Array,Class,Dict,Guid,Lista,Nullable,Span,String,Unmanaged,Derived}Serializer.cs`, `Atributes.cs`, `SerializerTools.cs`, `KAType.cs` | A hand-written reflection-free binary serializer with pluggable per-type serializers |
| Fast invocation | `Kernel.AFCP/FastMethod.cs` | `FastMethodInfo` — compiled delegates to avoid reflection on hot call paths |
| Connect handshake | `Kernel.AFCP/Systems/ConnectSystem.cs` | Kernel-to-kernel connect/disconnect (`PacketConnectRequest`, with a `Disconnect` flag) |
| Boot | `Kernel/Program.cs` starts an `AfcpServer(8000)` | The kernel listens on TCP port 8000 |

**V3 has nothing analogous.** `HCore.Main/Program.cs` does no networking at all.
Inter-module calls in V3 are pure in-process CLR virtual dispatch: a module holds
an interface (`IModuleHost`, `IModuleFileSystem`, or a module-specific interface
like `IUsbDevice`) and calls it directly. There is no wire format, no framing, no
serializer — and therefore **no way for a call to cross a process boundary**.

The implication: any feature in V2 that depended on remote kernels (data-channel
mirroring, remote module calls, multi-kernel sync) is structurally impossible in
V3 until a transport is reintroduced.

---

## 2. Multi-kernel / distributed model  *(V2: yes · V3: none)*

V2 modeled "a kernel" as a first-class, addressable entity:

```
Kernel.AFCP/KernelManagement/KernelDescriptor.cs   // Guid + IsLocal
Kernel.AFCP/KernelManagement/RunningKernel.cs      // abstract: SendRequest<T>(msgType, payload)
Kernel.AFCP/KernelManagement/LocalRunningKernel.cs  // the local kernel (SendRequest throws — local is direct)
Kernel.AFCP/KernelManagement/RemoteRunningKernel.cs // a peer we are connected to, via ChannelManagerAfcpClientConector
Kernel.AFCP/KernelManager.cs                        // the registry of known kernels
```

A `KernelDescriptor` carries a `Guid` and an `IsLocal` flag. `LocalRunningKernel`
is the kernel itself; `RemoteRunningKernel` wraps a `ChannelManagerAfcpClientConector`
and forwards `SendRequest<T>` over the TCP link. `KernelManager` tracks the set of
connected peers and fires `OnRemoteKernelConnect` / `OnRemoteKernelRequestDiconnect`.

This is what let V2 do `PacketSync` (exchange channel/module descriptors between
kernels) and route a call to a module living on a different machine.

**V3 is strictly single-kernel, single-process.** There is no concept of a
"kernel identity", no `Guid`, no peer registry. `HCore.Main/Internal/ModuleHost.cs`
is the one and only process table; everything resolves against the local
`_instances` dictionary.

---

## 3. Data channels — the streaming / pub-sub data plane  *(V2: yes, removed by `8627f42` · V3: none)*

This is exactly what commit `8627f42` deleted from V2 — so it **was present** at
`9d8526c`, the state we compare against. It was a streaming/evented data plane
**separate** from the RPC method-call path.

### Mechanics (V2)

- `Kernel.AFCP/Channel.cs` — `DataChannelInterface<T>` (abstract), with
  `LocalDataChannelInterface<T>` and `RemoteDataChannelInterface<T>`.
  Each holds a value of `T`, a list of `Action<T?>` event handlers, and
  `Set`/`Get`/`AddEvent`/`RemoveEvent`.
- `Kernel.AFCP/DataChannelManager.cs` — creates local data channels
  (`CreateLocalDataChannel<T>`), resolves them by path, and crucially
  **propagates changes across kernels**: `NoitifyChannelDataChanged` walks the
  channel's `KernelsToNotify` list and sends a `PacketChannelManagementRequest`
  (`ChannelDataChange` message) to each registered remote kernel.
  `SetValueRemote` / `GetValueRemote` / `AddEventRemote` / `RemoveEventRemote`
  drive a *remote* kernel's channel over the wire.
- `Kernel/Modules/Classes/DataChannelInterface.cs` + `ExistingDataChannelDescriptor.cs`
  — the kernel-side wrapper handed to modules.
- `Kernel.AFCP/DataReader.cs` / `DataWritter.cs` — streaming helpers.
- The config flag `HighData` (`Kernel/ConfigParser.cs` → `ConfigChannelDescriptor`)
  marked a channel as high-throughput.

### Author surface (V2)

In `Kernel.Modules.Base/BaseImplement.cs`:
```csharp
public IExistingDataChannelDescriptor? InitiateChannel<T>(string Path);   // host a channel
public IDataChannelInterface<T>? GetChannelInterface<T>(string Path);     // subscribe to one
```
A module could *host* a typed channel and *subscribe* to value-change events on
another module's channel — locally or on a remote kernel. This was the intended
path for sensor streams (lidar scans, IMU frames, etc.).

### V3

**V3 has no data plane at all.** The only inter-module communication is
synchronous method calls through injected interfaces. There is no event bus, no
pub/sub, no streaming abstraction. V2's author removed it from the kernel
intending to reimplement it *as a module*; V3 never did that reimplementation.

This is the most impactful functional regression for a robotics kernel: there is
no efficient way to push high-frequency sample streams between modules.

---

## 4. MKCalls — the module↔kernel RPC "syscall" surface  *(V2: yes · V3: replaced by direct injection)*

V2's modules did not hold direct kernel references. They held an `MKCallClient`
that issued typed request objects to an `MKCallReceiver` in the kernel — a
deliberate RPC boundary.

```
Kernel.Modules.Base/MKCalls/MKCallClient.cs        // module side: MakeCall<T>(request)
Kernel.Modules.Base/MKCalls/MKCallRequest.cs       // abstract request
Kernel.Modules.Base/MKCalls/MKCallResult.cs        // abstract result
Kernel.Modules.Base/MKCalls/RequestTypes/          // GetModuleInterface, GetDataChannelInterface,
                                                   //   InitiateDataChannel, SubModules/{Create,Start,Stop,Delete}
Kernel.Modules.Base/MKCalls/ResultTypes/           // matching results + NoValueResult
Kernel/Modules/MKCallReceiver.cs                   // kernel side: CallHandler dispatches by request type
```

### The function-pointer bridge (V2)

`MKCallClient` is constructed with a **raw function pointer**
(`delegate*<MKCallRequest, MKCallResult>`) into the kernel. `ModuleCreator.cs`
builds that pointer with **`System.Reflection.Emit`**: it emits a dynamic type
with a static method that loads a static `MKCallReceiver` field and calls
`CallHandler`, then takes `MethodHandle.GetFunctionPointer()`. The comment in
`MKCallReceiver.cs` explains the goal: prevent a module from reaching the
`ChannelManager`/`ModuleManager` via reflection — the module only ever sees the
function pointer, never the receiver object.

### The dispatch (V2)

`MKCallReceiver.CallHandler` switches on the request's generic type definition
(`InitiateDataChannelRequest<>`, `GetDataChannelInterfaceRequest<>`,
`GetModuleInterfaceRequest<>`, `CreateSubModuleRequest<>`, `StartSubModuleRequest`,
`StopSubModuleRequest`, `DeleteSubModuleRequest`) and routes each to the right
kernel manager — **enforcing permissions along the way** (see §7).

### V3

V3 **dissolved the RPC boundary**. `HCore.Modules.Base/BaseImplement.cs` is
injected with concrete interface objects:

```csharp
public IModuleFileSystem Vfs { get; }        // AttachVfs(...)
public IModuleHost Host { get; }             // AttachHost(...)
public IModuleLogger Logger { get; }         // AttachLogger(...)
public string InstanceName { get; }          // AttachInstanceName(...)
```

`IModuleHost` (`HCore.Modules.Base/IModuleHost.cs`) exposes `GetModuleInterface<T>`,
`Spawn<T>`, `SpawnChild<TImpl>`, `SpawnChildByName<T>`, `KillChild`, `Kill` —
implemented by `ModuleHost` (kernel) and `ScopedModuleHost` (per-instance owner
facade). Calls are direct CLR virtual dispatch.

**Trade-off:** V3 is dramatically simpler and faster, and avoids V2's reflection
attack surface. But because there is no RPC boundary, every cross-cutting feature
that wanted to sit *on* the wire (logging, permissions, remote routing, tracing)
loses its natural insertion point. Reintroducing networking in V3 will require
either re-creating an MKCall-style indirection or proxying interfaces (see §5).

---

## 5. Transparent dispatch proxy  *(V2: yes · V3: none — direct dispatch)*

`Kernel/Modules/Helpers/ModuleProxy.cs` — a `System.Reflection.DispatchProxy`
subclass that made a module interface call look local while actually routing it
through a `ModuleChannel`:

```csharp
public static T CreateProxy<T>(ModuleChannel moduleChannel) { ... }
// each call becomes: channel.Run(methodIndex, args)
```

`ModuleCreator.CreateMethodCache` assigned each interface method a `uint` index
(via `FastMethodInfo` compiled delegates), and `ModuleChannel.Run` dispatched
either locally (`RunFuncLocal` → `CacheMethods[path][FuncID](impl, Params)`) or
remotely (over AFCP). This is what gave V2 **location transparency**: the same
`IModule1` call worked whether the target was in-process or on another kernel.

**V3 doesn't need a proxy** — `GetModuleInterface<T>` returns the actual
`BaseImplement`-derived object cast to `T`. But the loss is the loss of the
location-transparency abstraction: in V3, "call another module" and "call another
machine" cannot share one code path.

---

## 6. Module lifecycle granularity  *(V2: Create/Start/Stop/Delete · V3: Spawn+init / Kill)*

### V2

`BaseImplement` declared three lifecycle hooks plus a delete:
```csharp
public abstract void Prepare();
public abstract void Start();
public abstract void EndStop();
public virtual  void Delete() { logger.LogW("Delete was not implemented!"); }
```
And `ModuleDescriptor` (`Kernel/Modules/Classes/ModuleDescriptor.cs`) tracked
`IsPrepared`, `IsRunning`, `IsDeleted` and exposed `Prepare()`/`Start()`/`Stop()`/`InternalDelete()`.
Sub-modules had the **same four operations individually**:

```
MKCalls/RequestTypes/SubModules/CreateSubModuleRequest.cs   // CreateSubModule(path, name, startOnCreation)
MKCalls/RequestTypes/SubModules/StartSubModuleRequest.cs
MKCalls/RequestTypes/SubModules/StopSubModuleRequest.cs
MKCalls/RequestTypes/SubModules/DeleteSubModuleRequest.cs
```

So a V2 module could create a child *dormant*, start it later, stop it without
deleting it, and restart it. `CreateSubModule` took a `startOnCreation` flag.

### V3

V3 collapsed this to **two verbs**: `SpawnChild` (construct + run `init` + publish)
and `KillChild`/`Kill` (reap). There is no separate start/stop, no "create but
don't start", no restart. `BaseImplement` has only `OnKilled()` (a reap hook) and
`DescribeForProc()`; `IRunnable.Run()` is the single execution entry. V3's
`plan.md` explicitly defers "full process lifecycle (start/stop/pause)" as future
work.

The V2 `Stop()`→cascade also had a known weakness (it logged *"parent didn't stop
the sub-module, stopping manually"* in `ModuleDescriptor.Stop`); V3 fixed that
*structurally* via `ParentName` + `KillLocked` leaf-first cascade — but threw away
the granular start/stop in the process.

---

## 7. Permissions / capability model  *(V2: embryonic · V3: none, documented gap)*

V2 had the seed of a capability system:

```
Kernel/Modules/Permissions/IModulePermissions.cs
Kernel/Modules/Permissions/Permission/ModulePermission.cs            // base
Kernel/Modules/Permissions/Permission/ConnectChannelPermission.cs    // RealPath + MPath
Kernel/Modules/Permissions/Permission/HostChannelPermission.cs       // RealPath + MPath
```

Each module carried an `IModulePermissions` list, populated by `ModuleCreator`
from the config's `connectedChannels` / `hostingChannels`. `MKCallReceiver`
called `WorkingModule.ModulePermissions.SolveMPath(MPath)` before every
data-channel access — if the module had no `ConnectChannelPermission` for that
`MPath`, it threw `SecurityException`. `SolveMPath` also **resolved a relative
`#name` to the real channel path**, so modules addressed channels by symbolic
names, not absolute paths.

**V3 has no capability model at all.** `IModuleHost.Kill` is unrestricted
(documented gap in `AGENTS.md` and `plan.md`): any module can kill any other
module's subtree. `GetModuleInterface<T>` and `Spawn<T>` have no permission
checks. V3's `ScopedModuleHost` enforces *ownership* (a module can only
`KillChild` its own children) but not *capability* (there is no concept of "module
A is allowed to call module B").

---

## 8. Declarative configuration (`config.json`)  *(V2: yes · V3: none — hardcoded boot)*

### V2

`Kernel/ConfigParser.cs` loads `config.json` into `ConfigFile.Config`:
```csharp
public required string[] ModulesDlls;                 // DLLs to load
public required StartupModuleConfig[] StartupModules; // what to spawn at boot
```
with each `StartupModuleConfig`:
```csharp
public string ModuleName;
public string Path;
public bool StartOnBoot;
public string? startAfter;                          // dependency ordering
public List<ConfigChannelDescriptor> hostingChannels;
public List<ConfigChannelDescriptor> connectedChannels;
public Dictionary<string, object>? Config;          // per-module config values
```
and `ConfigChannelDescriptor` (`Path`, `Name`, `HighData`).

`Program.cs` read this, loaded the DLLs via `ModuleLoader`, created each declared
instance via `ModuleManager.CreateLocalModuleInstance`, then called
`StartModulesViaTree()`.

### V3

**No config file.** `HCore.Main/Program.cs`:
- hardcodes the VFS root (`Mount("/", new HostFileSystem("/home/ardumine/hort/hcore/FS"))`),
- hardcodes the init module name (`host.Spawn<IRunnable>("HCore.Packages.HInit.Init", "init")`),
- discovers modpacks by scanning `/packs` for `mpd` files.

Boot-time module spawning is done **by shell scripts** (`.svc` files run by init's
worker shell) rather than by a declarative manifest. There is no `startAfter`
dependency graph and no per-module `Config` dictionary.

---

## 9. Per-module config injection  *(V2: yes · V3: none)*

V2: `Kernel.Modules.Base/IModuleConfigManager.cs` + `Kernel/Modules/ModuleConfigManager.cs`:
```csharp
public T? GetConfigValue<T>(string name);   // reads moduleConfig dict (from config.json)
```
injected into every `BaseImplement` as `ConfigManager`, and called by
`BaseImplement.GetConfigValue<T>(name)`.

**V3 modules receive only `Vfs`, `Host`, `Logger`, `InstanceName`.** There is no
config-injection channel; a module that needs parameters must receive them through
its `init` delegate (e.g. `SpawnChild<UsbDeviceImplement>("device0", d => d.Init("SN-A","1-1.2"))`),
which is hardcoded at the call site, not declarative.

---

## 10. Dependency-ordered boot / shutdown tree  *(V2: yes · V3: none)*

V2: `Kernel/Modules/Classes/StartModTree.cs` (`StartModBranch` with `Dependers`)
and `ModuleManager.StartModulesViaTree` / `StopAndDeleteModulesViaTree`. These
computed a topological order from each module's `startAfter` field and
started/stopped modules in dependency order, with manual sub-module reaping as a
fallback.

**V3 has no dependency graph.** Init runs `.svc` scripts in whatever order the
shell walks them; shutdown is a cascade kill, not an ordered stop.

---

## 11. Channel wiring declared in config  *(V2: yes · V3: none)*

V2's `hostingChannels` / `connectedChannels` config lists, combined with
`ModuleManager.ResolveChannelPathForMod` (resolving `#relativeName` → real path
via the permission table), let you declare a module's data-channel topology in
`config.json` and have the kernel wire it at boot — no code.

**V3 has no channels to wire** (see §3) and no symbolic-name resolution.

---

## 12. Real robotics hardware modules  *(V2: yes · V3: stub only)*

V2's commit history shows working drivers:
- **YDLidar** driver (`c58188e` "Removal of YDLidar Deps", `9d8526c` — so it was
  present, then being剥离'd) and **hectorSlam** SLAM (`14e5d62` "hectorSlam
  improvements. Better kernel communication").
- `Modules.USB/` with two real modules in one pack: `USBController` and `USBPort`
  (`IUSBController`, `IUSBPort`, descriptors + implements).

**V3's only non-demo module is `HCore.Packages.Usb`**, and it is a **demonstration
stub**: `UsbDeviceImplement.Read` returns nothing; it exists to show `/proc`
nesting and `DescribeForProc()`. There is no lidar, no SLAM, no real USB I/O.

This is the practical capability gap: V2 drove real hardware; V3 does not.

---

## 13. HViz — visualization  *(V2: yes · V3: none)*

Referenced throughout V2's commit log (`51cd453`, `12fcc84`, `0fb6842`, `eaf6f80`
"Logger update" tied to HViz). A visualization companion tool for inspecting
kernels/channels/modules at runtime.

**V3 has no equivalent.** The closest is the `/proc` synthetic filesystem, which
is read-only text.

---

## 14. Module loading & isolation  *(V2: plain LoadFile · V3: AssemblyLoadContext)*

This is an area where **V3 is strictly better**.

- **V2** `Kernel/ModuleLoader.cs` uses `Assembly.LoadFile(path)` into the default
  context, with an `AppDomain.AssemblyResolve` handler to satisfy cross-module
  dependencies. No isolation; no unloading; shared types by accident of the
  default context.
- **V3** `HCore.Main/Internal/ModPackAssemblyLoadContext.cs` loads each modpack
  from a stream into its own `AssemblyLoadContext` that **falls back to Default
  for `HCore.Modules.Base`** — so the shared-contract assembly keeps a single
  identity across all load contexts (this is what makes cross-module interface
  calls actually work). Descriptors are discovered via `IModuleDescriptor` and
  registered in `_loadedModuleDescriptors`.

V3 also reads each modpack's `mpd` file (plain text: DLL name line 1, optional
PDB line 2) from the VFS, loading the DLL+PDB as streams — fully VFS-driven,
unlike V2's raw filesystem paths.

---

## 15. VFS / process model  *(V2: none · V3: full)*

V3 gains an entire layer V2 never had:

```
HCore.Main/Vfs/FileSystem.cs          // mount tree + segment-wise path resolution
HCore.Main/Vfs/IVirtualFileSystem.cs  // the mount interface
HCore.Main/Vfs/HostFileSystem.cs      // real disk backing
HCore.Main/Vfs/MemoryFileSystem.cs    // tmpfs
HCore.Main/Vfs/DeviceFileSystem.cs    // /dev
HCore.Main/Vfs/ProcFileSystem.cs      // synthetic, nested /proc view of running instances
HCore.Main/Vfs/ModuleFileSystemProxy.cs // per-module VFS handle (the module's "syscall" for files)
HCore.Main/Vfs/PathHelpers.cs, VirtualNode.cs
```

- A unified mount tree (`/`, `/dev`, `/tmp`, `/proc`, `/packs`) with segment-wise
  resolution.
- **`/proc`** as a synthetic, read-only, **nested** view of running instances
  (split instance keys on `/`), rebuilt on every access from `ModuleHost`.
- **`/packs`** = installed modpacks; **`/proc`** = running instances.
- The module's file access is funneled through `IModuleFileSystem`
  (`HCore.Modules.Base/IModuleFileSystem.cs`) injected as `Vfs` — the module's
  only file "syscall".

V2 had **no filesystem abstraction at all** — modules talked to the kernel
through channels/MKCalls, with no unified file namespace.

---

## 16. Init / shell / services architecture  *(V2: none · V3: full)*

V3 introduces a Unix-like PID-1 split:

- **`HCore.Packages.HInit`** is PID 1 (`/proc/init`), a `ContainerImplement`
  implementing `IServiceManager` (`HCore.Modules.Base/IServiceManager.cs`). On
  `Run()` it spawns a worker shell `/proc/init/svc`, boots every
  `/etc/services/*.svc` script, then spawns the interactive console
  `/proc/init/console` and blocks on it.
- **`HCore.Packages.HShell`** (`IShell`, `HCore.Modules.Base/IShell.cs`) is the
  shell, with an `ICommand` + `CommandRegistry` dispatch shared by the REPL and
  `RunScript` (`Shell/Commands/`).
- The shell's `service` command reaches init via
  `GetModuleInterface<IServiceManager>("init")` — the interface lives in Base so
  it crosses ALCs.

V2 had no shell, no service manager, no script-driven boot — `Program.cs` built
everything imperatively from `config.json` and then `Console.ReadLine()`'d.

---

## 17. Structural cascade kill & ownership  *(V2: half-built · V3: correct)*

- **V2** `ModuleDescriptor.Stop()` / `ModuleManager.DeleteModule()` did a manual
  sub-module reap with a warning log (*"The module did not stop the sub-module
  ... Stopping manually..."*). Ownership was a `//TODO` — `MKCallReceiver`'s
  Start/Stop/Delete sub-module handlers explicitly say
  *"TODO: Checks to check if the module is an sub-module of the parent module"*.
- **V3** `ModuleHost` carries a single flat `_instances` registry plus a
  `ParentName` edge on each `RunningInstance`. `KillLocked` collects the whole
  subtree by matching `ParentName`, removes them all, and reaps leaf-first.
  `SpawnChildCore` runs `init` **outside the lock, before publish** (init-before-
  publish; no half-built node observable in `/proc`), and re-checks both name
  freedom and owner liveness before publishing. `ScopedModuleHost` binds the
  owner's instance name, so a module physically cannot forge another parent's
  `SpawnChild`/`KillChild`.

---

## 18. Logging  *(V2: Kernel.Logging · V3: Logyt + IModuleLogger)*

- **V2** `Kernel.Logging/`: `ILogger`, `Logger`, `LogIterator`, with
  `CreateSubLogger` used throughout (`ModuleManager`, `ModuleCreator`, etc.).
- **V3** `Logyt/` (`ConsoleLogyt`, `TextWritterLogyt`, `MessageType`) +
  `HCore.Modules.Base/IModuleLogger.cs` + `HCore.Main/Internal/ModuleLogger.cs`,
  injected into each `BaseImplement` as `Logger` (with an `EmptyModuleLogger`
  no-op default).

Lateral move / modest improvement, not a regression.

---

## Summary table

| Feature | V2 (`9d8526c`) | V3 (HCore) |
|---|:--:|:--:|
| AFCP TCP networking + serializer | ✅ | ❌ |
| Multi-kernel / remote kernels | ✅ | ❌ |
| Data channels (streaming pub/sub) | ✅ (removed by `8627f42`) | ❌ |
| MKCalls RPC syscall boundary | ✅ | ❌ (direct injection) |
| Transparent dispatch proxy | ✅ | ❌ (direct dispatch) |
| Permissions / capability model | ✅ (embryonic) | ❌ (documented gap) |
| `config.json` declarative boot | ✅ | ❌ (hardcoded + scripts) |
| Per-module config injection | ✅ | ❌ |
| Dependency-ordered boot tree | ✅ | ❌ |
| Config-declared channel wiring | ✅ | ❌ |
| Granular Create/Start/Stop/Delete | ✅ | ❌ (Spawn+init / Kill) |
| Real hardware drivers (lidar/SLAM/USB) | ✅ | ❌ (USB stub only) |
| HViz visualization | ✅ | ❌ |
| AssemblyLoadContext isolation | ❌ (`LoadFile`) | ✅ |
| Unified VFS + mount tree | ❌ | ✅ |
| `/proc` synthetic process view | ❌ | ✅ |
| Init/shell/services PID-1 architecture | ❌ | ✅ |
| Structural cascade kill + ownership | ⚠️ (half-built) | ✅ |
| Module hierarchy / sub-modules | ⚠️ (unfinished) | ✅ |

---

## Porting outlook (suggested order)

V3 is the better *kernel core*; V2 was the richer *system*. The natural porting
order, each step unblocking the next:

1. **Data channels as a module** — V2's own last commit (`8627f42`) declared this
   the intent. A `HCore.Packages.Channels` pack exposing `IChannel<T>` /
   `IDataChannel<T>` through `HCore.Modules.Base` would restore the pub/sub data
   plane without polluting the kernel. Highest functional value for a robotics
   kernel (sensor streams).
2. **`config.json` + per-module config injection** — add `IModuleConfigManager`
   to `HCore.Modules.Base` (mirroring V2's) and a `ConfigParser` in the kernel,
   wired at `Spawn` time. Low risk, replaces hardcoded boot.
3. **Capability model** — even a coarse allow-list on `IModuleHost.Kill` /
   `GetModuleInterface` would close V3's documented unrestricted-`Kill` gap and
   resurrect V2's `MPath`-style symbolic addressing.
4. **Granular lifecycle** — split `SpawnChild` into Create/Start/Stop and add
   `IRunnable.Stop()` (V3 already separates construction from `Run()`; this is
   mostly additive).
5. **AFCP transport + remote kernels** — the big one. Requires reintroducing an
   RPC indirection (MKCall-style or interface proxy) so calls can be routed over
   a wire. Only worth it once #1–#4 make V3 worth distributing.
6. **Real drivers** — port V2's `Modules.USB` (`USBController`/`USBPort`) and
   YDLidar/hectorSlam once #1 (data channels) exists, since they depend on a
   streaming plane.
