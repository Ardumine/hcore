# HCore Shell (HInit)

`HCore.Packages.HInit` is the **init module** — the first module the kernel runs. It provides an interactive shell over the VFS, plus commands to run and spawn other modules. When the shell exits, the kernel stops.

## Starting it

```bash
dotnet run --project HCore.Main
```

> Use a **real terminal**. The shell reads keystrokes directly (via the `ReadLine` library) and will throw if its input is piped or redirected.

## Commands

| Command | Description |
|---------|-------------|
| `help` | List all commands |
| `exit` | Exit the shell (the kernel then stops) |
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
| `clear` | Clear the terminal |

Arguments containing spaces can be quoted: `write notes.txt "hello world"`.

## The filesystem you'll see

| Path | What |
|------|------|
| `/` | Host filesystem (the real `FS/` directory) |
| `/packs` | Installed packages (DLLs + `mpd`) |
| `/dev` | Synthetic device files (read-only) |
| `/tmp` | In-memory scratch (lost on exit) |
| `/proc` | Live view of running module instances (read-only) |

## Example session — inter-module calls and processes

```
/ $ ls /proc
init/

/ $ spawn HCore.Modules.TestDemo.Module1 module1
spawned 'module1' from 'HCore.Modules.TestDemo.Module1' (at /proc/module1)

/ $ spawn HCore.Packages.TestDemo.Module2 m2
spawned 'm2' from 'HCore.Packages.TestDemo.Module2' (at /proc/m2)

/ $ run /proc/m2
Run Module 2!
Func1 was called!

/ $ ls /proc
init/
module1/
m2/

/ $ cat /proc/m2/info
instance:   m2
module:     HCore.Packages.TestDemo.Module2
friendly:   Demo module 2
interface:  HCore.Packages.TestDemo.Module2.IModule2
implements: HCore.Packages.TestDemo.Module2.Module2Implement
```

`spawn` takes a module's **descriptor `Name`** (e.g. `HCore.Modules.TestDemo.Module1`, `HCore.Packages.TestDemo.Module2`) plus an instance name; it creates the instance but does **not** run it, so it works for any module including services with no entry point (like `Module1`). `run` takes the `/proc` path (or bare name) of an **already-spawned** instance and runs it — it never creates anything. `m2` calls `Module1`, which must already be spawned at `/proc/module1`. See [DESIGN.md](DESIGN.md) for the spawn-vs-lookup distinction and [MODULE_AUTHORING.md](MODULE_AUTHORING.md) to build your own.
