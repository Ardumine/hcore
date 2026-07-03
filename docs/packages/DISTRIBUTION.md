# Distribution

How HCore packages are built, distributed, installed, and bootstrapped.

---

## Architecture

HCore is a microkernel with dynamically loaded packages. The kernel repo (`hcore/`) contains:

```
hcore/
  src/
    HCore.Main/             ← kernel
    HCore.Modules.Base/     ← shared kernel contracts
    HCore.Modules.Robotics/ ← shared domain contracts
  FS/                       ← runtime filesystem root (generated, not source)
```

Packages live in **separate repos** cloned alongside:

```
ardumine/
  hcore/       ← kernel
  hinit/       ← HCore.Packages.HInit (PID 1)
  hshell/      ← HCore.Packages.HShell (interactive shell)
  hpm/         ← HCore.Packages.Hpm (package manager)
  hsensors/    ← HCore.Packages.Sensor (LIDAR + SLAM)
  husb/        ← HCore.Packages.Usb (USB demo)
```

Each package repo references the kernel via a peer relative path and deploys its build output to `hcore/FS/packs/` via PostBuild.

---

## Bootstrap (First Boot)

When the kernel starts and `FS/packs/` is empty or missing essential packages, the bootstrap module runs **before** init:

```
Kernel boots
  → Mount FS root
  → Scan /packs/
  → HInit NOT found?  → RUN BOOTSTRAP
    1. Create FS skeleton: /packs/, /etc/services/, /home/, /data/
    2. Read bootstrap.json (embedded in kernel)
    3. For each essential package (hinit, hshell, hpm):
       Dev:  copy from ../<repo>/FS/packs/
       Release: HTTP GET .hpk from pinned URL
       → Extract into /packs/
    4. Re-scan /packs/ — register modules
  → Normal boot: spawn init → run init
```

On subsequent boots (packages already present), bootstrap is skipped.

The list of essential packages lives in `src/HCore.Main/Bootstrap/bootstrap.json`:

```json
{
  "essential": [
    { "name": "HCore.Packages.HInit",  "version": "1.0.0" },
    { "name": "HCore.Packages.HShell", "version": "1.0.0" },
    { "name": "HCore.Packages.Hpm",    "version": "1.0.0" }
  ]
}
```

---

## `.hpk` Format

The universal distribution format. See [PACKAGE_SYSTEM.md](PACKAGE_SYSTEM.md) §1 for the full spec.

### Creating a `.hpk`

```bash
# Inside HCore shell:
hpm pack /path/to/package/repo              # binary only
hpm pack --with-source /path/to/package/repo # include source code
```

Produces `<PackageName>-<version>.hpk`. Uses `dotnet publish -c Release` internally.

### Installing a `.hpk`

```bash
hpm install mypackage-1.0.0.hpk
```

What happens:
1. Read `manifest.json` from .hpk
2. Resolve `requires` — recursively fetch/install dependencies
3. Run `scripts/preinst.hsh` (if present)
4. Extract `lib/` → `/packs/<name>/`
5. Extract `src/` → `/packs/<name>/src/` (if --with-source was used)
6. Merge `etc/` overlay → `/etc/`
7. Run `scripts/postinst.hsh` (if present)

### Removing a package

```bash
hpm remove <package-name>
```

Runs `scripts/prerm.hsh` (if found on disk), then deletes `/packs/<name>/` recursively.

### Rebuilding from source

```bash
hpm rebuild <package-name> [--target-rid linux-riscv64]
```

Extracts `src/` from the installed package, runs `manifest.json`'s `source.buildCommand`, and replaces `lib/`. Useful for platform-specific rebuilds.

---

## Day-to-Day Development

1. **Clone kernel + desired packages:**
   ```bash
   git clone https://github.com/Ardumine/hcore.git
   git clone https://github.com/Ardumine/hinit.git
   git clone https://github.com/Ardumine/hshell.git
   git clone https://github.com/Ardumine/hpm.git
   ```

2. **Build a package:**
   ```bash
   cd hpm && dotnet build   # PostBuild copies to ../hcore/FS/packs/
   ```

3. **Run the kernel:**
   ```bash
   cd ../hcore
   dotnet run --project src/HCore.Main
   # Or override FS root:
   dotnet run --project src/HCore.Main -- --fs=/path/to/custom/FS
   ```

4. **Iterate:** edit package source, `dotnet build`, kernel auto-discovers updated DLLs on re-run.

---

## Release Pipeline

### Kernel

```bash
# Self-contained single-file (71MB, zero dependencies)
dotnet publish src/HCore.Main -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -o publish/

# Framework-dependent (77KB, needs .NET 10 runtime)
dotnet publish src/HCore.Main -c Release -o publish/
```

### Packages

Each package repo can publish `.hpk` artifacts via CI (GitHub Actions). The workflow:

1. `dotnet publish -c Release`
2. `hpm pack --with-source`
3. Upload `.hpk` as a GitHub Release asset

---

## FS Root

The kernel defaults to `./FS/` relative to the executable. Override:

```bash
dotnet run --project src/HCore.Main -- --fs=/opt/hcore/fs
# or
HCORE_FS_ROOT=/opt/hcore/fs dotnet run --project src/HCore.Main
```
