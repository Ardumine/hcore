# AGENTS.md

## Build & Run

```bash
dotnet build hcore.sln        # Builds all projects; post-build copies packages to FS/packs/
dotnet run --project HCore.Main   # Launches the kernel + init (boots /etc/services, then the console shell)
```

No test framework, no linter, no CI configured.

## Architecture (non-obvious)

- **HCore.Main** is the kernel. It dynamically loads packages at runtime — it must NEVER have a `<ProjectReference>` to any `HCore.Packages.*` project.
- **HCore.Packages.\*** are plugins. They reference ONLY `HCore.Modules.Base`, never `HCore.Main` or each other.
- **HCore.Modules.Base** is the shared contract. Types from this assembly must share identity across all load contexts — the custom `ModPackAssemblyLoadContext` falls back to `Default` context for this. Inter-module call interfaces (e.g. `IModule1`) only work cross-module because of this.
- Packages are loaded from `FS/packs/<Name>/` at runtime via `AssemblyLoadContext.LoadFromStream`.
- **User space / kernel space:** the kernel reaches modules and modules reach the kernel ONLY through interfaces declared in `HCore.Modules.Base` and injected into each `BaseImplement` — `Vfs` (`IModuleFileSystem`, files) and `Host` (`IModuleHost`, modules). These injected handles are the "system-call" surface; modules have no other access to the kernel.
- **`ModuleHost`** (`HCore.Main/Internal`) is the process table + call broker. Top-level calls: `Spawn<T>(module, instance)` = create a new named instance (does NOT run it; only operation that resolves the concrete impl type by NAME); `GetModuleInterface<T>(instancePath)` = look up an already-running instance by its `/proc` path (or bare name) and never create anything; `Kill(instancePath)` = privileged cascade kill (unrestricted — no capability model yet, documented gap). Instances are keyed by instance name (composite `"owner/leaf"` for children) and are only removed via `Kill`/`KillChild` — there's still no self-reap on `Run()` completion.
- **Module hierarchy.** A module can own child instances via `ContainerImplement.SpawnChild<TImpl>(leaf, init)` (concrete-type, primary) or `SpawnChildByName<T>(moduleName, leaf, init)` (cross-package escape hatch). The kernel constructs the child, wires it, runs `init` *before* publishing (never half-built), and records a `ParentName` edge on `RunningInstance` so killing the parent structurally reaps every descendant, leaf-first. Every created instance actually receives a `ScopedModuleHost` (bound to its own instance name) as `Host`, not the raw kernel `ModuleHost` — that's what makes a module physically unable to squat another parent's children. `KillChild` is owner-scoped; `Kill` is privileged and works on anything. Full design: `docs/MODULE_HIERARCHY.md`.
- **`/proc`** (`ProcFileSystem`) is a synthetic, read-only VFS view of running instances, nested by parent→child (splitting the composite instance key on `/`), rebuilt on every access from `ModuleHost`. `/packs` = installed, `/proc` = running.
- **Init / shell split.** `HCore.Packages.HInit` is PID 1 (`/proc/init`), a `ContainerImplement` implementing `IServiceManager`. On `Run()` it spawns a worker shell child `/proc/init/svc` (only ever used for `IShell.RunScript`), boots every `/etc/services/*.svc` script, then spawns the interactive console shell `/proc/init/console` and blocks on it. The shell itself is a separate package, `HCore.Packages.HShell` (`IShell`), with an `ICommand`+registry dispatch shared by the REPL and `RunScript`. The shell's `service` command reaches init via `GetModuleInterface<IServiceManager>("init")` (the interface lives in `HCore.Modules.Base` so it crosses ALCs). Services are top-level instances named after their `.svc` file; `service stop` = privileged `Kill(name)` cascade.
- **AFCP remote data plane.** The `AFCP/` project is a **standalone** protocol library (no HCore deps) that lets two HCore instances expose/consume each other's filesystems over TCP — remoteness as a path prefix (9P-style), not V2's `Guid`-keyed peers. Layer 1 (mount + `Sync` + `Read` + `Write`/`MkDir`/`Remove`) and Layer 2 (subscribe-push) are implemented; Layer 3 (MKCall proxy) is deferred. The serve side is a **generic VFS proxy** (`VfsAfcpProvider`) over the kernel `FileSystem` — it serves the entire root (`/proc`, `/etc`, `/dev`, `/packs`, ...), and the live `/proc` facets stay fresh because `ProcFileSystem` rebuilds them on every server-side read (no facet-specific protocol). **Layer 2 (subscribe-push) is the one non-VFS verb** (a facet isn't a file): it's transparent through the ordinary `IDataHost.Subscribe<T>` — `DataHost` holds a `FileSystem` ref and, via `FileSystem.TryResolveMount`, redirects a subscribe on a mounted path to `RemoteFileSystem.SubscribeData<T>` (the internal `IRemoteDataSource`), which wraps an `AfcpClient` subscription in a `RemoteSubscription<T>` (bounded channel + single consumer). The serve side backs it with `IFacet.SubscribeRaw` (non-generic subscribe) + `DataHost.FindFacet`, streaming `EventNotify` frames through `IAfcpSubscriptionSink`; a dropped connection tears down its subscriptions (`PeerSession.DisposeAllSubscriptions`). Demo consumer: `HCore.Packages.Sensor.RemoteSlam`. The **kernel-space bridge** lives in `HCore.Main` (it references AFCP directly — a shortcut; the clean target is an `HCore.Packages.Afcp` package, which needs `IVirtualFileSystem` moved to Base + a proc-view/mount contract, see `docs/AFCP.md`). The bridge (`AfcpKernelService`) is a **kernel-service singleton** registered under the reserved name `@afcp`, NOT a `/proc` module instance. The shell's `afcp` command reaches it via `GetModuleInterface<IAfcpKernel>("@afcp")` — the `IAfcpKernel` contract lives in `HCore.Modules.Base` so the shell package needs no AFCP reference. `afcp test` runs a full loopback self-test in one instance.

## Key gotchas

- The VFS host root path is **hardcoded** in `HCore.Main/Program.cs` (`_vfs.Mount("/", new HostFileSystem("/home/ardumine/hort/hcore/FS"))` in `Init()`). This must match the developer's machine.
- The init module name is **hardcoded** as `"HCore.Packages.HInit.Init"` in `Program.Main()` (the kernel `host.Spawn<IRunnable>("HCore.Packages.HInit.Init", "init")` then `.Run()`s it, so init appears at `/proc/init`).
- Each package directory needs a manually-created `mpd` file (plain text: DLL name on line 1, optional PDB on line 2).
- `CopyLocalLockFileAssemblies` must be `true` in package `.csproj` files so NuGet deps land next to the DLL.
- The `FS/packs/` directory contains **compiled output** (DLLs, PDBs) deployed by post-build steps — these are runtime artifacts on disk, not source.
- `BaseImplement.OnKilled()`/`DescribeForProc()` are `protected internal`, not `public`. `HCore.Main` can call them directly on a `BaseImplement` reference only because `HCore.Modules.Base/AssemblyInfo.cs` grants it via `[assembly: InternalsVisibleTo("HCore.Main")]`. A sibling package still can't call them on another module's instance — protected access is scoped to your own class hierarchy, `InternalsVisibleTo` only extends the *internal* half of `protected internal` to the one named friend assembly.
- Overriding a `protected internal` member from a subclass in a **different assembly** (e.g. `UsbDeviceImplement` overriding `BaseImplement.DescribeForProc()`) must be declared just `protected override`, not `protected internal override` — C# won't let a foreign-assembly override reclaim the `internal` half.
- **Kernel-service registry.** `ModuleHost.GetModuleInterface<T>` accepts a reserved `@`-prefixed name (e.g. `"@afcp"`) that resolves against a separate kernel-service registry (`RegisterKernelService`), NOT the `/proc` instance table. These are kernel-space singletons (like the AFCP bridge) that implement a `HCore.Modules.Base` contract but are not `BaseImplement` modules and don't appear in `/proc`. A `@`-name never collides with an instance name.
- **AFCP serializer gotchas.** The standalone `AFCP/` serializer is a port of V2's reflection-free IL-emit serializer. `Nullable<T>` *value* types are NOT supported (reference-types only) — protocol messages use `long X` + `bool HasX` flag pairs instead. Serializer types live in namespace `AFCP` (root), not `AFCP.Serializer`, because the class `Serializer` collided with the namespace (CS0118). The `List<T>`/`Dictionary<,>` emitters reach into private fields (`_items`/`_size`, `_entries`/`_count`) — verified on net10.0 but fragile across .NET versions. Class **deserialization requires a parameterless constructor** (`ClassSerializer` constructs then sets properties) — a positional `record` needs an explicit `public T() : this(...)`. This only bites a type that actually crosses the wire: local data-plane facet values pass by reference and `cat` uses the text formatter, so a facet type like `ScanFrame` first needs this the moment it's used with a remote AFCP subscribe (Layer 2).

## Creating a new package

1. `dotnet new classlib -n HCore.Packages.Foo --framework net10.0`
2. Add to `hcore.sln`
3. Reference only `HCore.Modules.Base`
4. Add PostBuild target to copy output to `FS/packs/$(AssemblyName)/`
5. Create the module triple: `IFoo : IModule`, `FooImplement : BaseImplement, IFoo`, `ModDescriptor : IModuleDescriptor`
6. Create `FS/packs/HCore.Packages.Foo/mpd` with the DLL filename

## Target framework

All projects target **net10.0** (.NET 10 Preview). Requires the .NET 10 SDK.
