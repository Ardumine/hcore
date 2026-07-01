# HCore — Design & Rationale

This document captures the *why* behind HCore: the questions that shaped the design and the answers the implementation settled on. It complements [ARCHITECTURE.md](ARCHITECTURE.md) (the *how*) and the [Module Authoring Guide](MODULE_AUTHORING.md) (the *do*).

## What is HCore?

> HCore is a .NET microkernel that hosts independently-loaded modules and brokers references between them. The virtual file system addresses *modules* (the things you talk to); a module calls another by obtaining a reference and invoking typed members on it. **The path identifies _who_ you talk to — never _what_ you say, nor _what_ comes back.**

In one line: a capability-brokering module host where the filesystem tree is the *addressing* layer and method calls are the *invocation* layer — and the two are deliberately never the same axis.

## The question that shaped everything: "where does the path end?"

The original design block: Module1 exposes `Func1`, Module2 wants to call it. So…

- Should there be a path `/modules/mod1/Func1`?
- If `Func1` returns a string, is there `/modules/mod1/…/ASimpleString/Split`?
- Where does it end? Is the path infinite?

### Answer: separate addressing from invocation

The fear of an infinite path comes from making *one* path segment do *three* unrelated jobs. Split them:

| Job | Question | What it is | Where it lives |
|-----|----------|------------|----------------|
| **Addressing** | *who am I talking to?* | a path / name | ends at the **module** |
| **Invocation** | *what am I asking it to do?* | a method call + arguments | a message, not a path |
| **Result** | *what did it answer?* | data | the caller's local value |

`/proc/Module1` is the address of a live module. `Func1` is a **message** you send it, not a place. The `string` it returns is **data** — calling `.Split` on it is your own local C# on your own stack; HCore never sees it. A method result never becomes a node, so `ASimpleString/Split` never exists.

**The rule:** the namespace contains only entities something deliberately *published*. It is not the transitive closure of everything reachable through them. The path ends where you stop registering. Every serious system draws this same line — D-Bus (object path vs. interface member), Plan 9 (a file vs. operations on it), capability systems (a reference vs. invoking it), Erlang (a process vs. the message it receives).

## "But if the path stops at the module, doesn't the caller still need the interface?"

Yes — and that's fine, because of one fact in the loader: the shared contract assembly `HCore.Modules.Base` (and any interface assembly placed there) keeps **the same type identity across every `AssemblyLoadContext`** (see [Assembly Isolation](ARCHITECTURE.md#assembly-isolation)). So `IModule1` as seen by Module2 is the *same* `Type` that Module1 implements, even though the two are loaded in isolation.

That makes the **typed path** work:

```csharp
Host.GetModuleInterface<IModule1>("/proc/module1").Func1();
```

Compile-time-checked, ordinary C# dispatch, zero overhead. The caller imports `IModule1` *if it wants type safety*. A future *dynamic* path (below) removes even that requirement.

## "How is a module 'in' the VFS? Shouldn't it be `/proc/Module1`?"

Right instinct. There are **three** levels, and they map onto Unix exactly:

| Level | VFS location | Unix analogy | Backed by |
|-------|--------------|--------------|-----------|
| **Pack** | `/packs/<pack>/` | an installed package on disk | real files (`HostFileSystem`) |
| **Module** (the program) | a descriptor inside the pack DLL | `/bin/sleep` | `IModuleDescriptor` |
| **Instance** (the process) | `/proc/<name>/` | a PID | `ModuleHost` |

`/packs` is *installed* code already on disk. `/proc` is a **live view** of *running* instances, rebuilt on every access — a module appears only once it has actually been created, exactly like a process. And a method is still never a path segment: you `ls /proc` to discover *who*; you call through the host to do the *what*.

## "When creating a running module, you give two paths — the program and the instance — and they should differ, right?"

Correct, and the reason is the key insight: **one program can run as many instances.** Like `exec("/bin/sleep")` → PID 4321:

- the **source** identifies *which module* (a descriptor name — the program);
- the **instance name** is the *running identity* in `/proc`.

A pack is **not** a module: one pack DLL can contain many module descriptors (the demo's `TestDemo` pack holds both `Module1` and `Module2`). So you instantiate a *module*, not a pack folder.

Two system calls express this:

- `Spawn<T>(module, instance)` → **create** a new named instance (a process), without running it. `Spawn` the same module twice → two independent objects at `/proc/a` and `/proc/b`. This is the only call that resolves the concrete implementation type.
- `GetModuleInterface<T>(instancePath)` → **look up** an already-running instance by its `/proc` path (e.g. `/proc/module1`). It never creates anything — a caller holding only an interface and a path could not construct the implementation anyway.

Disambiguation rule: `Spawn` creates and names; `GetModuleInterface` only finds something already running. Creation and lookup are kept as separate operations.

## "What about system calls — user space vs. kernel space?"

HCore already enforces this boundary:

- **Kernel space** = `HCore.Main` — the real `FileSystem`, the `ModuleHost`, the loaders, the mount table. Modules may never reference it.
- **User space** = the packages — isolated load contexts that reference only `HCore.Modules.Base`. No ambient kernel access.

The **only** bridge is the set of kernel-implemented interfaces the kernel *injects* into each module:

| Module sees (user space) | Kernel provides | Syscall group |
|--------------------------|-----------------|---------------|
| `Vfs : IModuleFileSystem` | `ModuleFileSystemProxy` → `FileSystem` | file calls |
| `Host : IModuleHost` | `ModuleHost` | process / IPC calls |

Calling a method on `Vfs`/`Host` **is** a syscall; the injected handles **are** the module's capabilities; it cannot reach anything it was not handed. Adding a kernel capability is literally "add a method to `IModuleHost`, implement it in `ModuleHost`" — which is exactly how `Spawn` was added.

## "The timer example — what if a module doesn't know the other's interface, and a module only exposes certain parts?"

This is the **dynamic** path, and it's the planned next layer. It does not replace the typed path; it sits beside it, exactly as D-Bus typed proxies sit over a dynamic message bus.

- A module marks the members it is willing to expose with an `[Exposed]` attribute — a small capability boundary, so only published members are reachable.
- Callers invoke by name *without importing the interface*: `Host.Call("timer", "Now")`, getting back data.

That gives the decoupling of message-passing systems (Erlang, Smalltalk, Objective-C) and the introspectable-substrate model of D-Bus / gRPC-reflection, while the typed `GetModuleInterface<T>` stays the ergonomic default. See *Future Work*.

## "How does a module own child sub-modules (e.g. a USB controller owning its device ports)?"

Built: **approach D — the parent is a host for its own subtree.** A parent extends `ContainerImplement` and calls one verb, `SpawnChild<UsbDeviceImplement>("device0", d => d.Init(...))`; the kernel constructs the child, wires its `Vfs`/`Host`/`InstanceName`, runs `init` *before* publishing, places it at `/proc/usb/device0`, and records a **parent→child edge** (`RunningInstance.ParentName`). Destroying the parent **structurally reaps** the whole subtree — no author teardown code.

Why D over the alternatives (host-managed flat keys / owned-and-served pull / returned handles): only D makes lifetime-coupling a *kernel guarantee* while keeping children real, stateful, and `/proc`-addressable — and it yields the *simplest* author code (one verb; the complexity lives in the kernel, paid once). The 2nd iteration (`Ardumine/kernel`) built the flat-keys-with-an-edge variant, and its own logs document the failures D removes (orphaned children, spawn-then-init, unenforced ownership).

Note: `expose` (which members a module publishes) and `sub-modules` (child instances it owns) are kept strictly separate. Full spec, build plan, acceptance criteria, the four-way debate, and implementation notes: see **[MODULE_HIERARCHY.md](MODULE_HIERARCHY.md)**; kernel mechanics in **[ARCHITECTURE.md → Module Hierarchy](ARCHITECTURE.md#module-hierarchy--sub-modules)**.

## Future work (designed, not yet built)

Each is an additive layer, not a rewrite:

1. **Dynamic invocation** — `Host.Call(name, member, args)` + an `[Exposed]` attribute. Call without the interface; expose only chosen members.
2. **Full process lifecycle** — `kill` (cascade reap) is built (see *Module hierarchy* above); `exit`/self-reap is not — a module still cannot signal its own completion and be cleaned up automatically when `Run()` returns.
3. **Service bootstrap** — an `/etc/services` directory holding startup scripts that the shell executes at boot to spawn the service modules other modules depend on, so consumers can rely on well-known instances (e.g. `/proc/module1`) already existing. Today services must be spawned manually before a consumer can look them up.
4. **Shell as its own module** — make the shell a separate module from the init process. Right now HInit *is* the init process (PID 1, `/proc/init`); the long-term idea is a minimal init that launches the shell as a distinct module.
5. **`ctl` / `data` file invocation** — drive a module by writing to `/proc/<name>/ctl` (the Plan 9 model), making modules scriptable straight from the shell.
6. **Out-of-process / remote modules** — would add a serialization layer; the typed proxy could then sit over a message transport unchanged.
7. **Capability model for `Kill`** — today `IModuleHost.Kill` is privileged and unrestricted (any holder of a `Host` can kill any instance by path); only `KillChild` is owner-scoped. A real fix needs a permission layer that doesn't exist yet.

## Prior-art touchstones

The design borrows deliberately:

- **D-Bus** — path / interface / member; results are data, references are explicit (`o` type).
- **Plan 9 / 9P** — everything-is-a-file; a leaf bit bounds depth; `/proc`, `ctl`/`data`.
- **Capability systems** (seL4, Cap'n Proto, E) — a reference *is* authority; reachability, not string depth, bounds the namespace.
- **OSGi / COM / gRPC reflection** — a shared *contract*, looked up by identity, yields a typed handle.
- **Erlang / Actor model** — address a receiver; the operation is data the receiver dispatches.
