# HCore Agent System — Overview

## What is HCore?

HCore is a microkernel-based system with a **Virtual Filesystem (VFS)** that unifies all resources — files, running processes, configuration, and hardware — under a single tree. Everything is a path.

## VFS Layout

| Path | Purpose |
|------|---------|
| `/packs` | Installed packages (modules, manifests, binaries) |
| `/proc` | Running module instances (read-only) |
| `/etc` | Configuration files and service definitions |
| `/home` | User data |
| `/data` | (Reserved) |
| `/agent-files` | Agent-created workspace |

## Running Instances (under `/proc`)

| Instance | Module | Role |
|----------|--------|------|
| `init` | `HCore.Packages.HInit.Init` | System init — spawns services from `/etc/services/` |
| `init/console` | `HCore.Packages.HShell.Shell` | Interactive shell (REPL) |
| `init/console/__cmd_agent_*` | `HCore.Packages.Agent.Agent` | **This LLM agent** (one-shot, per-command) |
| `init/svc` | `HCore.Packages.HShell.Shell` | Service shell (runs `.svc` scripts) |
| `nexus` | `HCore.Packages.Nexus.Nexus` | AFCP Nexus Connector (networking) |
| `lidar` | `HCore.Packages.Sensor.Lidar` | Demo lidar sensor (data producer) |
| `usb` | `HCore.Packages.Usb.Usb` | Demo USB controller |

## Agent System

The agent runs as a **one-shot interactive command** spawned by the shell (under `init/console`). It is **not** a registered service — `service stop agent` will not affect it.

### Configuration

- **File**: `/etc/agent/config.json`
- **API Key**: `/etc/agent/apikey`
- **Current model**: `deepseek-chat` (via Deepseek API)
- **Hot-reload**: Changing `model` in `config.json` takes effect on the next message — no restart needed.
- **Model switch command**: `/model <name>` (user-facing)

### Tools Available to the Agent

1. **`vfs_list(path)`** — List directory contents in the VFS
2. **`vfs_read(path)`** — Read a file from the VFS
3. **`vfs_write(path, content, append?)`** — Write/create/append files in the VFS
4. **`shell(command)`** — Run HShell command lines (spawn, run, kill, service, hpm, ls, cat, mkdir, etc.)

### Key HShell Commands

| Command | Description |
|---------|-------------|
| `ls [path]` | List directory |
| `cat <file>` | Read file |
| `mkdir/rmdir/rm/mv/touch/write/append` | File operations |
| `exists <path>` | Check if path exists |
| `spawn <module> <instance>` | Create a module instance (does not run) |
| `run <instance>` | Run a spawned instance |
| `kill <instance>` | Stop a running instance |
| `service <start\|stop\|restart\|status\|list> [name]` | Manage services |
| `hpm install\|list\|remove\|pack` | Package manager |

## Services (`/etc/services/`)

- **`demo.svc`** — TestDemo inter-module call demo (commented out)
- **`sensor.svc`** — Starts the lidar sensor producer
- **`usb.svc`** — Starts the USB demo controller

## Packages Installed

- `HCore.Packages.Agent` — The LLM agent module
- `HCore.Packages.HInit` — System init
- `HCore.Packages.Hpm` — Package manager
- `HCore.Packages.HShell` — Interactive shell
- `HCore.Packages.Nexus` — AFCP networking
- `HCore.Packages.Sensor` — Lidar sensor demo
- `HCore.Packages.TestDemo` — Inter-module call demo
- `HCore.Packages.Usb` — USB controller demo

## Architecture Notes

- Modules implement interfaces (e.g., `ILidar`, `IUsb`, `IShell`, `IAgent`) and are registered via `manifest.json`.
- The `mpd` file in each package directory points to the main DLL.
- AFCP (Nexus) enables remote method calls across module boundaries.
- The agent is stateless per turn — context is maintained by the LLM conversation history.
