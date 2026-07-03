# HCore

A modular microkernel runtime for C#. HCore makes building modular code and micro-services easy by providing dynamic module loading, assembly isolation, and a virtual file system — all without requiring module authors to deal with low-level complexity.

## Features

- **Dynamic Module Loading** — Packages are discovered and loaded at runtime from compiled DLLs, no compile-time coupling between the kernel and modules
- **Assembly Isolation** — Each package runs in its own `AssemblyLoadContext`, preventing dependency conflicts between modules
- **Virtual File System (VFS)** — A union-mount filesystem that layers host directories, in-memory storage, and synthetic devices under a single path hierarchy
- **Sandboxed Filesystem Access** — Each module receives a proxy to the VFS with its own working directory
- **Inter-Module Calls** — A module obtains a typed reference to an already-running instance through the kernel's module host (`Host.GetModuleInterface<T>(instancePath)`)
- **Data Plane** — A module exposes live data as a facet at `/proc/<m>/<facet>` (`Data.ExposeData<T>`); consumers take a snapshot or subscribe to a push stream
- **Modules as Processes** — `Host.Spawn<T>(module, instance)` creates the same module many times as independent named instances
- **Module Hierarchy** — `ContainerImplement.SpawnChild<T>(name, init)` lets a module own child instances; killing the parent reaps the whole subtree
- **Kernel / User-Space Boundary** — Modules reach the kernel only through injected interfaces (`Vfs`, `Host`, `Data`); no ambient access to kernel internals
- **Remote Data Plane (AFCP)** — Two HCore instances expose/consume each other's filesystems and modules over TCP (9P-style mount, subscribe-push, and MKCall proxy)

## Project Structure

This repo (`hcore/`) contains the kernel and shared contracts. Packages live in separate repos cloned alongside:

| Repo | Package | Role |
|------|---------|------|
| `hcore` | — | Kernel + Base + Robotics + Nexus (in-tree) |
| `hinit` | HCore.Packages.HInit | Init module (PID 1) — service manager, boots shell |
| `hshell` | HCore.Packages.HShell | Interactive shell + REPL |
| `hpm` | HCore.Packages.Hpm | Package manager (install, pack, rebuild) |
| `hsensors` | HCore.Packages.Sensor | LIDAR + SLAM demo |
| `husb` | HCore.Packages.Usb | USB controller demo |

### hcore/ layout

```
hcore/
  src/
    HCore.Main/              ← kernel
    HCore.Modules.Base/      ← shared kernel contracts
    HCore.Modules.Robotics/  ← shared domain contracts
  HCore.Packages.Nexus/      ← AFCP connector module (in-tree scaffolding)
  HCore.Packages.TestDemo/   ← inter-module call demo
  AFCP/                      ← AFCP protocol library (temp)
  FS/                        ← runtime filesystem root
  docs/
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build & Run

```bash
# Clone kernel + essential packages
git clone https://github.com/Ardumine/hcore.git
git clone https://github.com/Ardumine/hinit.git
git clone https://github.com/Ardumine/hshell.git
git clone https://github.com/Ardumine/hpm.git

# Build a package (PostBuild copies to hcore/FS/packs/)
cd hpm && dotnet build

# Run the kernel
cd ../hcore
dotnet run --project src/HCore.Main

# Custom FS root
dotnet run --project src/HCore.Main -- --fs=/path/to/FS
HCORE_FS_ROOT=/path/to/FS dotnet run --project src/HCore.Main
```

Packages are discovered automatically from `FS/packs/` at boot. On first boot without packages, the bootstrap module fetches the essentials.

## Core Concepts

### Packages & Modules

A **package** is a distributable unit — a .NET assembly in `FS/packs/<Name>/`. A **module** is the fundamental unit of functionality, defined by three parts:
1. **Interface** — Public API (extends `IModule` or `IRunnable`)
2. **Implement** — Logic (extends `BaseImplement` + implements the interface)
3. **Descriptor** — Metadata for the kernel (`IModuleDescriptor`)

### Virtual File System

```
/            — Host filesystem (maps to FS/ on disk)
/dev         — Synthetic device filesystem
/tmp         — In-memory tmpfs
/proc        — Live view of running module instances
/packs       — Installed packages
```

### Inter-Module Communication

```csharp
// Look up a running instance
var module1 = Host.GetModuleInterface<IModule1>("/proc/module1");
module1.Func1();

// Spawn a new instance
Host.Spawn<IRunnable>("HCore.Packages.Foo.Bar", "worker").Run();
```

Remote calls are transparent: if the path resolves to a remote AFCP mount, a marshalling proxy is returned instead of the local instance.

### Distribution

Packages are distributed as `.hpk` files (gzip-compressed tar). The `hpm` package manager handles install, remove, pack, and rebuild:

```bash
hpm pack /path/to/repo              # build + create .hpk
hpm pack --with-source /path/to/repo # include source code
hpm install mypackage-1.0.0.hpk      # extract + resolve deps + run scripts
hpm rebuild mypackage                # recompile from installed source
hpm remove mypackage                 # run prerm script + delete
```

### Shared Types

Interfaces called across package boundaries must live in `HCore.Modules.Base` or `HCore.Modules.Robotics`. Both are loaded in the default ALC context, giving them shared type identity across all packages.

## Documentation

- [Architecture](docs/architecture/ARCHITECTURE.md) — Boot sequence, VFS internals, assembly loading, module host
- [Design & Rationale](docs/architecture/DESIGN.md) — Why HCore is shaped this way
- [Data Plane Guide](docs/data-plane/DATA_PLANE.md) — Expose, stream, and consume live data facets
- [Module Authoring Guide](docs/modules/MODULE_AUTHORING.md) — Create your own packages
- [Package System](docs/packages/PACKAGE_SYSTEM.md) — .hpk format, hpm commands, shell extension
- [Distribution](docs/packages/DISTRIBUTION.md) — Bootstrap, workspace layout, release pipeline
- [Shell Guide](docs/shell/SHELL.md) — Shell commands and service model
- [API Reference](docs/architecture/API_REFERENCE.md) — Interface and class documentation

## License

TBD
