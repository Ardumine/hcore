# Module Authoring Guide

This guide walks you through creating a new HCore package with modules from scratch.

## Overview

To create a module for HCore, you need:

1. A **.NET 10 class library** project referencing `HCore.Modules.Base`
2. One or more **module triples** (interface + implement + descriptor)
3. An **`mpd` file** in your package's output directory
4. A **post-build step** to deploy to `FS/packs/`

## Quick Start

### 1. Create the Project

```bash
dotnet new classlib -n HCore.Packages.MyPackage --framework net10.0
dotnet sln hcore.sln add HCore.Packages.MyPackage
```

### 2. Add Reference to HCore.Modules.Base

Edit `HCore.Packages.MyPackage.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\HCore.Modules.Base\HCore.Modules.Base.csproj" />
    </ItemGroup>

    <Target Name="PostBuild" AfterTargets="PostBuildEvent">
        <Exec Command="mkdir -p $(ProjectDir)/../FS/packs/$(AssemblyName)/" />
        <Exec Command="cp -vr $(OutDir)/* $(ProjectDir)/../FS/packs/$(AssemblyName)/" />
    </Target>

</Project>
```

Key points:
- `CopyLocalLockFileAssemblies` ensures NuGet dependencies are copied to the output
- The `PostBuild` target deploys the package to `FS/packs/<AssemblyName>/`

### 3. Define the Module Interface

Create `IMathModule.cs`:

```csharp
using HCore.Modules.Base;

namespace HCore.Packages.MyPackage;

public interface IMathModule : IModule
{
    double Add(double a, double b);
    double Multiply(double a, double b);
}
```

If your module needs an entry point (like a main loop), extend `IRunnable` instead:

```csharp
public interface IMathModule : IRunnable
{
    double Add(double a, double b);
    double Multiply(double a, double b);
}
```

### 4. Create the Implementation

Create `MathModuleImplement.cs`:

```csharp
using HCore.Modules.Base;

namespace HCore.Packages.MyPackage;

public class MathModuleImplement : BaseImplement, IMathModule
{
    public double Add(double a, double b)
    {
        return a + b;
    }

    public double Multiply(double a, double b)
    {
        return a * b;
    }
}
```

Your implementation:
- **Must** extend `BaseImplement` (gives you `Vfs` for files and `Host` for reaching other modules)
- **Must** implement your module interface

### 5. Create the Descriptor

Create `ModDescriptor.cs`:

```csharp
using HCore.Modules.Base;

namespace HCore.Packages.MyPackage;

public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Packages.MyPackage.MathModule";
    public string FriendlyName => "Math Module";
    public Type ImplementType => typeof(MathModuleImplement);
    public Type InterfaceType => typeof(IMathModule);
}
```

- `Name` — Unique identifier used by the kernel to find and instantiate this module
- `FriendlyName` — Human-readable name for display/logging
- `ImplementType` — The class that contains the logic
- `InterfaceType` — The interface that defines the module's public API

> **Pick a unique, stable `Name`.** It is the identity other modules and the shell use to find this module — a reverse-DNS-style name works well (`MyCompany.MyPackage.MathModule`). The bundled demo is inconsistent (`HCore.Modules.TestDemo.Module1` vs `HCore.Packages.TestDemo.Module2`); don't copy it verbatim — choose one convention.

### 6. Create the `mpd` File

Create `FS/packs/HCore.Packages.MyPackage/mpd`:

```
HCore.Packages.MyPackage.dll
HCore.Packages.MyPackage.pdb
```

The second line (PDB) is optional but useful for debugging. The `mpd` file tells the kernel which DLL to load from this package directory.

### 7. Build and Run

```bash
dotnet build hcore.sln
dotnet run --project HCore.Main
```

The post-build step copies your DLL to `FS/packs/HCore.Packages.MyPackage/`. The kernel will discover it on next boot.

## Module Types

### IModule (Passive)

A passive module exposes methods that other modules can call but has no entry point of its own:

```csharp
public interface IMyModule : IModule
{
    void DoSomething();
    string GetResult();
}
```

### IRunnable (Active)

A runnable module has a `Run()` method that the kernel can invoke as an entry point:

```csharp
public interface IMyService : IRunnable
{
    void Configure(string setting);
}
```

The `Run()` method is inherited from `IRunnable` — you implement it in your class:

```csharp
public class MyServiceImplement : BaseImplement, IMyService
{
    public void Configure(string setting) { /* ... */ }

    public void Run()
    {
        // Entry point - start your service logic here
    }
}
```

## Using the Virtual File System

Every module has access to the VFS through the `Vfs` property (inherited from `BaseImplement`). The VFS is automatically injected by the kernel before your module runs.

### Available Operations

```csharp
// Read/write files
string content = Vfs.ReadAllText("/data/config.txt");
Vfs.WriteAllText("/data/output.txt", "Hello, HCore!");
Vfs.WriteAllText("/data/log.txt", "New entry\n", append: true);

// Stream-based I/O
using var stream = Vfs.OpenFileStream("/data/binary.dat", FileMode.Create, FileAccess.Write);

// Directory operations
Vfs.CreateDirectory("/data/mydir");
IEnumerable<string> entries = Vfs.ListDirectory("/data");

// File management
Vfs.TouchFile("/data/newfile.txt");
bool exists = Vfs.Exists("/data/config.txt");
bool isFile = Vfs.FileExists("/data/config.txt");
bool isDir = Vfs.DirectoryExists("/data/mydir");
Vfs.Move("/data/old.txt", "/data/new.txt");
Vfs.Rename("/data/file.txt", "renamed.txt");
Vfs.DeleteFile("/data/temp.txt");
Vfs.DeleteDirectory("/data/mydir", recursive: true);

// Working directory
string cwd = Vfs.WorkingDirectory;
Vfs.SetWorkingDirectory("/data");
string resolved = Vfs.ResolvePath("relative/path.txt");
```

### Path Resolution

- Absolute paths (starting with `/`) are resolved from the VFS root
- Relative paths are resolved against the module's working directory
- The initial working directory is set to the module's pack directory (e.g., `/packs/HCore.Packages.MyPackage`)

## Calling Another Module

A module never references another module's project. Instead it asks the **module host** — the `Host` property, injected by the kernel just like `Vfs` — for a typed reference. As long as the *interface* lives in an assembly both sides share (e.g. `HCore.Modules.Base`, or a common contracts package), the kernel returns the live instance typed as that interface.

### Look up an already-running instance

```csharp
public class Module2Implement : BaseImplement, IModule2
{
    public void Run()
    {
        // The PATH identifies which instance; Func1() is the call; the result is data.
        // Module1 must already be spawned at /proc/module1 — this is a pure lookup,
        // Module2 holds only IModule1 and so could never construct Module1 itself.
        var module1 = Host.GetModuleInterface<IModule1>("/proc/module1");
        module1.Func1();
    }
}
```

`GetModuleInterface<T>` **looks up** an already-running instance by its `/proc` path (a bare name like `"module1"` is also accepted) and never creates anything. It needs only the interface because the object already exists.

### Spawn a new instance (like a process)

```csharp
// Create the same module several times, each with its own identity in /proc.
// Spawn does NOT run the instance — call Run() yourself if it is IRunnable.
var a = Host.Spawn<IRunnable>("HCore.Packages.TestDemo.Module2", "worker-a");
var b = Host.Spawn<IRunnable>("HCore.Packages.TestDemo.Module2", "worker-b");
a.Run();
b.Run();

// Later, look one up again by its /proc path:
var same = Host.GetModuleInterface<IRunnable>("/proc/worker-a");
```

| Call | Returns | Use when |
|------|---------|----------|
| `Spawn<T>(module, instance)` | a new named instance (not run) | you want to create one or more independent copies |
| `GetModuleInterface<T>(instancePath)` | an existing instance | you want to reach an instance that is already running |

> **Where the interface lives matters.** For the cast inside these calls to succeed, the interface type (`IModule1` above) must be the *same* `Type` on both sides. Put shared interfaces in `HCore.Modules.Base` (or another assembly loaded once in the default context); see [ARCHITECTURE.md → Assembly Isolation](ARCHITECTURE.md#assembly-isolation) and [DESIGN.md](DESIGN.md).

## Running and Spawning from the Shell

The HInit shell spawns a module by its descriptor `Name`, then runs an already-spawned instance by its `/proc` path:

```
/ $ spawn HCore.Modules.TestDemo.Module1 module1   # create Module1 (not run)
/ $ spawn HCore.Packages.TestDemo.Module2 m2       # create Module2 (not run)
/ $ run /proc/m2                                   # run the m2 instance
/ $ ls /proc                                       # see what's running
/ $ kill /proc/m2                                  # kill it (and any children it owns)
```

See the [Shell Guide](SHELL.md) for the full command list.

## Owning Child Modules (Sub-Modules)

If your module needs to own **real, stateful children** — e.g. a USB controller owning its plugged-in device ports — extend `ContainerImplement` instead of `BaseImplement`. This is **Design D**; the full debate and spec live in [MODULE_HIERARCHY.md](MODULE_HIERARCHY.md).

```csharp
using HCore.Modules.Base;

namespace HCore.Packages.Usb.UsbDevice;

public sealed class UsbDeviceImplement : BaseImplement, IUsbDevice
{
    public string Serial { get; private set; } = "";
    public string Location { get; private set; } = "";

    internal void Init(string serial, string location)
    {
        Serial = serial;
        Location = location;
    }

    public byte[] Read(int len) => new byte[len];

    // Optional: extra lines shown in this instance's /proc/<name>/info.
    protected override string? DescribeForProc()
        => $"serial:     {Serial}\nlocation:   {Location}";
}
```

```csharp
using HCore.Modules.Base;
using HCore.Packages.Usb.UsbDevice;

namespace HCore.Packages.Usb.Usb;

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

This is the actual `HCore.Packages.Usb` demo pack (mirrors `TestDemo`'s project shape exactly). Running it produces:

```
/ $ spawn HCore.Packages.Usb.Usb usb
/ $ run /proc/usb
/ $ ls /proc/usb
info  device0/  device1/
/ $ cat /proc/usb/device0/info
instance:   usb/device0
module:     HCore.Packages.Usb.UsbDevice
...
serial:     SN-A
location:   1-1.2
/ $ kill /proc/usb          # reaps usb, device0, and device1 together
```

### What `ContainerImplement` gives you

| Member | Description |
|--------|-------------|
| `SpawnChild<TImpl>(name, init)` | Create a child of THIS instance, resolved by concrete implementation type. No `new`, no `Vfs`/`Host` wiring, no name strings. `init` runs before the child is visible in `/proc`, so it's never observed half-built. |
| `SpawnChildByName<T>(moduleName, name, init)` | Cross-package form: create a child by module name, returning its interface — the interface must live in `HCore.Modules.Base` (or another shared contract assembly) for the caller to use it as more than the empty `IModule` marker. |
| `KillChild(name)` | Kill one of THIS instance's own children. Optional — killing the parent module itself already cascades to every descendant. |

**Rules worth knowing:**
- A child's interface (`IUsbDevice` above) must live in `HCore.Modules.Base`, not in your package — otherwise a caller in a different package can only see it as the empty `IModule` marker (different `AssemblyLoadContext` ⇒ different `Type`).
- Ownership is enforced structurally: your module can only `KillChild` its own children. It cannot reach into another module's subtree that way. The shell's `kill` command, by contrast, is privileged and can kill anything by path — see [MODULE_HIERARCHY.md](MODULE_HIERARCHY.md) for why that's a documented, not-yet-closed gap.
- Leaf names (and top-level instance names passed to `Spawn`) can't contain `/` — nesting only happens through `SpawnChild`.

## Multiple Modules Per Package

A single package can contain multiple modules. Each needs its own interface, implement, and descriptor:

```
HCore.Packages.MyPackage/
├── IMathModule.cs
├── MathModuleImplement.cs
├── MathModDescriptor.cs
├── ILoggerModule.cs
├── LoggerModuleImplement.cs
└── LoggerModDescriptor.cs
```

The kernel scans the entire assembly for all `IModuleDescriptor` implementations and registers each one.

## Asynchronous Signaling with AdamPipe

For synchronous calls, use `Host` (see *Calling Another Module*). For **asynchronous** producer-consumer signaling — one module streaming events to another — use `AdamPipe<T>`:

```csharp
// A pipe shared between a producer and a consumer
var pipe = new AdamPipe<string>();

// Producer module
pipe.SendSignal("task-complete");

// Consumer module (blocks until a signal arrives)
string message = pipe.Wait();
```

`AdamPipe<T>` supports cancellation:

```csharp
var cts = new CancellationTokenSource();
try
{
    var message = pipe.Wait(cts.Token);
}
catch (OperationCanceledException)
{
    // Handle cancellation
}
```

## NuGet Dependencies

You can use NuGet packages in your modules. Make sure `CopyLocalLockFileAssemblies` is set to `true` in your `.csproj` so that dependencies are copied alongside your DLL:

```xml
<PropertyGroup>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
</PropertyGroup>

<ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
</ItemGroup>
```

The `ModPackAssemblyLoadContext` will automatically resolve dependencies from your package's directory in the VFS.

## Checklist

- [ ] Project targets `net10.0`
- [ ] References `HCore.Modules.Base`
- [ ] `CopyLocalLockFileAssemblies` is `true`
- [ ] Post-build copies output to `FS/packs/<Name>/`
- [ ] Interface extends `IModule` or `IRunnable`
- [ ] Implement extends `BaseImplement` and implements the interface
- [ ] Descriptor implements `IModuleDescriptor` with correct types
- [ ] `mpd` file exists in `FS/packs/<Name>/` with the DLL filename
