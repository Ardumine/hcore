# HCore Shell

The shell is its own package, `HCore.Packages.HShell` (`IShell`), and the init module (`HCore.Packages.HInit`, PID 1) drives it. On boot, init:

1. spawns a **worker shell** at `/proc/init/svc` (used only for `RunScript` — it never enters its REPL),
2. runs every `/etc/services/*.svc` script through that worker (each script spawns+runs a service — see [Services](#services)),
3. spawns the **interactive console shell** at `/proc/init/console` and blocks on its REPL.

When the console shell exits, init returns and the kernel stops.

## Starting it

```bash
dotnet run --project HCore.Main
```

> Use a **real terminal**. The console shell reads keystrokes directly (via the `ReadLine` library) and will throw if its input is piped or redirected.

## Commands

The shell dispatches through an `ICommand` registry (one class per command). Built-ins:

| Command | Description |
|---------|-------------|
| `help` | List all commands |
| `exit` | Exit the shell (init then returns and the kernel stops) |
| `pwd` | Print the working directory |
| `cd <path>` | Change working directory (defaults to `/`) |
| `ls [path]` | List directory entries (defaults to `.`) |
| `cat <file>` | Print a file's contents |
| `mkdir <dir>` | Create a directory |
| `rm <file>` | Remove a file |
| `rmdir <dir>` | Remove an empty directory |
| `touch <file>` | Create an empty file if missing |
| `exists <path>` | Print `true` / `false` |
| `mv <src> <dst>` | Move a file or directory |
| `rename <path> <new-name>` | Rename a file or directory |
| `write <file> <text>` | Overwrite a file with text |
| `append <file> <text>` | Append text to a file |
| `spawn <module> <instance>` | **Spawn** a new named instance (does **not** run it) |
| `run <instance>` | Run an already-spawned instance by its `/proc` path (must be `IRunnable`) |
| `kill <instance>` | **Kill** an instance and cascade to every child it owns (privileged) |
| `service <start\|stop\|restart\|status\|list> [name]` | Manage `/etc/services` entries (see [Services](#services)) |
| `clear` | Clear the terminal |

Arguments containing spaces can be quoted: `write notes.txt "hello world"`.

## Services

A **service** is a shell script at `/etc/services/<name>.svc`. By convention the script must spawn and run a module instance named exactly `<name>` (the service's primary instance, like systemd's main PID). Stopping a service kills that instance and, by cascade, every child it owns.

| Sub-command | Effect |
|-------------|--------|
| `service start <name>` | Run `/etc/services/<name>.svc` via the worker shell. Reports `Running` only if `/proc/<name>` exists afterward. |
| `service stop <name>` | `Kill` the primary instance (cascade). Reports `Stopped`. |
| `service restart <name>` | Stop then start. |
| `service status <name>` | `Running` / `Stopped` / `Failed` (no script on disk). |
| `service list` | One line per `*.svc`, with current status. |

At boot init auto-starts every `*.svc` (sorted by filename). The `service` command reaches init through the shared `IServiceManager` interface (a `GetModuleInterface<IServiceManager>("init")` lookup), so the shell package never references the init package.

A script is plain shell text — `#` starts a comment, one command per line. Example (`/etc/services/usb.svc`):

```
# Start the USB demo controller.
spawn HCore.Packages.Usb.Usb usb
run usb
```

## The filesystem you'll see

| Path | What |
|------|------|
| `/` | Host filesystem (the real `FS/` directory) |
| `/etc/services` | Service start scripts (`*.svc`) |
| `/packs` | Installed packages (DLLs + `mpd`) |
| `/dev` | Synthetic device files (read-only) |
| `/tmp` | In-memory scratch (lost on exit) |
| `/proc` | Live view of running module instances (read-only) |

After boot, `/proc` shows init's children plus the booted services, e.g.:

```
/ $ ls /proc
init/
module1/
demo/
usb/
/ $ ls /proc/init
info
svc/
console/
```

## Example session — services and processes

```
/ $ service list
demo                 Running
usb                  Running
/ $ service stop usb
usb: Stopped
/ $ service start usb
spawned 'usb' from 'HCore.Packages.Usb.Usb' (at /proc/usb)
usb: Running
/ $ kill /proc/usb
killed '/proc/usb'
/ $ service status usb
usb: Stopped
```

`spawn` takes a module's **descriptor `Name`** plus an instance name and creates the instance but does **not** run it. `run` takes the `/proc` path (or bare name) of an **already-spawned** instance and runs it. `kill` is privileged: it works on any instance by path, not just ones you own. See [MODULE_HIERARCHY.md](MODULE_HIERARCHY.md) for the full hierarchy/cascade design and [MODULE_AUTHORING.md](MODULE_AUTHORING.md) to build your own module.
