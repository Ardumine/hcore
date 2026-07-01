# HCore Service Manager

HCore has no `.service` files. The unit of service definition is a `.svc` file — a plain HCore shell script living under `/etc/services/`. The filename stem becomes the service name (`usb.svc` → service `usb`).

## Architecture

Three pieces cooperate:

| Piece | Package | Role |
|-------|---------|------|
| `IServiceManager` | `HCore.Modules.Base` | Shared contract interface |
| `InitImplement` | `HCore.Packages.HInit` | PID 1, implements `IServiceManager`, owns both shells |
| `ServiceCommand` | `HCore.Packages.HShell` | Shell `service` command, bridges to init via `IServiceManager` |

## Init lifecycle

`InitImplement.Run()` does three things in order:

1. **Spawn worker shell** (`/proc/init/svc`) — used only for `RunScript`, never enters its REPL.
2. **Boot services** — enumerates `/etc/services/*.svc` alphabetically, runs each one through the worker shell.
3. **Spawn console shell** (`/proc/init/console`) — blocks on the interactive REPL. Kernel stops when the console exits.

## How a service starts

`StartService(name)`:

1. Checks `/etc/services/<name>.svc` exists on disk → `Failed` if missing.
2. Checks `/proc/<name>` already exists → `Running` if already there (idempotent).
3. Delegates to worker shell's `RunScript(path)`, which reads the file line-by-line and dispatches each line through the shell's command registry (same path as the interactive REPL).
4. Returns `Running` only if `/proc/<name>` exists after the script finishes.

## How a service stops

`StopService(name)` calls `Host.Kill(name)` — a privileged cascade kill that reaps the primary instance and all its descendants. Returns `Stopped` if the instance wasn't running.

## How the shell manages services at runtime

The `service start|stop|restart|status|list` shell command reaches across the assembly-load-context boundary via:

```csharp
manager = ctx.Host.GetModuleInterface<IServiceManager>("init");
```

This calls the exact same `StartService`/`StopService`/etc. methods on the init module itself.

## The `.svc` convention

A `.svc` script must `spawn` and `run` a module instance named exactly after the service. The init module uses only this instance's presence in `/proc` to determine status — it has no other metadata.

Example (`/etc/services/usb.svc`):
```
# usb.svc — start the USB demo controller.
spawn HCore.Packages.Usb.Usb usb
run usb
```

## Contrast with systemd

| Aspect | systemd | HCore |
|--------|---------|-------|
| Unit file format | Declarative `.service` INI-style | Imperative `.svc` shell script |
| Actions | Properties describe what to run | Script literally runs shell commands |
| Conventions | `ExecStart`, `Type`, `Restart`, etc. | Convention: spawn+run an instance matching the filename |
| Topology | Dependency graph via `Requires`/`Wants` | Alphabetical script order only |
| Restart policy | `Restart=on-failure` / `RestartSec` | None |
| Socket activation | `*.socket` units | None |
| Timer activation | `*.timer` units | None |
| Status tracking | Full cgroup / pid tracking | `/proc/<name>` existence only |

## Future TODO

- [ ] **Ordering dependencies** — currently scripts run in alphabetical order with no `Requires`/`Before`/`After` graph. A competing service would need to know the ordering convention and hope for the best.
- [ ] **Restart policy** — a crashed service instance disappears from `/proc` and stays dead. No automatic restart.
- [ ] **Service metadata** — status is a binary `/proc/<name>` presence check. No tracking of exit codes, startup time, resource usage, or log streams.
- [ ] **Conditional start** — no `ConditionPathExists`, `ConditionFileNotEmpty`, or environment checks. The script must handle all preconditions itself.
- [ ] **Template units** — no `foo@.svc` parameterized instances.
- [ ] **Lifecycle hooks** — no `ExecStartPre`, `ExecStartPost`, or `ExecStop` commands. A stop is just `Kill`.
- [ ] **User / capability scoping** — `Kill` is privileged (unrestricted). No per-service sandboxing or resource limits.
