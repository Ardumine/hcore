# API Reference

## HCore.Modules.Base

This is the shared contract library that all modules reference. It defines the interfaces and base classes that module authors use.

---

### IModule

```csharp
namespace HCore.Modules.Base;

public interface IModule { }
```

Marker interface. The root of all module interfaces. Every module's public interface must extend this.

---

### IRunnable

```csharp
namespace HCore.Modules.Base;

public interface IRunnable : IModule
{
    void Run();
}
```

Extends `IModule` with an entry point. Modules implementing this can be executed by the kernel.

| Member | Description |
|--------|-------------|
| `Run()` | Entry point invoked by the kernel. Execution blocks until this method returns. |

---

### IModuleDescriptor

```csharp
namespace HCore.Modules.Base;

public interface IModuleDescriptor
{
    string Name { get; }
    string FriendlyName { get; }
    Type ImplementType { get; }
    Type InterfaceType { get; }
}
```

Metadata that tells the kernel how to instantiate a module. Each module must have exactly one descriptor class implementing this interface.

| Member | Description |
|--------|-------------|
| `Name` | Unique module identifier (e.g., `"MyCompany.MyModule"`). Used by the kernel to look up modules. |
| `FriendlyName` | Human-readable display name (e.g., `"Cool Video Downloader"`). |
| `ImplementType` | The `Type` of the class that implements the module's logic. Must extend `BaseImplement` and implement `InterfaceType`. |
| `InterfaceType` | The `Type` of the interface that defines the module's public API. Must extend `IModule`. |

---

### BaseImplement

```csharp
namespace HCore.Modules.Base;

public abstract class BaseImplement : IModule
{
    public IModuleFileSystem Vfs { get; private set; }
    public void AttachVfs(IModuleFileSystem vfs);

    public IModuleHost Host { get; private set; }
    public void AttachHost(IModuleHost host);

    public string InstanceName { get; private set; }
    public void AttachInstanceName(string name);

    protected internal virtual void OnKilled();
    protected internal virtual string? DescribeForProc();
}
```

Abstract base class that all module implementations must extend. Provides the two kernel "system-call" surfaces (`Vfs`, `Host`), this instance's own `/proc` identity (`InstanceName`), and two lifecycle hooks the kernel calls directly.

| Member | Description |
|--------|-------------|
| `Vfs` | The virtual filesystem proxy injected by the kernel. Initially a null-object that throws on any operation. |
| `AttachVfs(vfs)` | Called by the kernel to inject the module's filesystem proxy. Throws `ArgumentNullException` if `vfs` is null. |
| `Host` | The module host injected by the kernel — the module's gateway to other modules. Initially a null-object that throws on any operation. Every created instance actually receives a `ScopedModuleHost` bound to its own instance name, not the raw kernel object. |
| `AttachHost(host)` | Called by the kernel to inject the module host. Throws `ArgumentNullException` if `host` is null. |
| `InstanceName` | This instance's own `/proc` identity (e.g. `"usb/device0"`). Kernel-injected, like `Vfs`/`Host`. |
| `AttachInstanceName(name)` | Called by the kernel at creation, exactly like `AttachVfs`/`AttachHost`. |
| `OnKilled()` | Called by the kernel when this instance is reaped — killed directly, or as part of a parent's cascade. Override to release resources; default is a no-op. Runs *after* the kernel releases its process-table lock, leaf-first across a cascade. `protected internal` — see [Module Hierarchy](MODULE_HIERARCHY.md) for why only the kernel or a subclass can call it. |
| `DescribeForProc()` | Module-authored extra lines shown in this instance's `/proc/<name>/info` (e.g. a device's serial/location). Default `null` (no extra lines). Same access as `OnKilled()`. |

**Usage:** Module authors extend this class and use `Vfs` and `Host`. The kernel calls `AttachVfs()`, `AttachHost()`, and `AttachInstanceName()` before invoking any module methods. Override `OnKilled()`/`DescribeForProc()` only if your module needs cleanup or wants extra `/proc` info — most authors extend `ContainerImplement` instead of `BaseImplement` when they need children.

---

### ContainerImplement

```csharp
namespace HCore.Modules.Base;

public abstract class ContainerImplement : BaseImplement
{
    protected TImpl SpawnChild<TImpl>(string name, Action<TImpl>? init = null) where TImpl : IModule;
    protected T SpawnChildByName<T>(string moduleName, string name, Action<T>? init = null) where T : IModule;
    protected void KillChild(string name);
}
```

Base class for a module that owns child module instances (Design D — see [Module Hierarchy](MODULE_HIERARCHY.md)). Extend this instead of `BaseImplement` when your module needs to spawn stateful children that appear nested under it in `/proc`, with lifetime structurally coupled to it.

| Member | Description |
|--------|-------------|
| `SpawnChild<TImpl>(name, init)` | Create a child of THIS instance, resolved by concrete implementation type — no `new`, no `Vfs`/`Host` wiring, no name strings, no teardown code. `init` runs before the child is visible in `/proc`. |
| `SpawnChildByName<T>(moduleName, name, init)` | Cross-package form: create a child by module name, returning its interface (which must live in a shared contract assembly). |
| `KillChild(name)` | Kill one of THIS instance's own children (and its descendants). Not required for cleanup — killing the parent already cascades. |

**Example:**

```csharp
public sealed class UsbModuleImplement : ContainerImplement, IUsb, IRunnable
{
    public void Run()
    {
        SpawnChild<UsbDeviceImplement>("device0", d => d.Init("SN-A", "1-1.2"));
        SpawnChild<UsbDeviceImplement>("device1", d => d.Init("SN-B", "1-1.3"));
    }
    // No teardown — killing this module reaps device0/device1 automatically.
}
```

---

### IModuleHost

```csharp
namespace HCore.Modules.Base;

public interface IModuleHost
{
    T GetModuleInterface<T>(string instancePath) where T : IModule;
    T Spawn<T>(string moduleName, string instanceName) where T : IModule;

    TImpl SpawnChild<TImpl>(string leafName, Action<TImpl>? init) where TImpl : IModule;
    T SpawnChildByName<T>(string moduleName, string leafName, Action<T>? init) where T : IModule;
    void KillChild(string leafName);
    void Kill(string instancePath);
}
```

The process / IPC "system-call" surface. Implemented by the kernel (`ModuleHost`) and injected into each module as `BaseImplement.Host` — in practice every instance receives a `ScopedModuleHost` facade bound to its own instance name, so `SpawnChild`/`SpawnChildByName`/`KillChild` always act on the calling module's OWN children. Lets a module reach other modules **without referencing their assemblies** — only the shared interface type is needed, and its identity is preserved across load contexts.

| Method | Description |
|--------|-------------|
| `Spawn<T>(moduleName, instanceName)` | **Creates** a new top-level named instance of a module (like `exec`), registered at `/proc/<instanceName>` but **not** run. This is the only operation that resolves the concrete implementation type by NAME (via the descriptor registry). The same module may be spawned many times. Throws if the module name is unknown, the instance name is already in use, or contains `/`. |
| `GetModuleInterface<T>(instancePath)` | **Looks up** an already-running instance by its `/proc` path (e.g. `"/proc/module1"`; a bare name like `"module1"` is also accepted) and returns it as `T`. It **never creates** anything — a caller holding only an interface plus a path cannot construct anything, and lookup needs only the interface because the object already exists. Throws if nothing is running at that path or the instance does not implement `T`. |
| `SpawnChild<TImpl>(leafName, init)` | Creates a CHILD of the calling module, resolved by concrete implementation type. Runs `init` before the child is published — never observable half-built. Appears at `/proc/<owner>/<leafName>`; destroying the owner structurally reaps it. Most authors call `ContainerImplement.SpawnChild` instead of this directly. See [Module Hierarchy](MODULE_HIERARCHY.md). |
| `SpawnChildByName<T>(moduleName, leafName, init)` | Cross-package escape hatch for `SpawnChild`: creates a child by module NAME instead of concrete type, returning the child's interface (which must therefore live in a shared contract assembly). |
| `KillChild(leafName)` | Kills a child OF THE CALLING module. Owner-scoped — throws if `leafName` is not actually this module's child. Cascades to the child's own descendants, leaf-first. |
| `Kill(instancePath)` | Privileged cascade kill of ANY instance by `/proc` path, regardless of ownership. Reaps the target and every transitive descendant, leaf-first. No capability model exists yet, so this is intentionally unrestricted — the shell's `kill` command uses it. |

All throw `InvalidOperationException` if the target is unknown, already exists, or does not implement the requested type.

---

### IModuleFileSystem

```csharp
namespace HCore.Modules.Base;

public interface IModuleFileSystem
{
    string WorkingDirectory { get; }
    string ResolvePath(string path);
    void SetWorkingDirectory(string path);
    void CreateDirectory(string path);
    Stream OpenFileStream(string path, FileMode mode = FileMode.OpenOrCreate, FileAccess access = FileAccess.ReadWrite);
    bool DeleteFile(string path);
    bool DeleteDirectory(string path, bool recursive = false);
    bool Exists(string path);
    bool Move(string sourcePath, string destinationPath, bool overwrite = false);
    bool Rename(string path, string newName, bool overwrite = false);
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IEnumerable<string> ListDirectory(string path = ".");
    string ReadAllText(string path);
    void WriteAllText(string path, string contents, bool append = false);
    void TouchFile(string path);
}
```

The filesystem interface available to modules. All paths can be absolute (starting with `/`) or relative to the working directory.

#### Properties

| Member | Description |
|--------|-------------|
| `WorkingDirectory` | The module's current working directory (absolute path within the VFS). |

#### Path Operations

| Method | Description |
|--------|-------------|
| `ResolvePath(path)` | Resolves a relative path against the working directory. Returns an absolute VFS path. |
| `SetWorkingDirectory(path)` | Changes the module's working directory. |

#### File Operations

| Method | Description |
|--------|-------------|
| `OpenFileStream(path, mode, access)` | Opens a file and returns a `Stream`. Supports standard `FileMode` and `FileAccess` options. |
| `ReadAllText(path)` | Reads the entire contents of a file as a string. |
| `WriteAllText(path, contents, append)` | Writes a string to a file. If `append` is true, appends to existing content. |
| `TouchFile(path)` | Creates an empty file if it doesn't exist. |
| `DeleteFile(path)` | Deletes a file. Returns `true` if successful. |
| `Move(source, destination, overwrite)` | Moves a file or directory. Returns `true` if successful. |
| `Rename(path, newName, overwrite)` | Renames a file or directory. Returns `true` if successful. |

#### Directory Operations

| Method | Description |
|--------|-------------|
| `CreateDirectory(path)` | Creates a directory (and parent directories if needed). |
| `DeleteDirectory(path, recursive)` | Deletes a directory. If `recursive` is true, deletes contents first. Returns `true` if successful. |
| `ListDirectory(path)` | Returns an enumerable of entry names in the specified directory. Defaults to the current working directory. |

#### Existence Checks

| Method | Description |
|--------|-------------|
| `Exists(path)` | Returns `true` if the path exists (file or directory). |
| `FileExists(path)` | Returns `true` if the path exists and is a file. |
| `DirectoryExists(path)` | Returns `true` if the path exists and is a directory. |

---

### AdamPipe\<T\>

```csharp
namespace HCore.Modules.Base;

public class AdamPipe<T>
{
    public void SendSignal(T item);
    public T Wait(CancellationToken ct = default);
}
```

A thread-safe producer-consumer signaling queue for inter-module communication. Uses `SemaphoreSlim` for blocking and a `Queue<T>` with lock for storage.

| Method | Description |
|--------|-------------|
| `SendSignal(item)` | Enqueues an item and releases the semaphore, unblocking one waiting consumer. |
| `Wait(ct)` | Blocks until an item is available, then dequeues and returns it. Supports cancellation via `CancellationToken`. Throws `OperationCanceledException` if cancelled. |

**Thread safety:** Both methods are fully thread-safe. Multiple producers and consumers can operate concurrently.

---

## VFS Interfaces (Kernel Internal)

These interfaces are used internally by the kernel's VFS implementation. Module authors typically don't interact with these directly.

---

### IVirtualFileSystem

```csharp
public interface IVirtualFileSystem
{
    string Name { get; }
    bool IsReadOnly { get; }
    IVirtualDirectory Root { get; }
}
```

Represents a mountable filesystem provider.

| Member | Description |
|--------|-------------|
| `Name` | The filesystem's identifier (e.g., `"tmpfs"`, `"devfs"`). |
| `IsReadOnly` | Whether the filesystem allows write operations. |
| `Root` | The root directory of this filesystem. |

---

### IVirtualNode

```csharp
public interface IVirtualNode
{
    string Name { get; }
    IVirtualDirectory? Parent { get; }
    string Path { get; }
}
```

Base interface for all filesystem nodes (files and directories).

| Member | Description |
|--------|-------------|
| `Name` | The node's name (filename or directory name). |
| `Parent` | The parent directory, or `null` for root nodes. |
| `Path` | The full absolute path within the filesystem. |

---

### IVirtualDirectory

```csharp
public interface IVirtualDirectory : IVirtualNode
{
    IEnumerable<IVirtualNode> Enumerate();
    IEnumerable<IVirtualNode> EnumerateDirectories();
    IEnumerable<IVirtualNode> EnumerateFiles();
    IVirtualDirectory? TryGetDirectory(string name);
    IVirtualDirectory GetDirectory(string name);
    IVirtualFile? TryGetFile(string name);
    IVirtualFile GetFile(string name);
    IVirtualNode? TryGet(string name);
    IVirtualDirectory CreateDirectory(string name);
    bool TryDelete(string name);
    IVirtualFile CreateFile(string name, bool overwrite = true, ReadOnlySpan<byte> initialData = default);
}
```

Represents a directory in the VFS.

| Method | Description |
|--------|-------------|
| `Enumerate()` | Returns all child nodes (files and directories). |
| `EnumerateDirectories()` | Returns the child directories. |
| `EnumerateFiles()` | Returns the child files. |
| `TryGetDirectory(name)` / `GetDirectory(name)` | Gets a subdirectory by name (`Try…` returns `null`; the other throws `DirectoryNotFoundException`). |
| `TryGetFile(name)` / `GetFile(name)` | Gets a file by name (`Try…` returns `null`; the other throws `FileNotFoundException`). |
| `TryGet(name)` | Gets a child node of either kind, or `null`. |
| `CreateDirectory(name)` | Creates and returns a subdirectory. |
| `TryDelete(name)` | Deletes a child node by name; returns `true` if it existed. |
| `CreateFile(name, overwrite, initialData)` | Creates a file, optionally seeded with `initialData`. |

---

### IVirtualFile

```csharp
public interface IVirtualFile : IVirtualNode
{
    Stream GetStream(FileMode mode = FileMode.OpenOrCreate, FileAccess access = FileAccess.ReadWrite);
    byte[] ReadAllBytes();
    string ReadString(Encoding? encoding = null);
    void Write(ReadOnlySpan<byte> data);
}
```

Represents a file in the VFS.

| Method | Description |
|--------|-------------|
| `GetStream(mode, access)` | Opens the file as a stream with the specified mode and access. |
| `ReadAllBytes()` | Reads the entire file content as a byte array. |
| `ReadString(encoding)` | Reads the entire file content as text (UTF-8 by default). |
| `Write(data)` | Overwrites the file content with the given bytes. |

---

### Built-in VFS Providers

The kernel ships four `IVirtualFileSystem` implementations, mounted in `Program.cs`:

| Provider | Mount | Read-only | Description |
|----------|-------|-----------|-------------|
| `HostFileSystem` | `/` | no | Maps to a real host directory (`FS/`). |
| `MemoryFileSystem` | `/tmp` | no | In-memory; contents are lost when the process exits. |
| `DeviceFileSystem` | `/dev` | yes | Synthetic device files. |
| `ProcFileSystem` | `/proc` | yes | Live view of running module instances, nested by parent→child (rebuilt on every access from the `ModuleHost`). |

---

## Logyt

### ConsoleLogyt

```csharp
namespace Logyt;

public class ConsoleLogyt : TextWritterLogyt
{
    public ConsoleLogyt(string description);
}
```

Creates a logger that outputs color-coded messages to the console.

| Color | Message Type |
|-------|-------------|
| Cyan | Info |
| Yellow | Warning |
| Red | Error |
| Dark Red | Critical |
| Gray | Debug |

### Log Methods (inherited from Logyt base)

| Method | Description |
|--------|-------------|
| `I(message)` | Log an info message |
| `W(message)` | Log a warning message |

### Output Format

```
<TypeLetter>[<Timestamp> <Description>] <Message>
```

Example: `I[0.0012 HCore] Starting...`
