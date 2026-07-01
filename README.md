# HCore

A modular microkernel runtime for C#. HCore makes building modular code and micro-services easy by providing dynamic module loading, assembly isolation, and a virtual file system — all without requiring module authors to deal with low-level complexity.

## Features

- **Dynamic Module Loading** — Packages are discovered and loaded at runtime from compiled DLLs, no compile-time coupling between the kernel and modules
- **Assembly Isolation** — Each package runs in its own `AssemblyLoadContext`, preventing dependency conflicts between modules
- **Virtual File System (VFS)** — A union-mount filesystem that layers host directories, in-memory storage, and synthetic devices under a single path hierarchy
- **Sandboxed Filesystem Access** — Each module receives a proxy to the VFS with its own working directory
- **Inter-Module Calls** — A module obtains a typed reference to an already-running instance through the kernel's module host (`Host.GetModuleInterface<T>(instancePath)`), with no compile-time coupling between the modules
- **Modules as Processes** — `Host.Spawn<T>(module, instance)` creates the same module many times as independent named instances, each visible under `/proc`
- **Module Hierarchy** — `ContainerImplement.SpawnChild<T>(name, init)` lets a module own real, stateful child instances nested at `/proc/<parent>/<child>`; killing the parent (`kill <instance>` in the shell) structurally reaps the whole subtree
- **Kernel / User-Space Boundary** — Modules reach the kernel only through injected "system-call" interfaces (`Vfs`, `Host`); they have no ambient access to kernel internals
- **Async Signaling** — `AdamPipe<T>` provides thread-safe producer-consumer messaging between modules

## Project Structure

| Project | Type | Role |
|---------|------|------|
| `HCore.Main` | Executable | The kernel — boots the system, manages VFS, loads packages |
| `HCore.Modules.Base` | Class Library | Shared contracts (interfaces, base classes) used by all modules |
| `Logyt` | Class Library | Structured logging with console color support |
| `HCore.Packages.HInit` | Class Library (Package) | Init module (PID 1) — service manager; boots `/etc/services` and launches the shell |
| `HCore.Packages.HShell` | Class Library (Package) | The interactive shell (`IShell`), with an `ICommand`+registry dispatch |
| `HCore.Packages.TestDemo` | Class Library (Package) | Demo package with two example modules |
| `HCore.Packages.Usb` | Class Library (Package) | Demo package for the module hierarchy: a USB controller owning two device children |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build & Run

```bash
# Build the entire solution
dotnet build hcore.sln

# Run HCore (starts the kernel; init boots /etc/services then drops to the shell)
dotnet run --project HCore.Main
```

When you build, each package's post-build step automatically copies its output to `FS/packs/<PackageName>/`, making it available for runtime discovery.

## Core Concepts

### Packages (ModPacks)

A package is a distributable unit — a .NET assembly DLL placed in `FS/packs/<Name>/` alongside an `mpd` descriptor file. Packages contain one or more modules.

### Modules

A module is the fundamental unit of functionality. Every module consists of three parts:
1. **Interface** — Defines the module's public API (extends `IModule` or `IRunnable`)
2. **Implement** — Contains the logic (extends `BaseImplement` + implements the interface)
3. **Descriptor** — Metadata that tells the kernel how to instantiate the module (implements `IModuleDescriptor`)

### Virtual File System

The VFS provides a unified filesystem abstraction with mount points:
- `/` — Host filesystem (maps to the `FS/` directory on disk)
- `/dev` — Synthetic device filesystem (read-only)
- `/tmp` — In-memory filesystem (tmpfs)
- `/proc` — Live view of running module instances (read-only)

### Inter-Module Communication & Processes

A module never references another module's assembly. To call another module it asks the **module host** (injected as `Host`):

```csharp
// Look up an ALREADY-RUNNING instance by its /proc path, typed as its interface:
var module1 = Host.GetModuleInterface<IModule1>("/proc/module1");
module1.Func1();

// Create a new named instance (like a process) — visible at /proc/<instance>, not run:
Host.Spawn<IRunnable>("HCore.Packages.TestDemo.Module2", "worker-a").Run();

// A module can own real, stateful CHILDREN — see below, ContainerImplement.
Host.Kill("/proc/worker-a"); // privileged: reaps the instance and any children it owns
```

The **path identifies _who_** you talk to; the **method call is _what_** you say; the **return value is data**. This separation is the heart of the design — see [DESIGN.md](docs/DESIGN.md).

There are three levels: a **pack** (`/packs/<pack>/`, installed code), a **module** (a program defined by a descriptor inside the pack), and an **instance** (a running module, listed in `/proc/<name>/`).

### Module Hierarchy (Sub-Modules)

A module can extend `ContainerImplement` instead of `BaseImplement` to own real child instances, nested under it in `/proc` and reaped automatically when it's killed:

```csharp
public sealed class UsbModuleImplement : ContainerImplement, IUsb, IRunnable
{
    public void Run()
    {
        SpawnChild<UsbDeviceImplement>("device0", d => d.Init("SN-A", "1-1.2"));
        SpawnChild<UsbDeviceImplement>("device1", d => d.Init("SN-B", "1-1.3"));
    }
    // No teardown — killing this module reaps device0/device1 automatically.
}
```

See [MODULE_HIERARCHY.md](docs/MODULE_HIERARCHY.md) for the full design and [MODULE_AUTHORING.md](docs/MODULE_AUTHORING.md) for a walkthrough.

### The `mpd` File

Each package directory contains an `mpd` (Mod Pack Descriptor) file — a simple text file listing the DLL (and optionally PDB) to load:

```
MyPackage.dll
MyPackage.pdb
```

## Documentation

- [Architecture](docs/ARCHITECTURE.md) — Boot sequence, VFS internals, assembly loading, the module host, kernel/user-space & system calls, module hierarchy mechanics
- [Design & Rationale](docs/DESIGN.md) — Why HCore is shaped this way: the core design questions, answered
- [Module Hierarchy](docs/MODULE_HIERARCHY.md) — The sub-module design debate, chosen approach, and implementation notes
- [Module Authoring Guide](docs/MODULE_AUTHORING.md) — How to create your own modules (and call others, and own children)
- [Shell Guide](docs/SHELL.md) — Shell commands and the `/etc/services` service model
- [API Reference](docs/API_REFERENCE.md) — Interface and class documentation

## License

TBD
