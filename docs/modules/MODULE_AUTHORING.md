# Module Authoring Guide

This guide walks you through creating a new HCore package as a **standalone repo** — the recommended pattern. See [DISTRIBUTION.md](../packages/DISTRIBUTION.md) for the workspace layout.

## Overview

A package repo lives **alongside** the kernel repo and references it by a peer relative path:

```
ardumine/
  hcore/       ← kernel (cloned)
  mypackage/   ← your package (created here)
```

Each package needs:

1. A **.NET 10 class library** referencing `HCore.Modules.Base` (and optionally `HCore.Modules.Robotics`)
2. One or more **module triples** (interface + implement + descriptor)
3. An **`mpd`** file in your package directory
4. A **`manifest.json`** for package metadata
5. A **PostBuild** step to deploy to `hcore/FS/packs/`

## Quick Start

### 1. Create the Repo

```bash
git init mypackage
cd mypackage
dotnet new classlib -n HCore.Packages.MyPackage --framework net10.0
```

### 2. Set up the .csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <AssemblyName>HCore.Packages.MyPackage</AssemblyName>
        <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\hcore\src\HCore.Modules.Base\HCore.Modules.Base.csproj" />
        <!-- If your types cross ALC boundaries, also reference Robotics: -->
        <!-- <ProjectReference Include="..\hcore\src\HCore.Modules.Robotics\HCore.Modules.Robotics.csproj" /> -->
    </ItemGroup>

    <ItemGroup>
        <None Update="mpd" CopyToOutputDirectory="PreserveNewest" />
        <None Update="manifest.json" CopyToOutputDirectory="PreserveNewest" />
    </ItemGroup>

    <Target Name="PostBuild" AfterTargets="PostBuildEvent">
        <Exec Command="mkdir -p $(ProjectDir)/../hcore/FS/packs/$(AssemblyName)/" />
        <Exec Command="cp -vr $(OutDir)/* $(ProjectDir)/../hcore/FS/packs/$(AssemblyName)/" />
        <!-- Deploy etc/ overlay if you have service definitions -->
        <Exec Command="cp -vr $(ProjectDir)/etc $(ProjectDir)/../hcore/FS/" ContinueOnError="true" />
    </Target>

</Project>
```

Key points:
- `AssemblyName` must be the package identity (matches `manifest.json` `name`)
- `CopyLocalLockFileAssemblies` ensures NuGet dependencies are copied
- The `ProjectReference` path uses `..\hcore\src\` (peer repo layout)
- `PostBuild` deploys to `../hcore/FS/packs/<AssemblyName>/`

### 3. Define the Module Interface

```csharp
using HCore.Modules.Base;

namespace HCore.Packages.MyPackage;

public interface IMyModule : IModule
{
    double Add(double a, double b);
}
```

For interfaces that will be called **across packages**, place them in `HCore.Modules.Base` or `HCore.Modules.Robotics`. See [Shared Types](#shared-types-cross-alc) below.

### 4. Create the Implementation

```csharp
using HCore.Modules.Base;

namespace HCore.Packages.MyPackage;

public class MyModuleImplement : BaseImplement, IMyModule
{
    public double Add(double a, double b) => a + b;
}
```

Extend `ContainerImplement` instead of `BaseImplement` if your module spawns child instances.

### 5. Create the Descriptor

```csharp
using HCore.Modules.Base;

namespace HCore.Packages.MyPackage;

public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Packages.MyPackage.MyModule";
    public string FriendlyName => "My Module";
    public Type ImplementType => typeof(MyModuleImplement);
    public Type InterfaceType => typeof(IMyModule);
}
```

- `Name` — Unique identifier. Use a reverse-DNS convention.
- `ImplementType` — The class with the logic.
- `InterfaceType` — The module's public API.

### 6. Create `manifest.json`

```json
{
  "name": "HCore.Packages.MyPackage",
  "version": "1.0.0",
  "description": "My custom HCore package"
}
```

If your package provides a one-shot shell command, add a `commands` array:
```json
{
  "commands": [
    {
      "name": "mytool",
      "description": "My one-shot tool",
      "mode": "oneshot",
      "moduleName": "HCore.Packages.MyPackage.Mod"
    }
  ]
}
```

### 7. Create the `mpd` File

```
HCore.Packages.MyPackage.dll
HCore.Packages.MyPackage.pdb
```

Line 1: DLL filename. Line 2: optional PDB filename.

### 8. Service Definition (optional)

If your package should auto-start, create `etc/services/mypkg.svc`:

```
spawn HCore.Packages.MyPackage.MyModule mypkg
run mypkg
```

The PostBuild step copies `etc/` to the kernel's `FS/etc/`. At boot, init reads `/etc/services/*.svc` and spawns each service.

### 9. Build and Run

```bash
cd mypackage
dotnet build                          # builds + deploys to hcore/FS/packs/

cd ../hcore
dotnet run --project src/HCore.Main   # starts kernel, discovers your package
```

---

## Shared Types (Cross-ALC)

For a type to be used across package boundaries, it must live in an assembly loaded in the `Default` ALC context. Two options:

| Assembly | For |
|----------|-----|
| `HCore.Modules.Base` | Kernel contracts: `IModule`, `IRunnable`, `ICommand`, `IModuleHost`, `IShell`, ... |
| `HCore.Modules.Robotics` | Domain contracts: `ILidar`, and future: `ISlam`, `IUsbDevice`, `ScanFrame`, ... |

Example: `ILidar` lives in `Robotics` so both Sensor (producer) and Nexus (consumer) share the same type identity:

```xml
<!-- In your package's .csproj -->
<ProjectReference Include="..\hcore\src\HCore.Modules.Robotics\HCore.Modules.Robotics.csproj" />
```

```csharp
// Consumer (any package)
var lidar = Host.GetModuleInterface<ILidar>("/proc/lidar");
lidar.SetFrameRate(50);
```

---

## Data Plane

See [DATA_PLANE.md](../data-plane/DATA_PLANE.md) for the full guide. Quick summary:

```csharp
// Producer
var facet = Data.ExposeData<ScanFrame>("scan_data", FacetKind.Stream, formatter: FormatFrame);
facet.Publish(frame);

// Consumer
var sub = Data.Subscribe<ScanFrame>("/proc/lidar/scan_data", handler, onDisconnected);
```

---

## Shell Commands

### One-shot (manifest-declared)

Implement `IOneshotCommand` (from Base) and declare in `manifest.json`:

```csharp
public sealed class MyTool : BaseImplement, IOneshotCommand
{
    private string[] _args = [];
    public void SetArguments(string[] args) => _args = args;
    public void Run() { Console.WriteLine($"Args: {string.Join(' ', _args)}"); }
}
```

### Persistent (runtime-registered)

A running module registers commands dynamically:

```csharp
var shell = Host.GetModuleInterface<IShell>("init/console");
shell.RegisterCommand(new MyCommand());
```

---

## Checklist

- [ ] Project targets `net10.0`, `AssemblyName` matches package identity
- [ ] References `..\hcore\src\HCore.Modules.Base\...` (peer relative path)
- [ ] `CopyLocalLockFileAssemblies` is `true`
- [ ] PostBuild deploys to `../hcore/FS/packs/$(AssemblyName)/`
- [ ] Interface extends `IModule` (or `IRunnable`)
- [ ] Implement extends `BaseImplement` (or `ContainerImplement`)
- [ ] Descriptor implements `IModuleDescriptor`
- [ ] `mpd` file with DLL name
- [ ] `manifest.json` with `name` + `version`
- [ ] `etc/services/*.svc` if the package is a service
- [ ] Cross-package interfaces in Base or Robotics
