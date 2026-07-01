# Design D — Module Hierarchy / Sub-Modules — Implementation Plan

## Context

HCore V3 stalled on two intertwined questions: how a module owns **sub-modules**, and how modules live in the **VFS**. After a full design debate (recorded in `docs/modules/MODULE_HIERARCHY.md`), **approach D** was chosen: a parent module owns real, stateful child module instances that appear at `/proc/<parent>/<child>`, are created by the parent (which passes their data), are callable from anywhere, and whose lifetime is **structurally coupled** to the parent (destroy parent → children reaped by the kernel). The author writes ~one verb; the kernel does the wiring. This is the shape the 2nd iteration (`Ardumine/kernel`) reached for but only half-built (its code logs "parent didn't stop/delete its child, doing it manually" — the failure D eliminates).

Two choices confirmed with the user:
- **`/proc/<child>/info` shows the module's own data** (e.g. a USB device's serial + location) via a small author hook.
- **Kill authority:** the shell's `kill` is privileged (can reap any subtree); a module's `KillChild` is owner-scoped. (No capability model yet → unrestricted `Kill` is a documented gap to close later.)

**Acceptance criteria:** (1) structural cascade; (2) init-before-publish (no half-built node visible in `/proc`); (3) owner-enforced child kill; (4) single authoritative registry (no drift); (5) cross-package safe (child interfaces in `HCore.Modules.Base`); (6) one-verb author API (no `new`, no Vfs/Host wiring, no path strings, no teardown).

## Resolved design decisions

- **SpawnChild by concrete type** (primary): `SpawnChild<UsbDeviceImplement>("device0", d => d.Init(...))` — resolves the descriptor via a new `Type→descriptor` index; typed `init`, no cast, no magic string. `SpawnChildByName<T>(moduleName, name, init)` is the cross-package escape hatch (returns the interface; interface must be in Base).
- **Ownership via a per-module host facade** (`ScopedModuleHost`) injected at create time, binding the owner's instance name — a module physically cannot forge/squat another parent. Structural fix for the 2nd iteration's unfinished ownership `//TODO`.
- **One flat registry + `ParentName` edge** on `RunningInstance` (single source of truth; no second sub-host table to drift). The composite key (`"usb/device0"`) encodes the tree; `ParentName` makes the edge authoritative for cascade.
- **Init runs outside the lock, before publish** (see Concurrency) so nested spawns don't deadlock and no half-built node is observable.
- **Cascade `Kill`** reaps the target + all transitive descendants leaf-first, calling a minimal `OnKilled()` hook. Not `IDisposable` (full process lifecycle stays future work).
- **`/proc` nested render** by splitting instance keys on `/` (reuses `ReadOnlyVirtualDirectory`); `info` includes the module's `DescribeForProc()` output.

## Changes by file

### `HCore.Modules.Base/`

- **`BaseImplement.cs`** (edit): add
  - `public string InstanceName { get; private set; } = "";` + `public void AttachInstanceName(string name)` (kernel-injected in `Create`, mirrors `AttachVfs`/`AttachHost`).
  - `protected internal virtual void OnKilled() { }` — default no-op; overridden to release resources on reap.
  - `protected internal virtual string? DescribeForProc() => null;` — module-authored extra `info` lines (the facet for the user's serial/location choice).
- **`IModuleHost.cs`** (edit): add to the interface (implemented by `ModuleHost` + `ScopedModuleHost`; `EmptyModuleHost` throws `NotAttached()` for each):
  - `T SpawnChild<TImpl>(string leafName, Action<TImpl>? init) where TImpl : IModule;`
  - `T SpawnChildByName<T>(string moduleName, string leafName, Action<T>? init) where T : IModule;`
  - `void KillChild(string leafName);`
  - `void Kill(string instancePath);`  ← privileged; the shell uses this.
- **`ContainerImplement.cs`** (NEW): `abstract class ContainerImplement : BaseImplement` exposing `protected` `SpawnChild<T>`, `SpawnChildByName<T>`, `KillChild` that forward to the injected `Host` facade (owner already bound). This is the one-verb author surface; child ops stay off the author's `Host` happy-path by convention (only reachable via this base).
- **`IUsbDevice.cs`** (NEW): `public interface IUsbDevice : IModule { string Serial { get; } string Location { get; } byte[] Read(int len); }` — **must live in Base** for cross-package callers (acceptance #5).

### `HCore.Main/`

- **`Internal/ModuleHost.cs`** (edit — the bulk):
  - `RunningInstance` gains `string? ParentName`.
  - New `Dictionary<Type, LoadedModuleDescriptor> _byImplType` built in ctor from `_descriptors` (dup impl type → throw at startup).
  - `Create(loaded, instanceName, parentName)`: after `AttachVfs`, inject the facade — `instance.AttachHost(new ScopedModuleHost(this, instanceName))` — and `instance.AttachInstanceName(instanceName)`. Top-level `Spawn` passes `parentName: null`.
  - `Spawn<T>`: reject `instanceName` containing `/` (now structural — nesting only via `SpawnChild`).
  - `SpawnChildCore<T>(ownerName, descriptor, leaf, Action<BaseImplement>? init)`: reject `/` in leaf; compose `childName = $"{ownerName}/{leaf}"`; verify owner is running (orphan guard) and child name free (collision guard); **construct + `AttachX` while NOT publishing; run `init` outside the lock; re-acquire lock, re-check name free, then publish to `_instances`**; return `Cast<T>`.
  - Internal `SpawnChildByType<TImpl>(owner, leaf, init)` (resolve via `_byImplType[typeof(TImpl)]`) and `SpawnChildByName<T>(owner, moduleName, leaf, init)` (via existing `FindDescriptor`) → both call `SpawnChildCore`.
  - `Kill(instancePath)` (public, privileged): `InstanceNameFromPath` → `KillLocked(name)` under the lock. `KillChildCore(owner, leaf)`: verify resolved child's `ParentName == owner` (else throw — acceptance #3), then `KillLocked`.
  - `KillLocked(name)` (assumes lock held): collect subtree by recursively matching `ParentName`; remove all; call `OnKilled()` leaf-first. (Single lock session; `Kill`/`KillChildCore` acquire once then call this — no double-lock.)
  - `GetRunningModules()`: snapshot instances under the lock, then **outside the lock** map to `RunningModuleInfo` (add a `string? Details` field = `instance.DescribeForProc()`, so calling module code never runs under `_instancesLock`).
- **`Internal/ScopedModuleHost.cs`** (NEW): `internal sealed class ScopedModuleHost : IModuleHost` holding `(ModuleHost kernel, string owner)`. `GetModuleInterface`/`Spawn`/`Kill` pass straight through to the kernel; `SpawnChild`/`SpawnChildByName`/`KillChild` forward with `owner` bound.
- **`Vfs/ProcFileSystem.cs`** (edit): replace the flat loop with a split-and-nest builder — sort `GetRunningModules()` by `InstanceName` ascending (parents before children), then for each split `InstanceName` on `/`, walk/create intermediate `ReadOnlyVirtualDirectory` nodes (reuse existing via `TryGetDirectory`), and attach the `info` file (generic lines + `Details`) at the node for the full key. Intermediate-only segments simply get no `info`.
- **`Program.cs`**: no change required (cascade fires on `Kill`).

### `HCore.Packages.HInit/Init/InitImplement.cs` (edit)
Add a `kill` case → `Host.Kill(args[1])` (+ confirmation line) and a `PrintHelp` line: `kill <instance>   Kill an instance and its children`. `spawn` stays create-only; `run` unchanged. Demo path: `spawn HCore.Packages.Usb.Usb usb` then `run /proc/usb`.

### Demo pack: `HCore.Packages.Usb/` (NEW — mirror `HCore.Packages.TestDemo` exactly)
- `Usb/IUsb.cs` — `public interface IUsb : IModule { }` (package-local; parent is only invoked as `IRunnable`).
- `Usb/UsbModuleImplement.cs` — `sealed class UsbModuleImplement : ContainerImplement, IUsb, IRunnable`; `Run()` calls `SpawnChild<UsbDeviceImplement>("device0", d => d.Init("SN-A","1-1.2"))` and `("device1", …"SN-B","1-1.3")`. No teardown.
- `Usb/ModDescriptor.cs` — Name `"HCore.Packages.Usb.Usb"`, impl `UsbModuleImplement`, interface `IUsb`.
- `UsbDevice/UsbDeviceImplement.cs` — `sealed class UsbDeviceImplement : BaseImplement, IUsbDevice`; `Serial`/`Location` set by `internal void Init(...)`; `Read` stub; **override `DescribeForProc()` → `$"serial:     {Serial}\nlocation:   {Location}"`** (the user's /proc-info choice).
- `UsbDevice/ModDescriptor.cs` — Name `"HCore.Packages.Usb.UsbDevice"`, impl `UsbDeviceImplement`, interface `IUsbDevice`.
- `HCore.Packages.Usb.csproj` — copy TestDemo's verbatim (net10.0, ref `HCore.Modules.Base`, PostBuild `cp` into `../FS/packs/$(AssemblyName)/`).
- `FS/packs/HCore.Packages.Usb/mpd` (NEW, hand-authored — post-build does NOT create it): two lines `HCore.Packages.Usb.dll` / `HCore.Packages.Usb.pdb`.
- `hcore.sln` — add the `HCore.Packages.Usb` project (solution membership only; `HCore.Main` must NOT reference it — packages load dynamically via ALC).

## Reuse — do NOT change
- `Vfs/FileSystem.cs` — `Resolve`/`FindDirectory` already walk segment-by-segment; nested `/proc/usb/device0/info` resolves for free.
- `ReadOnlyVirtualDirectory`/`ReadOnlyVirtualFile` (`Vfs/DeviceFileSystem.cs`) — nest to arbitrary depth; reuse verbatim.
- `ModuleHost.InstanceNameFromPath` + `GetModuleInterface<T>` lookup — already accept multi-segment composite keys (`"usb/device0"`); calling a nested child already works.
- `ModuleFileSystemProxy`, `ModPackAssemblyLoadContext`, descriptor loading in `Program.cs`.

## Concurrency & edge cases
- All `_instances` mutation/reads take `_instancesLock`; `KillLocked` runs inside a single held lock (no re-lock).
- **`init` must run OUTSIDE the lock, before publish**: construct + attach (no registry mutation), release lock, run `init`, re-acquire, re-check name free, publish. This allows a child's `init` to spawn grandchildren without deadlocking the non-reentrant lock, and still guarantees init-before-publish (a losing race on the same child name throws on the re-check).
- `DescribeForProc()` is called outside the lock (module code never runs under `_instancesLock`).
- Killing a non-leaf reaps the whole subtree; `OnKilled()` fires leaf-first. Slash is now kernel-owned: rejected in top-level `Spawn` names and in child leaf names. A holder of a killed child's interface keeps the object alive (GC) but it's gone from `/proc` and re-lookup throws (documented).

## Verification (end-to-end)
1. **Build** (Base first so contracts propagate): `dotnet build HCore.Modules.Base/HCore.Modules.Base.csproj` then `dotnet build hcore.sln`. Confirm `FS/packs/HCore.Packages.Usb/` has the `.dll`/`.pdb` and the hand-authored `mpd`.
2. **Interactive** (real TTY — piped stdin crashes ReadLine): `dotnet run --project HCore.Main`, then:
   - `spawn HCore.Packages.Usb.Usb usb` → `run /proc/usb`
   - `ls /proc` → `init/  usb/`; `ls /proc/usb` → `info  device0/  device1/`
   - `cat /proc/usb/device0/info` → shows `instance: usb/device0`, `module: …UsbDevice`, **`serial: SN-A`, `location: 1-1.2`**
   - `kill /proc/usb` → `ls /proc` → `init/` only; `ls /proc/usb` → not found
3. **Headless proof** (no TTY): temporarily, after `_vfs.Mount("/proc", …)` in `Program.cs`, spawn+run usb, print `GetRunningModules()` keys (expect `init, usb, usb/device0, usb/device1`), read `GetModuleInterface<IUsbDevice>("/proc/usb/device0").Serial` (expect `SN-A` — proves cross-package call + init-before-publish), `Kill("usb")`, reprint keys (expect `init` only — proves cascade), then `return;`. **Revert this scaffolding after.**
4. **Acceptance map:** cascade → kill leaves only `init`; init-before-publish → Serial set on first observation; ownership → a non-owner `KillChild("device0")` throws; single store → `/proc` + `GetModuleInterface` both read `_instances`; cross-package → `IUsbDevice.Serial` callable from Main; one-verb → `Run()` is two `SpawnChild` calls.

## Critical files
- `HCore.Main/Internal/ModuleHost.cs` — edge, Type index, `SpawnChildCore`, `Kill`/`KillLocked`/`KillChildCore`, facade wiring, `Details` in `GetRunningModules`.
- `HCore.Modules.Base/BaseImplement.cs` — `InstanceName`, `OnKilled`, `DescribeForProc`.
- `HCore.Modules.Base/IModuleHost.cs` — child + kill surface.
- `HCore.Main/Internal/ScopedModuleHost.cs` (NEW) — per-module owner-bound facade.
- `HCore.Main/Vfs/ProcFileSystem.cs` — nested split render + `Details`.
- `HCore.Packages.HInit/Init/InitImplement.cs` — `kill` command.
- NEW: `HCore.Modules.Base/ContainerImplement.cs`, `HCore.Modules.Base/IUsbDevice.cs`, and the `HCore.Packages.Usb/*` pack (+ `FS/packs/HCore.Packages.Usb/mpd`, `hcore.sln` entry).
