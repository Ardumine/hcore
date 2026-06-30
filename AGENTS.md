# AGENTS.md

## Build & Run

```bash
dotnet build hcore.sln        # Builds all projects; post-build copies packages to FS/packs/
dotnet run --project HCore.Main   # Launches the kernel + init shell
```

No test framework, no linter, no CI configured.

## Architecture (non-obvious)

- **HCore.Main** is the kernel. It dynamically loads packages at runtime — it must NEVER have a `<ProjectReference>` to any `HCore.Packages.*` project.
- **HCore.Packages.\*** are plugins. They reference ONLY `HCore.Modules.Base`, never `HCore.Main` or each other.
- **HCore.Modules.Base** is the shared contract. Types from this assembly must share identity across all load contexts — the custom `ModPackAssemblyLoadContext` falls back to `Default` context for this. Inter-module call interfaces (e.g. `IModule1`) only work cross-module because of this.
- Packages are loaded from `FS/packs/<Name>/` at runtime via `AssemblyLoadContext.LoadFromStream`.
- **User space / kernel space:** the kernel reaches modules and modules reach the kernel ONLY through interfaces declared in `HCore.Modules.Base` and injected into each `BaseImplement` — `Vfs` (`IModuleFileSystem`, files) and `Host` (`IModuleHost`, modules). These injected handles are the "system-call" surface; modules have no other access to the kernel.
- **`ModuleHost`** (`HCore.Main/Internal`) is the process table + call broker. It has exactly two `IModuleHost` calls: `Spawn<T>(module, instance)` = create a new named instance (does NOT run it; only operation that resolves the concrete impl type); `GetModuleInterface<T>(instancePath)` = look up an already-running instance by its `/proc` path (or bare name) and never create anything. Instances are keyed by instance name and never removed (no process lifecycle yet).
- **`/proc`** (`ProcFileSystem`) is a synthetic, read-only VFS view of running instances, rebuilt on every access from `ModuleHost`. `/packs` = installed, `/proc` = running.

## Key gotchas

- The VFS host root path is **hardcoded** in `HCore.Main/Program.cs` (`_vfs.Mount("/", new HostFileSystem("/home/ardumine/hort/hcore/FS"))` in `Init()`). This must match the developer's machine.
- The init module name is **hardcoded** as `"HCore.Packages.HInit.Init"` in `Program.Main()` (the kernel `host.Spawn<IRunnable>("HCore.Packages.HInit.Init", "init")` then `.Run()`s it, so init appears at `/proc/init`).
- Each package directory needs a manually-created `mpd` file (plain text: DLL name on line 1, optional PDB on line 2).
- `CopyLocalLockFileAssemblies` must be `true` in package `.csproj` files so NuGet deps land next to the DLL.
- The `FS/packs/` directory contains **compiled output** (DLLs, PDBs) deployed by post-build steps — these are runtime artifacts on disk, not source.

## Creating a new package

1. `dotnet new classlib -n HCore.Packages.Foo --framework net10.0`
2. Add to `hcore.sln`
3. Reference only `HCore.Modules.Base`
4. Add PostBuild target to copy output to `FS/packs/$(AssemblyName)/`
5. Create the module triple: `IFoo : IModule`, `FooImplement : BaseImplement, IFoo`, `ModDescriptor : IModuleDescriptor`
6. Create `FS/packs/HCore.Packages.Foo/mpd` with the DLL filename

## Target framework

All projects target **net10.0** (.NET 10 Preview). Requires the .NET 10 SDK.
