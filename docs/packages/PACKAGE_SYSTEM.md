# Package System — Design & Implementation Plan

> **Status:** Design complete · Implementation pending  
> **Related:** [MODULE_AUTHORING.md](../modules/MODULE_AUTHORING.md) · [TODO.md](../TODO.md) §E  

---

## 1. Motivation

Today HCore packages live in a monorepo under `HCore.Packages.*/`, built together in
`hcore.sln`, and deployed to `FS/packs/` via a PostBuild `cp -vr`. Each package will
eventually live in its **own GitHub repo** (see `AGENTS.md` §Module repositories).
This requires:

1.  **A distribution format** — a portable artifact a developer can produce from their
    package repo and a user can install into their HCore instance.
2.  **A package manager** — install, list, and remove packages from a running HCore
    system without manual file manipulation.
3.  **A shell extension point** — packages must be able to contribute commands to the
    shell, both as one-shot binaries (spawned per invocation, like `apt`) and as
    persistent services that register commands while running.

---

## 2. Two Command Modes

A package can contribute commands to the shell in two ways. They compose, not compete.

### 2.1 Oneshot (manifest-declared)

The package's `manifest.json` declares commands. The shell reads all manifests at
startup and creates lazy proxy `ICommand` entries that **spawn a fresh child module
instance for every invocation**, run it, wait for completion, and kill it.

```
User types:  hpm install foo.hpk
                 │
                 ▼
Shell ──► ManifestCommand.Execute(ctx)
              │
              ├─ ctx.Host.SpawnChildByName<IOneshotCommand>(
              │       "HCore.Packages.Hpm.Mod", "__cmd_hpm_a1b2", null)
              │
              ├─ child.SetArguments(["hpm", "install", "foo.hpk"])
              ├─ child.Run()                          ← blocks shell
              ├─ ctx.Host.KillChild("__cmd_hpm_a1b2")
              │
              ▼
         /proc/init/console/__cmd_hpm_a1b2   (appears & disappears)
```

This is the `apt`, `grep`, `ls` model — a process, not a daemon.

**The child appears at `/proc/init/console/__cmd_<name>_<guid>`** — a child of the
shell instance. If the shell is killed, all oneshot children are reaped automatically.
A GUID suffix prevents collisions on concurrent invocations (racy but harmless for
`hpm` — two `install`s to the same pack will just overwrite each other).

### 2.2 Persistent (runtime-registered)

A module that is **already spawned and running** (a service, a daemon, a long-lived
controller) calls `IShell.RegisterCommand(ICommand)` with its own `ICommand`
implementation. The shell adds it to the dispatch dictionary. No proxy, no spawn, no
kill — the command's `Execute()` runs directly in the module's code.

```
Module A (running at /proc/mya):
    var shell = Host.GetModuleInterface<IShell>("init/console");
    shell.RegisterCommand(new MyStatusCommand());   // adds "mystatus"
    shell.RegisterCommand(new MyConfigCommand());   // adds "myconfig"

User types:  mystatus
                 │
                 ▼
Shell ──► MyStatusCommand.Execute(ctx)    ← runs in Module A's ALC, zero proxying
```

This is the `service` command model — the shell holds a thin dispatch wrapper
(`ServiceCommand`) that delegates to init via `GetModuleInterface<IServiceManager>`.
The new `RegisterCommand` path removes the need for the shell to hardcode a wrapper
per service: the service registers its own commands directly.

---

## 3. Contract Changes (`HCore.Modules.Base`)

### 3.1 Move `ICommand` + `ShellContext` into Base

**Why:** a module in a different `AssemblyLoadContext` that implements `ICommand` or
calls `IShell.RegisterCommand(ICommand)` must share the same `Type` identity for
`ICommand` as the shell. Only types in `HCore.Modules.Base.dll` share identity across
all load contexts (the `ModPackAssemblyLoadContext.Load()` fallback to
`AssemblyLoadContext.Default` operates at the *assembly* level).

**Precedent:** `IShell`, `IAfcpKernel`, `IServiceManager`, `IUsbDevice` already live
in Base for the exact same reason.

**Source:** move the 44-line file from
`HCore.Packages.HShell/Shell/Commands/ICommand.cs` to
`HCore.Modules.Base/ICommand.cs`, changing only the namespace from
`HCore.Packages.HShell.Shell.Commands` to `HCore.Modules.Base`.

```csharp
// HCore.Modules.Base/ICommand.cs
namespace HCore.Modules.Base;

public sealed class ShellContext
{
    public IModuleFileSystem Vfs { get; }
    public IModuleHost Host { get; }
    public TextWriter Out { get; }
    public bool ExitRequested { get; private set; }

    public ShellContext(IModuleFileSystem vfs, IModuleHost host, TextWriter? output = null)
    {
        Vfs = vfs; Host = host; Out = output ?? Console.Out;
    }

    public void RequestExit() => ExitRequested = true;

    public static void RequireArgs(IReadOnlyList<string> args, int minimum, string usage)
    {
        if (args.Count < minimum)
            throw new InvalidOperationException(usage);
    }
}

public interface ICommand
{
    string Name { get; }
    string Description { get; }
    void Execute(IReadOnlyList<string> args, ShellContext ctx);
}
```

> `ShellContext` depends on `IModuleFileSystem` and `IModuleHost` (both in Base)
> and `TextWriter` (BCL, already referenced). No new dependency edges.

### 3.2 New interface: `IOneshotCommand`

```csharp
// HCore.Modules.Base/IOneshotCommand.cs
namespace HCore.Modules.Base;

/// <summary>
/// A module that is spawned, given arguments, run to completion, and killed —
/// the pattern for a one-shot shell command (like a Unix process).
/// Extends IRunnable (→ IModule), so it works with SpawnChildByName.
/// </summary>
public interface IOneshotCommand : IRunnable
{
    /// <summary>
    /// Receive the full argv before <see cref="IRunnable.Run"/> is called.
    /// args[0] is the command name (e.g. "hpm"), args[1] is the sub-command.
    /// </summary>
    void SetArguments(string[] args);
}
```

`IRunnable : IModule` ensures the constraint `where T : IModule` on
`SpawnChildByName<T>` is satisfied.

### 3.3 Extend `IShell`

```csharp
public interface IShell : IRunnable
{
    bool RunScript(string path);

    /// <summary>
    /// Register a command at runtime. A persistent module calls this after it is
    /// spawned to add its own commands to the shell's dispatch table. The command
    /// is stored in the same registry as built-in commands and is dispatched by
    /// the same REPL loop.
    /// </summary>
    void RegisterCommand(ICommand command);
}
```

---

## 4. Shell Infrastructure Changes (`HCore.Packages.HShell`)

### 4.1 New class: `ManifestCommand`

Lives in the shell package (does NOT need to be in Base — the shell creates these
internally).

```csharp
// HCore.Packages.HShell/Shell/Commands/ManifestCommand.cs
namespace HCore.Packages.HShell.Shell.Commands;

internal sealed class ManifestCommand : ICommand
{
    public string Name { get; }
    public string Description { get; }
    private readonly string _moduleName;

    public ManifestCommand(string name, string description, string moduleName)
        => (Name, Description, _moduleName) = (name, description, moduleName);

    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        var leafName = $"__cmd_{Name}_{Guid.NewGuid():N}";
        var child = ctx.Host.SpawnChildByName<IOneshotCommand>(
            _moduleName, leafName, null);
        try
        {
            child.SetArguments(args.ToArray());
            child.Run();   // blocks the shell
        }
        finally
        {
            ctx.Host.KillChild(leafName);
        }
    }
}
```

Key behaviors:
- **Blocks the shell** — `child.Run()` is synchronous; the REPL is frozen until
  the oneshot completes. This matches the existing `RunCommand` pattern and the
  Unix terminal model (a long `apt install` blocks the prompt).
- **Unique leaf names** — `Guid.NewGuid():N` prevents collisions on concurrent
  invocations (e.g. two shells running `hpm install` simultaneously).
- **Cleanup in `finally`** — ensures the `/proc` entry is removed even if
  `SetArguments` or `Run` throws.
- **`SpawnChildByName<IOneshotCommand>`** — uses the module name (a string), so
  the shell package needs no reference to the hpm package.

### 4.2 ShellImplement changes

**A. Manifest loading**

Add after `Vfs.SetWorkingDirectory("/")` in `Run()` and guarded in `RunScript()`:

```csharp
private bool _manifestsLoaded;

private void LoadManifestCommands()
{
    if (_manifestsLoaded) return;
    _manifestsLoaded = true;

    try
    {
        foreach (var pack in Vfs.ListDirectory("/packs"))
        {
            var manifestPath = $"/packs/{pack}/manifest.json";
            if (!Vfs.Exists(manifestPath)) continue;

            try
            {
                var json = Vfs.ReadAllText(manifestPath);
                var manifest = System.Text.Json.JsonSerializer
                    .Deserialize<PackManifest>(json);
                if (manifest?.commands is null) continue;

                foreach (var cmd in manifest.commands)
                {
                    if ("oneshot".Equals(cmd.mode, StringComparison.OrdinalIgnoreCase))
                    {
                        _registry.Register(new ManifestCommand(
                            cmd.name, cmd.description, cmd.moduleName));
                    }
                }
            }
            catch { /* malformed manifest — skip silently */ }
        }
    }
    catch { /* /packs might not exist */ }
}

// JSON contract (private, shell-internal)
private sealed class PackManifest
{
    public string? name { get; set; }
    public string? version { get; set; }
    public CommandEntry[]? commands { get; set; }
}

private sealed class CommandEntry
{
    public string name { get; set; } = "";
    public string description { get; set; } = "";
    public string mode { get; set; } = "";
    public string moduleName { get; set; } = "";
}
```

**B. `RegisterCommand` implementation**

```csharp
public void RegisterCommand(ICommand command) => _registry.Register(command);
```

Trivial — `CommandRegistry.Register` is already public and backed by a
`Dictionary<,>` (safe for concurrent readers after registration, though the
shell's dispatch is single-threaded).

### 4.3 Shell migration — fix usings

After deleting `HCore.Packages.HShell/Shell/Commands/ICommand.cs`:

| File | Change |
|------|--------|
| `FileSystemCommands.cs` | Add `using HCore.Modules.Base;` |
| `ProcessCommands.cs` | Add `using HCore.Modules.Base;` |
| `ServiceCommand.cs` | Already has `using HCore.Modules.Base;` — verify |
| `AfcpCommand.cs` | Already has `using HCore.Modules.Base;` — verify |
| `HelpCommand.cs` | Add `using HCore.Modules.Base;` |
| `CommandRegistry.cs` | Add `using HCore.Modules.Base;` |
| `ShellImplement.cs` | Add `using HCore.Modules.Base;` |
| `AutoCompletionHandler.cs` | Already resolves via `CommandRegistry` — verify |

Auto-completion works without changes: `_registry.Commands` includes all registered
commands (builtin + manifest + runtime), so command-name tab-completion covers
everything. Sub-command completion for dynamic commands is deferred.

---

## 5. `manifest.json` Format

Every package under `/packs/<name>/` **may** include a `manifest.json`. The shell
reads it at startup to discover commands. All fields except `commands` are currently
advisory.

```json
{
  "name": "HCore.Packages.Hpm",
  "version": "1.0.0",
  "baseVersion": "1.0.0",
  "description": "HCore Package Manager",
  "commands": [
    {
      "name": "hpm",
      "description": "Package manager — install, list, remove, pack",
      "mode": "oneshot",
      "moduleName": "HCore.Packages.Hpm.Mod"
    }
  ]
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `name` | — | Package identity; matches the `/packs/` directory name |
| `version` | — | Semantic version (advisory, not enforced) |
| `baseVersion` | — | Minimum `HCore.Modules.Base` version expected (advisory) |
| `description` | — | Human-readable summary |
| `commands[].name` | yes | Shell command name (case-insensitive, must be unique) |
| `commands[].description` | yes | Help text shown by `help` |
| `commands[].mode` | yes | `"oneshot"` — spawned per invocation. Future: `"persistent"`. |
| `commands[].moduleName` | yes | `IModuleDescriptor.Name` of the module to spawn for oneshot |

A package with no commands simply omits the `commands` array or the `manifest.json`
entirely.

---

## 6. `.hpk` Distribution Format

A `.hpk` file is a **gzip-compressed tar archive** (`.tar.gz`) with a flat
internal structure:

```
mypackage-1.0.0.hpk
├── manifest.json     # same format as §5
├── mpd               # "MyPackage.dll\\nMyPackage.pdb"
├── MyPackage.dll
├── MyPackage.pdb
├── HCore.Modules.Base.dll
└── *.dll             # transitive NuGet dependency DLLs
```

Files are stored flat at the root of the archive — no `lib/` subdirectory.
This matches the existing `FS/packs/<name>/` directory structure.

**Install semantics:**
- Extract to `FS/packs/<name>/` where `<name>` is read from `manifest.json`.
- If the directory already exists, files are overwritten (merge — a re-install).
- No dependency resolution — packages do not depend on each other, only on
  `HCore.Modules.Base` (which the kernel already has).
- After install, the user must restart the shell (or type `exit` and re-enter)
  for `manifest.json` to be re-read and new commands to appear.

**Production considerations (out of scope):**
- Signature/hash verification
- Pre/post-install hooks
- Uninstall that removes only files originally installed (vs. `rm -rf`)

---

## 7. `HCore.Packages.Hpm` — The Package Manager

### 7.1 Project structure

```
HCore.Packages.Hpm/
├── HCore.Packages.Hpm.csproj
├── HpmImplement.cs
└── ModDescriptor.cs
```

### 7.2 csproj

Standard package project referencing only `HCore.Modules.Base`, with
`CopyLocalLockFileAssemblies` (no NuGet deps needed) and a PostBuild target
that copies output to `FS/packs/HCore.Packages.Hpm/`.

### 7.3 Module descriptor

```csharp
// ModDescriptor.cs
public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Packages.Hpm.Mod";
    public string FriendlyName => "HCore Package Manager";
    public Type ImplementType => typeof(HpmImplement);
    public Type InterfaceType => typeof(IOneshotCommand);
}
```

The module name `"HCore.Packages.Hpm.Mod"` is what the shell's `ManifestCommand`
spawns when `hpm` is invoked.

### 7.4 Implementation — `HpmImplement : BaseImplement, IOneshotCommand`

Four sub-commands dispatched via `args[1]` (matching the `service` / `afcp` pattern):

```
hpm install <path-to-hpk>    Extract a .hpk into /packs/<name>/
hpm list                     List installed packages (reads /packs/*/manifest.json)
hpm remove <name>            Delete /packs/<name>/ recursively
hpm pack <project-dir>       Build a package repo with dotnet publish, produce .hpk
```

**Key details:**

| Sub-command | Uses | Notes |
|-------------|------|-------|
| `install` | `Vfs.ResolvePath`, `GZipStream`, `TarReader`, `Vfs.CreateDirectory`, `Vfs.GetFile().GetStream()` | Reads `manifest.json` from the tar to determine the package name, then extracts everything. Uses `System.Formats.Tar` (in .NET 10 BCL). |
| `list` | `Vfs.ListDirectory("/packs")`, `Vfs.ReadAllText` | Prints name + version for each pack with a manifest. |
| `remove` | `Vfs.DeleteDirectory(recursive: true)` | Destructive — no confirmation prompt in v1. |
| `pack` | `Process.Start("dotnet", "publish ...")` | Shells out to the host's `dotnet` CLI. Needs the project to have a `manifest.json` at its root. Produces a `.hpk` in the current directory. Only works on host filesystem paths (not remote mounts). |

**`hpm pack` notes:**
- Runs `dotnet publish -c Release -o <tempdir>` in the project directory.
- Reads `manifest.json` from the project root to determine the package name.
- Wraps the publish output + manifest + mpd in a tar.gz.
- Requires `dotnet` on the host PATH (reasonable — you're building .NET code).
- Uses host `System.IO.File` (not VFS) for the publish output, because post-build
  artifacts land on the host filesystem.

**Output:** The module uses `Console.WriteLine` directly for user-facing output
(the shell REPL shares the same process stdout). Logger is available for internal
diagnostics.

### 7.5 Bootstrap

`hpm` itself is a package — it must be pre-installed in `FS/packs/HCore.Packages.Hpm/`
as part of the base HCore distribution. The first `hpm install` cannot install `hpm`.

The base distribution tarball includes:
- `FS/packs/HCore.Packages.HInit/`
- `FS/packs/HCore.Packages.HShell/`
- `FS/packs/HCore.Packages.Hpm/`

A future `menuconfig`-style build tool would let users select which packages ship
in their distribution.

---

## 8. Implementation Phases

### Phase 1 — Contracts (`HCore.Modules.Base`)
- [ ] Create `HCore.Modules.Base/ICommand.cs` (move from HShell)
- [ ] Create `HCore.Modules.Base/IOneshotCommand.cs`
- [ ] Modify `HCore.Modules.Base/IShell.cs` — add `RegisterCommand`

**Verification:** `dotnet build` succeeds. No runtime test yet.

### Phase 2 — Shell infrastructure (`HCore.Packages.HShell`)
- [ ] Delete `Shell/Commands/ICommand.cs` (moved to Base)
- [ ] Create `Shell/Commands/ManifestCommand.cs`
- [ ] Add `LoadManifestCommands()` to `ShellImplement`
- [ ] Add `RegisterCommand()` to `ShellImplement`
- [ ] Add JSON model classes (private, in `ShellImplement.cs`)
- [ ] Fix `using` statements in all shell command files

**Verification:** `dotnet build` succeeds. `dotnet run` — shell boots, `help` works,
built-in commands work. Console shows no errors about missing manifests.

### Phase 3 — `HCore.Packages.Hpm`
- [ ] Create project: `dotnet new classlib -n HCore.Packages.Hpm --framework net10.0`
- [ ] Write csproj (reference Base, PostBuild target)
- [ ] Create `ModDescriptor.cs`
- [ ] Create `HpmImplement.cs` (`install`, `list`, `remove`, `pack`)
- [ ] Create `FS/packs/HCore.Packages.Hpm/mpd`
- [ ] Create `FS/packs/HCore.Packages.Hpm/manifest.json`
- [ ] Add to `hcore.sln`

**Verification:** `dotnet build hcore.sln` copies output. `dotnet run` — `hpm` appears
in `help`. `hpm list` shows installed packages including itself.

### Phase 4 — Round-trip test
- [ ] Create a dummy package `HCore.Packages.DemoPkg` with an `IOneshotCommand`
- [ ] Write its `manifest.json` declaring a oneshot command
- [ ] Use `hpm pack` to produce `DemoPkg.hpk`
- [ ] Use `hpm install DemoPkg.hpk` to install it
- [ ] Restart shell, verify the command appears
- [ ] Run the command, verify it works
- [ ] `hpm remove DemoPkg`, restart shell, verify command is gone

---

## 9. Distribution & Bootstrap

### 9.1 Base system tarball

The HCore kernel + essential packages ship as a single tarball:

```
hcore-1.0.0.tar.gz
├── HCore.Main.dll
├── HCore.Modules.Base.dll
├── FS/
│   ├── etc/
│   │   └── services/
│   │       └── *.svc
│   └── packs/
│       ├── HCore.Packages.HInit/
│       │   ├── mpd, manifest.json
│       │   └── lib/*.dll
│       ├── HCore.Packages.HShell/
│       │   ├── mpd, manifest.json
│       │   └── lib/*.dll
│       └── HCore.Packages.Hpm/
│           ├── mpd, manifest.json
│           └── lib/*.dll
```

The kernel's `Program.Main` hardcodes the FS root mount and the init module name
(see `AGENTS.md` §Key gotchas). These remain hardcoded for the base distribution.

### 9.2 Future: `menuconfig` tool

A standalone CLI tool (not an HCore module — it runs on the host at build time):

```
hcore-menuconfig
  ✓ HCore.Packages.HInit          [built-in]
  ✓ HCore.Packages.HShell         [built-in]
  ✓ HCore.Packages.Hpm            [built-in]
  ☐ HCore.Packages.Sensor         (LIDAR + SLAM demo)
  ☐ HCore.Packages.Usb            (USB controller + device demo)
  ☐ HCore.Packages.Nexus          (AFCP connector — future)
```

Generates a `FS/packs/` directory tree with the selected packages, ready for
tarball packaging. This is the Linux `make menuconfig` analog.

### 9.3 Future: package registry

A simple JSON index hosted at a known URL (e.g. `packages.hcore.dev/index.json`)
listing available packages with download URLs. `hpm install hcore-packages-sensor`
resolves the name to a URL and downloads the `.hpk`. Out of scope for v1.

---

## 10. Design Decisions & Tradeoffs

### 10.1 Why `ICommand` is in Base

The binary choice: **ALC type identity.** A module registering an `ICommand` at
runtime passes a concrete implementation from its own `AssemblyLoadContext`. The
shell receives it in the default ALC. If `ICommand` were in the shell's package
assembly, the two sides would hold different `Type` objects — `is`/`as` checks
would silently fail, `Dictionary<ICommand>` lookups would miss. Moving it to
`HCore.Modules.Base` is the ONLY mechanism for cross-ALC type identity in this
architecture.

The precedent is already well-established: `IShell`, `IAfcpKernel`,
`IServiceManager`, `IUsbDevice` all live in Base for the same reason, despite
being optional components. Base is pragmatically a grab-bag of cross-module
contracts, and adding `ICommand` does not change its nature.

### 10.2 Why `ShellContext` is in Base

`ShellContext` is a **mutable sealed class** — the only mutable concrete class in
Base. The alternative (split it: keep `ShellContext` in the shell, change `ICommand`
to take raw `IModuleFileSystem`, `IModuleHost`, `TextWriter`, `bool` parameters) is
technically possible but degrades the module developer experience:

```csharp
// With ShellContext in Base (Option A):
void Execute(IReadOnlyList<string> args, ShellContext ctx);

// Without ShellContext (alternative):
void Execute(IReadOnlyList<string> args,
    IModuleFileSystem vfs, IModuleHost host,
    TextWriter out, ref bool exitRequested);
```

The 33-line `ShellContext` class is trivial overhead. It bundles the two most-used
injected handles, adds an output writer, an exit signal, and a one-line argument
guard — these are useful to every command author.

### 10.3 Why oneshot = child of shell

- **Clean lifecycle.** The oneshot instance is structurally owned by the shell. If
  the shell is killed, all oneshot children are reaped automatically
  (`ContainerImplement` cascade).
- **Visible in `/proc`.** The process tree shows running commands:
  `/proc/init/console/__cmd_hpm_a1b2`. Matches the Unix mental model.
- **No `/proc` clutter.** The child is killed in a `finally` block after `Run()`
  completes. Exception or success — the entry is removed.
- **Scoped.** The shell uses `SpawnChildByName<T>` (scoped to its own owner), not
  the unrestricted `Spawn<T>`. A command can't accidentally create a top-level
  orphan instance.

### 10.4 Why manifest.json is not mandatory

A package that only provides library modules (no commands) shouldn't need a
manifest. The kernel doesn't read manifests — only the shell does, and only for
command discovery. A package with no `manifest.json` is still loaded by the
kernel, its modules are still spawnable, but it adds no shell commands.

### 10.5 Why `hpm pack` shells out to `dotnet publish`

- **Single tool.** `hpm` is the package manager. Developers should not need a
  separate build tool.
- **`dotnet` is already required.** You need the .NET 10 SDK to build any HCore
  package. `dotnet publish` is the standard way to produce a self-contained
  output folder with all transitive dependencies.
- **The HCore process has unrestricted `Process.Start`.** No sandboxing exists.
  The shell runs as the same user who launched the kernel.

---

## 11. File Manifest

### New files
| Path | Purpose |
|------|---------|
| `HCore.Modules.Base/ICommand.cs` | `ICommand` + `ShellContext`, moved from HShell |
| `HCore.Modules.Base/IOneshotCommand.cs` | New interface for one-shot modules |
| `HCore.Packages.HShell/Shell/Commands/ManifestCommand.cs` | Lazy proxy for manifest-declared commands |
| `HCore.Packages.Hpm/HCore.Packages.Hpm.csproj` | New package project |
| `HCore.Packages.Hpm/HpmImplement.cs` | Package manager implementation |
| `HCore.Packages.Hpm/ModDescriptor.cs` | Module descriptor |
| `FS/packs/HCore.Packages.Hpm/mpd` | Mod pack descriptor (DLL name) |
| `FS/packs/HCore.Packages.Hpm/manifest.json` | Package manifest declaring `hpm` command |
| `docs/packages/PACKAGE_SYSTEM.md` | This document |

### Modified files
| Path | Change |
|------|--------|
| `HCore.Modules.Base/IShell.cs` | Add `RegisterCommand(ICommand)` |
| `HCore.Packages.HShell/Shell/ShellImplement.cs` | Add manifest loader, `RegisterCommand` impl, JSON models |
| `HCore.Packages.HShell/Shell/Commands/*.cs` (8 files) | Add `using HCore.Modules.Base;` |
| `hcore.sln` | Add `HCore.Packages.Hpm` project |

### Deleted files
| Path | Reason |
|------|--------|
| `HCore.Packages.HShell/Shell/Commands/ICommand.cs` | Moved to `HCore.Modules.Base/ICommand.cs` |
