# Package System — Design & Implementation

> **Status:** Implemented  
> **Related:** [MODULE_AUTHORING.md](../modules/MODULE_AUTHORING.md) · [DISTRIBUTION.md](DISTRIBUTION.md)

---

## 1. `.hpk` Format

A `.hpk` file is a **gzip-compressed tar archive** (`.tar.gz`):

```
mypackage-1.0.0.hpk
├── manifest.json        ← package identity, version, dependencies, commands
├── mpd                  ← DLL name (line 1), optional PDB (line 2)
├── lib/                 ← compiled binaries
│   ├── MyPackage.dll
│   ├── MyPackage.pdb
│   ├── MyPackage.deps.json
│   ├── HCore.Modules.Base.dll
│   └── *.dll            ← NuGet transitive deps
├── src/                 ← source code (optional, --with-source)
│   ├── *.cs
│   └── *.csproj
├── etc/                 ← config overlay → merged into /etc/ on install
│   └── services/
│       └── mypkg.svc
└── scripts/             ← maintainer scripts (optional)
    ├── preinst.hsh
    ├── postinst.hsh
    ├── prerm.hsh
    └── postrm.hsh
```

### `manifest.json`

```json
{
  "name": "HCore.Packages.Sensor",
  "version": "2.1.0",
  "baseVersion": "1.0.0",
  "description": "LIDAR + SLAM sensor stack",
  "requires": {
    "HCore.Packages.Usb": ">=1.0.0"
  },
  "provides": {
    "modules": [
      { "name": "HCore.Packages.Sensor.Lidar", "interface": "HCore.Modules.Robotics.ILidar" },
      { "name": "HCore.Packages.Sensor.Slam",  "interface": "HCore.Packages.Sensor.Slam.ISlam" }
    ]
  },
  "source": {
    "language": "csharp",
    "framework": "net10.0",
    "buildCommand": "dotnet publish -c Release -o lib/"
  },
  "commands": [
    {
      "name": "sensor",
      "description": "Manage sensor pipeline",
      "mode": "oneshot",
      "moduleName": "HCore.Packages.Sensor.Mod"
    }
  ]
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `name` | yes | Package identity; matches `/packs/` directory name |
| `version` | yes | Semantic version |
| `baseVersion` | — | Minimum `HCore.Modules.Base` version expected |
| `description` | — | Human-readable summary |
| `requires` | — | Package dependencies (name → version range) |
| `provides.modules` | — | Modules this package registers |
| `source` | — | Build metadata for `hpm rebuild` |
| `commands` | — | Shell commands (one-shot or persistent) |

---

## 2. `hpm` — Package Manager

### Commands

| Command | Description |
|---------|-------------|
| `hpm install <path.hpk>` | Extract .hpk into `/packs/<name>/`, resolve deps, merge etc/ overlay, run scripts |
| `hpm list` | List installed packages with versions and dependency info |
| `hpm remove <name>` | Run prerm script, delete `/packs/<name>/` recursively |
| `hpm pack [--with-source] <dir>` | `dotnet publish` + package into .hpk (with optional src/) |
| `hpm rebuild <name> [--target-rid <rid>]` | Extract src/ from installed package, compile, replace lib/ |

### Dependency Resolution

`hpm install` reads `manifest.json` from the .hpk, checks the `requires` field, and recursively installs missing dependencies. Dependencies are looked up:

1. **Installed** — skip if already in `/packs/`
2. **Local** — check workspace root for `<DepName>.hpk`
3. **Manual** — print a message asking the user to install the dependency manually

### Install Scripts

Scripts are HShell `.hsh` files executed at specific points:

| Script | When |
|--------|------|
| `preinst.hsh` | Before extraction (validate pre-requisites) |
| `postinst.hsh` | After extraction (enable service, register commands) |
| `prerm.hsh` | Before removal (stop service) |

Scripts are run via `IShell.RunScript()`.

### `etc/` Overlay

The `etc/` directory in a .hpk is merged into `/etc/` on install. This is how packages ship service definitions:

```
HCore.Packages.Sensor/etc/services/sensor.svc → /etc/services/sensor.svc
```

The init module reads `/etc/services/*.svc` at boot and spawns the listed services.

---

## 3. Distribution & Bootstrap

### Bootstrap flow

On first boot with an empty `FS/packs/`, the kernel runs the bootstrap module (embedded in `HCore.Main`):

1. Create FS skeleton: `/packs/`, `/etc/services/`, `/home/`, `/data/`
2. Read `bootstrap.json` (embedded resource) for essential packages
3. **Dev mode:** copy from peer-repo build output (`../hinit/FS/packs/`, etc.)
4. **Release mode:** HTTP fetch `.hpk` from pinned URLs
5. Extract each package into `/packs/`
6. Warm re-scan — register modules, proceed to normal boot

On subsequent boots, if `/packs/` has packages, bootstrap is skipped.

### Workspace layout

```
ardumine/
  hcore/          ← kernel + Base + Robotics (this repo)
  hinit/          ← HCore.Packages.HInit
  hshell/         ← HCore.Packages.HShell
  hpm/            ← HCore.Packages.Hpm
  hsensors/       ← HCore.Packages.Sensor
  husb/           ← HCore.Packages.Usb
```

Each package repo references the kernel via a peer relative path:
```xml
<ProjectReference Include="..\hcore\src\HCore.Modules.Base\HCore.Modules.Base.csproj" />
```

PostBuild deploys to the kernel's `FS/packs/`:
```xml
<Exec Command="cp -vr $(OutDir)/* $(ProjectDir)/../hcore/FS/packs/$(AssemblyName)/" />
```

---

## 4. Shared Types — `HCore.Modules.Robotics`

Interfaces that cross ALC boundaries must live in `HCore.Modules.Base` or `HCore.Modules.Robotics`. Both assemblies are loaded in the `Default` ALC context, so their types share identity across all package ALCs.

```
HCore.Modules.Base      → kernel contracts (IModule, IRunnable, ICommand, IModuleHost, ...)
HCore.Modules.Robotics  → domain contracts (ILidar, future: IUsbDevice, ISlam, ...)
```

Packages that need Robotics types add a reference:
```xml
<ProjectReference Include="..\hcore\src\HCore.Modules.Robotics\HCore.Modules.Robotics.csproj" />
```

---

## 5. Creating a New Package

See [MODULE_AUTHORING.md](../modules/MODULE_AUTHORING.md) for the full walkthrough. Quick start:

```bash
# Create a standalone repo (peer to hcore/)
mkdir mypkg && cd mypkg
dotnet new classlib -n HCore.Packages.MyPkg --framework net10.0

# Reference kernel modules
# .csproj: <ProjectReference Include="..\hcore\src\HCore.Modules.Base\..." />

# Create the module triple (interface + implement + descriptor)
# Create manifest.json, mpd, etc/services/*.svc
# Build: dotnet build  (PostBuild copies to ../hcore/FS/packs/)
# Run: cd ../hcore && dotnet run --project src/HCore.Main
```
