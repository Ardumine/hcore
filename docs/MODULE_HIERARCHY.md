# Open Design Question — Module Hierarchy & Sub-Modules

**Status:** ✅ DECIDED 2026-06-30 — **approach D (sub-host, via `ContainerImplement` + `SpawnChild`)**. Full spec in *Chosen design (D)* below. Not yet implemented (deferred; current build unaffected).
**Scope:** future work. The current build (flat `/proc`, `Spawn` + path-based `GetModuleInterface`) already works and ships without this.
**Related:** [ARCHITECTURE.md](ARCHITECTURE.md), [DESIGN.md](DESIGN.md).

---

## The problem

We want modules to form a **tree**: a parent module that *contains child module instances*, nested in the VFS. Today `/proc` is flat — every running instance is a top-level entry. The question is how a parent should own and present its children.

### Worked example

```
/proc/usb/                     ← the USB controller module (a parent)
├── info                       ← its own facet file (a leaf)
├── ctl                        ← (future) write commands to it
├── device0/                   ← a CHILD module — a plugged-in device
│   ├── info
│   └── endpoint0/             ← children can have children
└── device1/                   ← another child module
    └── info
```

Each `deviceN` is itself a **real module instance**: its own interface (`IUsbDevice`), its own `info`, callable (`GetModuleInterface<IUsbDevice>("/proc/usb/device0").Read()`), and possibly its own children.

---

## Key distinction (do not conflate)

A module has **three separate things**; this question is about the third only.

| Thing | What it is | Example node |
|---|---|---|
| **Exposed interface** | callable members of *this* module (its API surface — the `[Exposed]`/`ctl` axis) | `/proc/usb/ctl` |
| **Own facets** | info/status about *this* module | `/proc/usb/info` |
| **Sub-modules** ← *this question* | child module **instances** the parent owns | `/proc/usb/device0/` |

"Expose" = which of my members are callable. "Sub-modules" = which child instances I contain. **Different axes.**

---

## Why this is the same bounding rule — not infinite recursion

The original fear (`/proc/m1/Func1/ASimpleString/Split/…`, infinite) was resolved by one rule: **a node exists only when something explicitly publishes it.** A method's return value is never published, so it never becomes a node.

That same rule makes the device tree *legitimate and finite*: a `device0` node exists **because the USB module explicitly published a child** when a real device appeared. Depth = the real structure (controller → device → interface → endpoint → leaf); it bottoms out at real leaves. Not infinite — *as deep as reality, never one node deeper.*

---

## Constraints (from HCore's architecture)

- **Kernel/user boundary.** User-space modules (`HCore.Packages.*`) may reference **only** `HCore.Modules.Base`, never `HCore.Main`. So a module **cannot** implement the kernel's `IVirtualFileSystem`/`IVirtualDirectory` (those live in `HCore.Main`).
- **The bridge pattern.** Letting a module serve a subtree therefore needs a **Base-side contract** + a **kernel-side adapter** — the same shape as `IModuleFileSystem`→`ModuleFileSystemProxy` and `IModuleHost`→`ModuleHost`.
- **Mount delegation already exists.** `FileSystem` resolves by longest-prefix, so a provider mounted at a prefix already receives all sub-paths — the routing substrate for delegation is present.

---

## The core sub-question

When you `ls /proc/usb`, where do the entries come from, and who holds the child objects?

- **Created** — the parent imperatively spawns/kills child instances; the **kernel** stores and renders them.
- **Owned-and-served** — the **parent** owns its subtree and answers listing/resolution on demand (kernel delegates to it).

…and the question that matters most:

> **What does this look like in the module author's implementation code — and does it get confusing?**

---

## Candidate approaches (being debated)

1. **Owned-and-served (module is a file server).** Module implements a new Base-side `IModuleDirectory` (`ListChildren()` / `ResolveChild(name)`); a kernel adapter mounts it at `/proc/usb`. Listing delegated (pull); child instance materialized lazily on resolve. *(Plan 9 model.)*
2. **Host-managed (imperative spawn/kill).** Module calls `Host.Spawn<IUsbDevice>("UsbDevice","usb/device0")` on plug, `Host.Kill("usb/device0")` on unplug. Kernel owns a path-keyed table; `ProcFileSystem` splits keys into a tree.
3. **Returned references (no VFS nodes).** No `/proc/usb/deviceN`. The Usb module's own interface returns child handles (`IReadOnlyList<IUsbDevice> Devices()` / `IUsbDevice Open(int port)`). The object graph carries the hierarchy; `/proc` may show a read-only human listing but isn't the addressing mechanism. *(D-Bus `GetManagedObjects` / capability style.)*
4. **Sub-host hybrid.** Module holds its own child registry (a mini-`ModuleHost`) — children are real owned instances — *and* implements the directory contract so they render under `/proc/usb`. *(Recursive: a parent is, for its subtree, what the kernel `ModuleHost` is for the top level.)*

---

## Evaluation criteria

1. **Module-author ergonomics** *(primary)* — how much/confusing is the code an author writes?
2. Architectural soundness & consistency with "everything is a file".
3. Dynamic plug/unplug; lazy child lifecycle (ties into the not-yet-built `kill`/reap).
4. Type identity across `AssemblyLoadContext`s when calling a child.
5. Kernel complexity (path-walk / delegation).

---

## Debate results

*Produced 2026-06-30 by a 9-agent debate: each approach was championed with full `UsbModule` author-code, adversarially critiqued on author-ergonomics, and scored (combined ergonomics + soundness, 1–10); a moderator synthesized.*

| Approach | Who holds the child | Score | One-line verdict |
|---|---|---|---|
| **B. Host-managed** (spawn/kill) | kernel's flat `_instances` (slash keys) | **6** | Smallest delta, clearest to read — but no real parent↔child edge, ALC-fragile init cast, namespace is unowned strings. |
| **D. Sub-host hybrid** | parent's own mini-host (2nd table) | **6** | Right shape for stateful callable children; most invasive kernel change; needs a `ContainerImplement` base to be ergonomic. |
| **A. Owned-and-served** (Plan 9) | parent field (+ kernel index) | **5** | Elegant pull-based `/proc`-for-a-subtree — but `new` + mandatory `AttachChild` leak the kernel boundary; torn dual-ownership. |
| **C. Returned handles** | consumer holding the reference | **5** | Prettiest happy path — but child interfaces in the package break cross-package use (usable only as empty `IModule`). |

### Verdict & recommendation

**Ship-now pick: B (host-managed) — but only with a kernel-side salvage, never as first drawn.** It is the smallest step from today's code and keeps a child a 100% ordinary module, *if* the two sharp edges are removed by the kernel.

- **A** is the better long-term fit if children must be **lazily materialized** and hot-plug reflected with zero sync code (most consistent with HCore's existing pull-based `/proc`).
- **D** fits **stateful, individually-callable children that have their own subtrees** — behind a `ContainerImplement` base.
- **C** is **ruled out** for cross-module hierarchy unless *all* child interfaces are mandated into `HCore.Modules.Base`.

### Decision (2026-06-30): CHOSEN → **D**

Added hard requirements shift the goal from "smallest delta to ship" to "right model for these needs":

1. Children are **real, stateful modules** (own serial, location, …), referenced by **type**, not a magic name string.
2. The **parent creates** each child and **passes it its data** at creation.
3. **Lifetime is coupled:** destroy the parent → its children are reaped automatically.
4. Optimize for **module-developer experience**.

Requirement 3 is decisive — it is the property only **D** guarantees: **B** orphans children (no parent→child edge); **A** GC-collects children but leaves the kernel's `/proc` index to clean up by hand; **C** cascades cleanly via GC **but** children never appear in `/proc`; **D** ties each child to the parent's sub-host, so the kernel reaps the whole subtree on parent death while children stay real, stateful, and addressable in `/proc`.

**On DX (the optimization target):** D's *module-author* code is the simplest of all four — one verb behind a `ContainerImplement` base:

```csharp
SpawnChild<UsbDeviceImplement>("device0", d => d.Init("SN-A", "1-1.2"));
```

No `new`, no `Vfs`/`Host` wiring, no name strings, no teardown. D's *complexity* lives in the **kernel** (parent→child edges, cascade reap, nested `/proc` walk) — paid **once** by the kernel author, never by module authors. That is the correct place for it, and the same trade HCore already makes with `Vfs`/`Host` injection.

**Caveat (kept as a build constraint):** D's good DX is entirely contingent on the `ContainerImplement` + `SpawnChild` abstraction being built well — exposing the raw sub-host would wreck it. (If the `/proc`-visibility requirement were ever dropped, **C** would match D on author DX with far less kernel work — but `/proc` visibility is a requirement, so this stays D.) **Decision locked: D** (2026-06-30). Build spec below; no code written yet.

### The one decision under all four

Every approach gets confusing for the **same** reason, and is fixed the **same** way: **let the kernel construct and wire children** (not the author). Concretely:

- Add `T SpawnChild<T>(string moduleName, string leafName, Action<T>? init = null)` to `IModuleHost`, plus an injected `InstanceName` on `BaseImplement`.
- The kernel prepends the caller's own instance name (→ no magic `"usb/device0"` string, no squatting), runs the existing `Create` path (→ each child gets its **own** `ModuleFileSystemProxy`/working-dir and `Host`, not the parent's), records a real **parent→child edge** (→ cascade-kill for free), and runs `init` **before** publishing (→ no half-built node visible in `/proc`; the fragile `IUsbDeviceInit` cast disappears).
- Hide it behind a `ContainerImplement : BaseImplement` base so a container collapses to one familiar verb.

Net: the author **never** `new`s an implementation, **never** hand-wires `Vfs`/`Host`, and **never** authors the namespace as a raw string — exactly the confusion that prompted this question.

### Cross-cutting hazards (true regardless of approach)

- **Child interfaces MUST live in `HCore.Modules.Base`.** Put `IUsbDevice` in the package and a cross-package consumer can only hold it as the empty `IModule` marker (different `AssemblyLoadContext` = different `Type`); `dev.Read(...)` won't compile. This sinks C as drawn and is a latent bug in B's `IUsbDeviceInit`.
- **Spawn-then-init race:** registering an instance before initializing it briefly publishes a half-built node in `/proc`. The `init`-before-publish overload closes it.
- **Listing vs calling can diverge** (A): a child may drop from the listing but stay callable via a stale cached reference (or vice-versa) — the symptom of two registries.

## Chosen design (D) — specification

*Decision locked 2026-06-30. Not yet implemented; this is the build spec.*

### Goal
A parent module owns child module instances that:
- are **real, stateful modules** (own data: serial, location, …);
- are **created by the parent**, which passes each child its data;
- appear in the namespace at `/proc/<parent>/<child>` and are **callable from anywhere**;
- have **lifetime coupled to the parent**: destroying the parent reaps all descendants, automatically and structurally.

…with a **one-verb author API**, the kernel doing all the wiring.

### Author-facing surface (in `HCore.Modules.Base`)

`ContainerImplement : BaseImplement` — base class for any module that owns children. It hides the sub-host entirely and exposes only:

```csharp
public abstract class ContainerImplement : BaseImplement
{
    // Create a child, run its init BEFORE it is published, return its interface.
    protected T SpawnChild<T>(string name, Action<T>? init = null) where T : IModule;
    protected void KillChild(string name);   // optional; cascade handles the rest
    // The kernel reads the owned children through this base — the author never implements it.
}
```

- **Child interfaces MUST live in `HCore.Modules.Base`** (or a shared contract assembly). Otherwise a consumer in another package can only hold the child as the empty `IModule` marker (different `AssemblyLoadContext` = different `Type`). **Hard rule** — the one cross-cutting constraint from both the debate and the 2nd-iteration analysis.
- The author never calls `new`, never wires `Vfs`/`Host`, never writes teardown code.

### Canonical author example

```csharp
// HCore.Modules.Base
public interface IUsbDevice : IModule { string Serial { get; } string Location { get; } byte[] Read(int len); }

// HCore.Packages.Usb
public sealed class UsbDeviceImplement : BaseImplement, IUsbDevice
{
    public string Serial   { get; private set; } = "";
    public string Location { get; private set; } = "";
    internal void Init(string serial, string location) { Serial = serial; Location = location; }
    public byte[] Read(int len) => /* talk to the real device */ new byte[len];
}

public sealed class UsbModuleImplement : ContainerImplement, IUsb, IRunnable
{
    public void Run()
    {
        SpawnChild<IUsbDevice>("device0", d => ((UsbDeviceImplement)d).Init("SN-A", "1-1.2"));
        SpawnChild<IUsbDevice>("device1", d => ((UsbDeviceImplement)d).Init("SN-B", "1-1.3"));
    }
    // No teardown — killing this module reaps device0/device1 automatically.
}
```

Resulting tree:

```
/proc/
└── usb/
    ├── info
    ├── device0/   (Serial=SN-A, Location=1-1.2)
    │   └── info
    └── device1/   (Serial=SN-B, Location=1-1.3)
        └── info
# kill /proc/usb → device0 and device1 vanish with it (kernel-enforced cascade)
```

### Kernel-side mechanics (`HCore.Main`)

On `SpawnChild`:
1. Resolve the child descriptor (→ `ImplementType`).
2. `Activator.CreateInstance`; `AttachVfs` (its **own** `ModuleFileSystemProxy`/working dir); `AttachHost`; set `InstanceName = "<parent>/<child>"`.
3. Run `init(child)` — **before** registering/publishing (no half-built node).
4. Register the instance and record a kernel-owned **parent→child edge** (conceptually the parent owns a sub-host of its children).
5. Return the typed interface.

- **Cascade (structural):** killing/destroying any instance recursively reaps every descendant via the parent edge — no author code, no "doing it manually" warnings.
- **Ownership enforced:** only the owning parent may stop/kill a child (closes the 2nd iteration's unfinished `//TODO: check the module is a sub-module of the parent`; blocks path-squatting).
- **`/proc`:** `ProcFileSystem` nests instances by the parent edge so `/proc/usb/device0/info` renders; recursive for grandchildren.
- **Addressing:** `GetModuleInterface<T>("/proc/usb/device0")` already takes a path — extend resolution to the nested composite key.

### Acceptance criteria (design these out — from the 2nd iteration's scars)
1. **Structural cascade:** destroy parent ⇒ all descendants reaped automatically (no warnings, no author teardown).
2. **Init-before-publish:** a child is never visible/callable in `/proc` until its `init` has run.
3. **Enforced ownership:** only the owning parent may stop/kill a child.
4. **Single authoritative store:** the parent→child edge is kernel-owned; `/proc` and `GetModuleInterface` derive from it (no drift between two tables).
5. **Cross-package safe:** child interfaces in Base; a different package can call the child.
6. **One-verb author API:** `SpawnChild` only — no `new`, no service wiring, no name strings, no teardown.

### Build plan (small, behind the current build)
1. `ContainerImplement` + `SpawnChild`/`KillChild` in Base (+ the kernel-read child enumeration the base auto-implements).
2. Kernel: parent→child edge + structural cascade reap + ownership check + init-before-publish in the create path.
3. `ProcFileSystem`: nested rendering; `GetModuleInterface`/`InstanceNameFromPath`: nested-path resolution.
4. Shell: `kill <instance>` (cascade); `spawn` stays create-only.
5. Demo: `UsbModuleImplement` + `UsbDeviceImplement`; verify `ls /proc/usb`, `cat /proc/usb/device0/info`, and that `kill /proc/usb` reaps both.

### Open questions to finalize at build time
- **`SpawnChild` by type vs name:** a concrete-type form `SpawnChild<UsbDeviceImplement>(name, init)` (clean & type-safe for in-package children, and lets `init` receive the concrete type — removing the `((UsbDeviceImplement)d)` cast) vs a descriptor-name form (required for cross-package). Likely offer both.
- **Sub-host shape:** a literal per-parent `ModuleHost` instance vs a `parentInstanceName`/edge field on the existing flat table. Both satisfy the acceptance criteria; the literal sub-host is conceptually cleaner, the flat+edge is less code.
- **Init ergonomics:** prefer the typed `SpawnChild<TImpl>` above, or an `IInitializable<TArgs>` contract, to avoid the downcast in the example.

## Pros & Cons (per approach)

### A. Owned-and-served (module is a file server) — score 5
**Pros:** always-live by pull (no stale `/proc`, hot-plug "just works"); children cost nothing until opened (lazy); author writes just `ListChildren()` + `ResolveChild()`; recurses for free; faithful clone of the existing `IModuleFileSystem`/`ProcFileSystem` patterns.
**Cons:** `new` + mandatory `AttachChild` leak the kernel boundary (forget it → "VFS not attached" on first call); dual ownership (parent field + kernel index can drift); no hot-plug events (poll only); `ListChildren`/`ResolveChild` run on the kernel's thread (a slow/throwing bus scan stalls/breaks `ls`); composite-path addressing forces `InstanceNameFromPath` changes.

### B. Host-managed (imperative spawn/kill) — score 6
**Pros:** smallest infra (+1 `Kill`, ~25 kernel lines, reuses `Spawn` verbatim); child is a 100% ordinary module; single source of truth (one kernel table — `/proc`, `GetModuleInterface`, future `ps` all agree); hardware semantics map cleanly (spawn = arrived, kill = removed); children globally addressable with no new API.
**Cons:** hierarchy is an *unenforced naming convention* (anyone can squat `usb/...`); no parent↔child link (kill parent → orphans; manual cascade); author hand-rolls unique names; **spawn-then-init** wart (half-built device + ALC-fragile cast); a cross-module holder keeps a dangling reference across unplug.

### C. Returned references (no VFS nodes) — score 5
**Pros:** near-zero kernel change; ordinary idiomatic C# (a field + a factory + `new`); strongly typed end-to-end (no stringly-typed paths); free uniform nesting; capability-secure (no ambient global path); matches D-Bus `ObjectManager` / Cap'n Proto.
**Cons:** **breaks cross-package** (child interface not in Base → consumer sees only empty `IModule`); children invisible to `/proc`/`ls` by default; two mental models for "get module X" (path vs navigate); no global address for a child; children miss kernel lifecycle services unless hand-wired; GC-driven lifetime surprises.

### D. Sub-host hybrid (parent holds a child registry + serves it) — score 6
**Pros:** real ownership (parent holds typed child refs — can call/restart/dispose); conceptually uniform/recursive ("a parent is the host of its subtree"); pure pull (truthful `/proc`); small Base surface (one `IModuleTree` + one host call); children are normal registered modules (descriptors, injection, ALC rules intact).
**Cons:** fuses API + container + sub-host in one class (re-blurs expose vs sub-modules); **most invasive** kernel change (multi-segment path walk, recursive `/proc`, parent→sub-host back-refs); a second process-table source of truth (can drift); per-parent boilerplate (begs for `ContainerImplement`); impl downcast to configure a child leaks across packages.

---

## Prior art — how the 2nd iteration (`Ardumine/kernel`) did it

The second iteration (predecessor to this repo) **already built sub-modules — and the example was literally a USB controller owning ports** (`Modules.USB`: `USBController` + `USBPort`).

- **Addressing:** by VFS-like path string (`GetModuleInterface<IModule1>("/modules/mod1")`), dispatched through a `DispatchProxy` over a **flat, path-keyed kernel table** (`ChannelManager.Channels`, `ModuleManager.LocalRModules`). Slashes carried no structural meaning — pure naming convention. (= approach **B**'s addressing.)
- **Hierarchy:** the parent called `CreateSubModule<IUSBPort>("/modules/USBController/Port1", "USBPort")` / `DeleteSubModule(...)`; the kernel recorded a real **`parent.SubModules` edge** (`SubModuleDescriptor.ParentModule`), but children lived in the *same flat table* as everything else. → **B with a thin ownership edge bolted on** — D in intent, not in structure (no sub-host, no served subtree).

**It hit exactly the problems D is meant to solve — and the code documents them:**

1. **Cascade was best-effort, not structural.** `ModuleDescriptor.Stop` / `ModuleManager.DeleteModule` *warn* "parent didn't stop/delete its child, doing it manually" and force-clean as a safety net → direct evidence that **author-driven teardown is unreliable** → make cascade **structural** (D).
2. **Spawn-then-init wart was real:** the USB child was `Start()`ed, *then* `SetName(...)` was called — a half-built published child → validates **`SpawnChild<T>(…, init)` running init before publish.**
3. **Ownership was unenforced:** `//TODO: check the module is a sub-module of the parent` on start/stop/delete → validates **kernel-enforced ownership** (D's "parent *is* the host" makes it natural; B's global slash-keys invite squatting).
4. **Two sources of truth** (flat table + `SubModules` edge) risked drift — a cost **both** B and D pay, so not a reason to prefer B.

**Net:** iteration 2 is a B-with-ownership-edge prototype that already ran into orphaning, spawn-then-init, and unenforced ownership — strong corroboration for moving B → **D**, and a concrete "design these out" checklist for iteration 3.

## Related open items (future work)

- Process lifecycle: `kill`/exit/reap (instances are currently never removed).
- `/proc/<m>/ctl` invocation (Plan 9 `ctl` model) and an `exposed` file listing callable members.
- `/etc/services` bootstrap scripts run by the shell.
- Shell as its own module, separate from the init process.
