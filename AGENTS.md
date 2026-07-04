# AGENTS.md

## Build & Run

```bash
dotnet build hcore.sln        # Builds kernel + in-tree projects
dotnet run --project HCore.Main   # Launches kernel (FS root defaults to ./FS/)

# Override FS root:
dotnet run --project HCore.Main -- --fs=/path/to/FS
HCORE_FS_ROOT=/path/to/FS dotnet run --project HCore.Main
```

No test framework, no linter, no CI configured.

## Project Structure

```
hcore/
  HCore.Main/              ← kernel (must NEVER reference packages)
  HCore.Modules.Base/      ← shared kernel contracts (git submodule → ardumine/hcore-modules-base)
  HCore.Packages.TestDemo/ ← inter-module call demo
  AFCP/                    ← temp in-tree protocol library
  FS/                      ← runtime FS root (generated, not source; gitignored)
```

The kernel repo is **flat** (no `src/`): `HCore.Main/` and the `HCore.Modules.Base/`
submodule sit at the root, mirroring the flat package-repo layout.

Packages live in **separate repos** cloned alongside:
```
ardumine/
  hcore/                ← this repo
  hcore-modules-base/   ← HCore.Modules.Base (the ABI; git submodule of hcore + every package)
  hrobotics/            ← HCore.Modules.Robotics (shared domain contracts: ILidar, ...)
  hinit/         ← HCore.Packages.HInit
  hshell/        ← HCore.Packages.HShell
  hpm/           ← HCore.Packages.Hpm
  hsensors/      ← HCore.Packages.Sensor (uses hrobotics)
  husb/          ← HCore.Packages.Usb
  nexus/         ← HCore.Packages.Nexus (AFCP connector; uses hrobotics)
  kaserializer/  ← KASerializer (referenced by nexus)
```

`HCore.Modules.Base` (the ABI) is its **own repo**, consumed as a **git submodule** at
`HCore.Modules.Base/` by the kernel and by every package repo, pinned to a released tag
(e.g. `v1.0.0`). Clone with `--recurse-submodules`. Because the submodule sits under the
project dir, each package `.csproj` must `<Compile Remove>` its sources so they resolve
from the referenced assembly, not the local glob. `HCore.Modules.Robotics` is now **its
own repo** (`ardumine/hrobotics`, which bundles Base) and is **no longer referenced by the
kernel**. Packages that use it (Sensor, Nexus) consume it as a submodule at
`HCore.Modules.Robotics/` and reference Base *through* it (one Base in the build graph).
Cross-module type identity for these contracts is preserved by the kernel's
**shared-contract loader** (see below), not by a kernel reference.
PostBuild deploys to `../hcore/FS/packs/` (generated, never committed).

## Architecture (non-obvious)

- **HCore.Main** is the kernel. It dynamically loads packages at runtime — it must NEVER have a `<ProjectReference>` to any `HCore.Packages.*` project.
- **HCore.Packages.\*** are plugins. They reference ONLY `HCore.Modules.Base` (and optionally `HCore.Modules.Robotics` via the `hrobotics` submodule), never `HCore.Main` or each other.
- **HCore.Modules.Base** is the shared contract. Types from this assembly must share identity across all load contexts — the custom `ModPackAssemblyLoadContext` falls back to `Default` context for it (the kernel references Base, so it is loaded into `Default` at startup).
- **Shared-contract loader.** Any assembly named `HCore.Modules.*` that a package needs is treated as a shared contract: `ModPackAssemblyLoadContext` **promotes it into the `Default` context on first load**, so every package sees one identity (e.g. `ILidar` produced by Sensor and consumed by Nexus). The kernel no longer references `HCore.Modules.Robotics`; it rides this generic path.
- Packages are loaded from `FS/packs/<Name>/` at runtime via `AssemblyLoadContext.LoadFromStream`.
- **User space / kernel space:** the kernel reaches modules and modules reach the kernel ONLY through interfaces declared in `HCore.Modules.Base` and injected into each `BaseImplement` — `Vfs` (`IModuleFileSystem`), `Host` (`IModuleHost`), `Data` (`IDataHost`). These injected handles are the "system-call" surface.
- **Driver-door interfaces** (`IKernelVfs`, `IFacetView`, `IModuleResolver`) are privileged contract surfaces that a driver module (e.g. Nexus) receives. `IRemoteMountHook` lets a driver module transparently redirect subscribe/call to a remote peer.
- **`ModuleHost`** (`HCore.Main/Internal`) is the process table + call broker. `Spawn<T>(module, instance)` creates a named instance (does NOT run it). `GetModuleInterface<T>(instancePath)` looks up a running instance. `Kill(instancePath)` does a privileged cascade kill.
- **Module hierarchy.** `ContainerImplement.SpawnChild<TImpl>(leaf, init)` creates child instances nested at `/proc/<parent>/<child>`. Killing the parent reaps every descendant. Full design: `docs/modules/MODULE_HIERARCHY.md`.
- **`/proc`** is a synthetic, read-only VFS view of running instances. `/packs` = installed, `/proc` = running.
- **Init / shell split.** `HCore.Packages.HInit` is PID 1, implements `IServiceManager`. Boots `/etc/services/*.svc`, then spawns the interactive console shell and blocks on it.
- **Bootstrap.** On first boot with empty `FS/packs/`, the kernel runs the embedded bootstrap module (detects missing essential packages, creates FS skeleton, fetches/copies packages, re-scans). On subsequent boots, bootstrap is skipped.
- **Kernel-service registry.** `ModuleHost.GetModuleInterface<T>` accepts a reserved `@`-prefixed name (e.g. `"@afcp"`) resolving against a separate kernel-service registry, NOT the `/proc` instance table.
- **AFCP remote data plane.** The `AFCP/` project is a standalone protocol library. Layer 1 (VFS mount), Layer 2 (subscribe-push), and Layer 3 (MKCall proxy) are implemented, plus typed wire errors (C7d) and chunked large-file streaming (C7e). Nexus is the connector module that implements `IAfcpKernel` + `IDriverModule` + `IRemoteMountHook`; it now lives in its **own repo** (`ardumine/nexus`, cloned alongside as `../nexus`, consuming the `hrobotics` submodule for Base+Robotics and `../kaserializer`) and deploys to `FS/packs/HCore.Packages.Nexus/`. The AFCP protocol/transport is bundled inside that repo (the upstream-lib swap, E2.1, is still pending).

## Key gotchas

- The VFS host root path defaults to `./FS/` relative to the executable. Override via `--fs=<path>` or `HCORE_FS_ROOT` env var.
- The init module name is **hardcoded** as `"HCore.Packages.HInit.Init"` in `Program.Main()`.
- Each package directory needs a manually-created `mpd` file (plain text: DLL name on line 1, optional PDB on line 2).
- `CopyLocalLockFileAssemblies` must be `true` in package `.csproj` files so NuGet deps land next to the DLL.
- `BaseImplement.OnKilled()`/`DescribeForProc()` are `protected internal`. Override in a different assembly with just `protected override`, not `protected internal override`.
- **Kernel-service registry.** Use `@`-prefixed names for kernel-space singletons (e.g. `"@afcp"`, `"@vfs"`). These never collide with `/proc` instance names.
- **AFCP serializer:** `Nullable<T>` value types are NOT supported. Class deserialization requires a parameterless constructor. List/Dictionary emitters reach into private fields — verified on net10.0 but fragile.

## Distribution

See [docs/packages/DISTRIBUTION.md](docs/packages/DISTRIBUTION.md) for the full bootstrap and release workflow.
See [docs/modules/MODULE_AUTHORING.md](docs/modules/MODULE_AUTHORING.md) for creating a new package as a standalone repo.

## Target framework

All projects target **net10.0** (.NET 10 Preview). Requires the .NET 10 SDK.
