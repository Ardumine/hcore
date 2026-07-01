# AFCP Layer 3 — MKCall / ModuleProxy (Design record)

> **Status:** IMPLEMENTED & VERIFIED (tracks TODO.md §C2 / §C7c). `afcp test` passes;
> the Module1/Module2 two-instance demo works (see `docs/afcp/AFCP.md` "Layer 3").
> This doc is the design record; the authoritative description now lives in
> [AFCP.md](AFCP.md).
> **Related:** [AFCP.md](AFCP.md) (Layers 1 & 2), [DATA_PLANE_DESIGN.md](../data-plane/DATA_PLANE_DESIGN.md) Part IX, [V2_V3_COMPARISON.md](../comparison/V2_V3_COMPARISON.md) §4–§5.

## Goal

Make a remote module-interface call look local. A caller on instance A does:

```csharp
var lidar = Host.GetModuleInterface<ILidar>("/mnt/proc/lidar");
lidar.SetFrameRate(60);   // executes on instance B, over the wire
```

where `/mnt` is a mount of B's root on A. `GetModuleInterface<T>(remotePath)` returns a
**marshalling proxy** when the path resolves to a remote mount; method invocations on the
proxy are serialized into a new AFCP `Call` request/response, dispatched server-side into
the real `BaseImplement` instance, and the return value (if any) is carried back.

This is **Layer 3 — RPC, request/response**. It is deliberately NOT a file verb
("you can't `cat` your way into a method call" — DATA_PLANE_DESIGN.md:617). It mirrors how
Layer 2 (subscribe-push) is the one non-VFS verb for streams.

## Non-goals (explicitly deferred)

- **Capability model (C3).** Remote calls are wide-open trusted-LAN, same stance as
  remote VFS writes (C7a) and `Kill` (`IModuleHost.cs:86`). A mounting peer can call any
  method on any served instance. C3 gates production safety; documented gap.
- **Typed wire errors (C7d).** `CallResponse` carries a `Success` bool + `Error` string,
  matching the minimalism of `Write`/`MkDir`/`Remove`. Full typed-error taxonomy is C7d.
- **`Nullable<T>` value-type args/returns.** The AFCP serializer does not support them
  (AFCP.md:86–89). Use the `long X` + `bool HasX` flag-pair idiom in any `Call` message
  fields, and require callers to avoid `Nullable<T>` in proxied method signatures for now.
- **`FastMethodInfo` method-index dispatch (V2 optimization).** ~~First cut uses
  reflection + a `ConcurrentDictionary` method cache server-side.~~ **Ported from
  V2 during this pass** (`HCore.Main/Internal/FastMethodInfo.cs`) — the server-side
  invoker is a compiled `Expression` delegate per method, cached in a
  `ConcurrentDictionary<CallKey, FastMethodInfo>`. Method *resolution* (name →
  `MethodInfo` via `Type.GetType` + `GetMethod`) still happens once per
  `(type, method, param signature)` on cache miss; the hot path is the compiled
  delegate, not `MethodInfo.Invoke`. V2's `uint` method *index* was NOT ported
  (name + param-type names instead — overload-safe, no shared enumeration contract).
- **Out/ref parameters.** Not supported by the proxy. In/out semantics need a richer
  message; defer.

---

## Design decisions

### D1. Method identification: name + parameter type full names

`CallRequest` carries `MethodName` (string) + `ParamTypeNames` (string[]). The server
resolves the method via `Type.GetMethod(name, BindingFlags, paramTypes)`.

- Handles **overloads** unambiguously (name alone would not).
- Robust across ALCs and versions without a shared method-index registry.
- V2 used a `uint` index assigned by `CreateMethodCache`; faster but requires both peers to
  assign indices identically. Deferred (see Non-goals).

### D2. Argument + return marshalling: polymorphic `object[]` / `object?`

The AFCP serializer handles `object` (a `CanBeDerived` class) via `DerivedSerializer`
(`Serializer.cs:208`): each value is written as `[assembly-qualified runtime type name] +
[runtime-typed payload]`, and `NullableSerializer` wraps it for null handling. An `object[]`
is a `Managed1DArray` whose element serializer is that derived transformer — so **mixed-type
arrays serialize polymorphically** (verified by reading `ArraySerializer.GetManagedArraySerializer`
+ `GetMethodForSerialize` at `Serializer.cs:156`). This is the same path V2 used for
`PacketModFuncRequest.Params`.

Therefore the wire payload is simply:

- `CallRequest.Args` : `object[]` — one element per parameter, polymorphic. Empty array for
  no-arg methods. Null elements are handled by the null wrapper.
- `CallResponse.ReturnValue` : `object?` — the boxed return value. Null covers both void
  methods and null reference returns (the proxy ignores the return for void methods anyway,
  since `DispatchProxy.Invoke`'s return is discarded by the caller for void). Value-type
  returns are boxed and round-trip via the derived path.

This drops the earlier per-arg `(TypeName, byte[] Payload)` wrapper entirely — no `CallArg`
type, no manual runtime-typed serialize calls, no `byte[][]`. The `Success`/`Error` fields
carry failure (D3-failure below).

- **Failure:** `Success=false`, `Error` = `ex.GetType().FullName + ": " + ex.Message`,
  `ReturnValue = null`. The proxy throws a `RemoteCallException` carrying that string. (No
  exception-type reconstruction yet — C7d territory.)

### D4. Proxy: `System.Reflection.DispatchProxy` (V2 precedent)

New `RemoteModuleProxy<T> : DispatchProxy where T : IModule`, in
`HCore.Main/Vfs/RemoteModuleProxy.cs` (alongside `RemoteFileSystem`).

- Holds `AfcpClient`, `Serializer`, and the **remote** instance path (mount prefix already
  stripped — mirrors Layer 2's `remotePath`).
- `Invoke(MethodInfo, object[] args)` builds a `CallRequest`, calls
  `AfcpClient.CallAsync(CallRequest, ct)`, decodes the `CallResponse`, and returns/throws.
- Factory `RemoteModuleProxy.Create<T>(client, serializer, remotePath)` — creates the proxy
  via `DispatchProxy.Create<T, RemoteModuleProxy<T>>()` and initializes it.
- `DispatchProxy` can only proxy interfaces; `T : IModule` guarantees that.

### D5. Path interception: `ModuleHost.GetModuleInterface<T>` consults `TryResolveMount`

Mirror `DataHost.Subscribe<T>` (`DataHost.cs:90`). In `ModuleHost.GetModuleInterface<T>`
(`ModuleHost.cs:79`), **before** the `@`-prefix and `/proc` branches:

1. `_vfs.TryResolveMount(instancePath, out var fs, out var remotePath)`.
2. If `fs is RemoteFileSystem remote` → return `RemoteModuleProxy.Create<T>(remote.Client, remote.Serializer, remotePath)`.
3. Otherwise fall through to the existing local resolution.

`ScopedModuleHost.GetModuleInterface<T>` already passes through to the kernel
`ModuleHost`, so this is the single interception point. `RemoteFileSystem` exposes its
`AfcpClient` + `Serializer` via `internal` accessors (currently private).

`InstanceNameFromPath` currently rejects arbitrary `/`-prefixed paths (`ModuleHost.cs:344`).
The `TryResolveMount` check happens first, so remote paths never reach that rejection.

### D6. Server-side dispatch: new `IAfcpProvider.Call` + a non-generic instance resolve

- `AFCP/IAfcpProvider.cs`: add `CallResponse Call(CallRequest request)` to `IAfcpProvider`.
- `AFCP/AfcpServer.cs` `PeerSession.HandleRequest` switch (`:111`): add
  `case MessageType.Call:` → `_provider.Call(req)`.
- `HCore.Main/Internal/AfcpKernelService.cs` `VfsAfcpProvider`: implement `Call`:
  1. Resolve the instance by `request.InstancePath` — needs a **new non-generic** kernel
     surface `ModuleHost.TryResolveInstance(string path, out BaseImplement instance)`.
     (`GetModuleInterface<T>` is typed and casts; we need the raw object to reflect over.)
     Path parsing reuses `InstanceNameFromPath`.
  2. Resolve the method: `ConcurrentDictionary<(Type, string, Type[]), MethodInfo>` cache
     (keyed by declaring type + name + param types). `Type.GetType` for each
     `ParamTypeName`; `instance.GetType().GetMethod(name, public instance, binder, paramTypes, null)`.
  3. Invoke: `method.Invoke(instance, args)`. Catch `TargetInvocationException` → unwrap.
   4. Box return into `CallResponse.ReturnValue` (the serializer's `object?` path handles
      boxing/polymorphism). Void → `ReturnValue = null`.
  5. Any failure → `CallResponse { Success=false, Error=... }`.

### D7. `AfcpClient.CallAsync`

`AFCP/AfcpClient.cs`: add `CallAsync(CallRequest req, CancellationToken ct)` via the
existing `RoundTripAsync<TReq,TRes>(MessageType.Call, req, ct)` pattern (`:175`). Returns
`CallResponse`.

---

## Wire protocol additions

`AFCP/Protocol/MessageType.cs`:
```
Call = 9,
```

`AFCP/Protocol/Messages.cs` — two new POCOs (parameterless ctor, public get/set props,
matching the existing message style; no `Nullable<T>` fields):

```csharp
public class CallRequest {
    public string InstancePath { get; set; } = "";        // remote-side path, e.g. "/proc/lidar"
    public string MethodName { get; set; } = "";
    public string[] ParamTypeNames { get; set; } = Array.Empty<string>();  // assembly-qualified
    public object[] Args { get; set; } = Array.Empty<object>();            // polymorphic
    // ReturnTypeFullName omitted: the proxy already knows MethodInfo.ReturnType; the server
    // only needs to know void-ness to skip boxing, which it derives from the resolved MethodInfo.
}

public class CallResponse {
    public bool Success { get; set; }
    public string Error { get; set; } = "";
    public object? ReturnValue { get; set; }   // null for void / null return / failure
}
```

`object[]` / `object?` ride the `DerivedSerializer` path (`Serializer.cs:208`) — each value
carries its own runtime type tag, so polymorphic args/returns and nulls work without a
separate per-arg wrapper. (This is the same mechanism V2 relied on for `PacketModFuncRequest.Params`.)

---

## Code change map

| # | File | Change |
|---|---|---|
| 1 | `AFCP/Protocol/MessageType.cs` | `Call = 9` |
| 2 | `AFCP/Protocol/Messages.cs` | `CallRequest`, `CallResponse` |
| 3 | `AFCP/IAfcpProvider.cs` | `CallResponse Call(CallRequest)` on `IAfcpProvider` |
| 4 | `AFCP/AfcpServer.cs` | `case MessageType.Call:` in `PeerSession.HandleRequest` |
| 5 | `AFCP/AfcpClient.cs` | `CallAsync(CallRequest, ct)` |
| 6 | `HCore.Main/Internal/ModuleHost.cs` | `TryResolveInstance(path, out BaseImplement)`; `TryResolveMount` branch in `GetModuleInterface<T>` |
| 7 | `HCore.Main/Vfs/RemoteFileSystem.cs` | `internal AfcpClient Client` + `internal Serializer Serializer` accessors |
| 8 | `HCore.Main/Vfs/RemoteModuleProxy.cs` | **NEW** — `RemoteModuleProxy<T> : DispatchProxy` |
| 9 | `HCore.Main/Internal/AfcpKernelService.cs` | `VfsAfcpProvider.Call` impl + method cache |
| 10 | `HCore.Packages.Sensor` | add a callable method to `ILidar` (e.g. `SetFrameRate(int)` / `GetRate()`) for the self-test |
| 11 | `HCore.Main/Internal/AfcpKernelService.cs` `SelfTest` | extend `afcp test`: mount loopback, get remote proxy, call a method, assert return value |

---

## Constraints (to document)

1. **AFCP-serializable args/returns.** Every argument and return type must be
   AFCP-serializable: unmanaged struct / string / Guid / array / `List<T>` /
   `Dictionary<,>` / a class with a **parameterless constructor** and public get/set
   properties (AFCP.md:394–397). `Nullable<T>` value types are NOT supported.
2. **Both peers must have the type loaded.** `DerivedSerializer` writes the
   assembly-qualified name; the server resolves arg/param/return types via `Type.GetType`.
   Contract types in `HCore.Modules.Base` resolve (shared identity across ALCs). Custom
   arg/return types in a package assembly must be loaded on both peers — e.g. a `ScanFrame`
   arg only works if both A and B have the sensor package's assembly loadable. (This is the
   same constraint Layer 2 imposes on facet value types, TODO.md:184–186.)
3. **No out/ref params.** Proxy does not marshal them.
4. **No capability check.** A mounting peer can call any public method on any served
   instance. Trusted-LAN only (C3).
5. **Exception round-trip is lossy.** Server-side exceptions surface as a string
   `Type.FullName: Message`; the proxy throws `RemoteCallException`. Original exception type
   is not reconstructed (C7d).

---

## Verification

- **Extend `afcp test`** (`AfcpKernelService.SelfTest`): after the existing loopback mount
  + Layer 2 checks, obtain a remote proxy via `GetModuleInterface<ILidar>("/.../proc/lidar")`,
  call a value-returning method and a void method, assert the return value and that the
  method executed server-side (observable side effect or echo). Also assert a failing call
  surfaces `Success=false` / throws on the client.
- **Manual shell check**: `afcp test` from the console shell; confirm output.
- No new test framework (repo has none).

## Open question (decide before implementing)

- ~~**Demo interface location.**~~ **RESOLVED** — extend `ILidar` in `HCore.Packages.Sensor`
  (matches DATA_PLANE_DESIGN.md:617's `ILidar.SetFrameRate(60)` example; Sensor is already the
  Layer 1/2 demo package).

---

## V2 prior art (reference, `/home/ardumine/hort/kernel` @ 8627f42)

V2 implemented MKCall + `ModuleProxy` and is the direct template. Key files (read for
reference, do not copy verbatim — V3's kernel/user split and ALC model differ):

- `Kernel/Modules/Helpers/ModuleProxy.cs` — `ModuleProxy<T> : DispatchProxy`.
  `Invoke(targetMethod, args)` → `channel.Run(CacheMethods[targetMethod.Name], args)`.
  `CreateProxy<T>(ModuleChannel)` builds a `Dictionary<string,uint>` method-name→index cache.
  **Always** returns a proxy (location transparency); V3 deliberately drops this and proxies
  only remote paths (direct dispatch stays zero-overhead for local calls — see
  V2_V3_COMPARISON.md §5).
- `Kernel.AFCP/FastMethod.cs` — `FastMethodInfo`: compiles a `ReturnValueDelegate` per
  `MethodInfo` via `Expression.Lambda`. This is the V2 server-side invocation fast path. **V3
  first cut uses `MethodInfo.Invoke` + a `ConcurrentDictionary` cache; porting `FastMethodInfo`
  is a future optimization** (proven in V2, small standalone class).
- `Kernel/Modules/ModuleCreator.cs:220` `CreateMethodCache` — assigns a `uint` index per
  interface method by enumerating `GetMethods` (DeclaredOnly + inherited interfaces). Server
  caches `ReturnValueDelegate[]` per module path; `RunFuncLocal` dispatches by index. **Latent
  bug:** the proxy's `CacheMethods` is keyed by `method.Name` only, so overloaded interface
  methods collide. V3 avoids this by sending `MethodName` + `ParamTypeNames` (stateless, no
  shared enumeration contract, overload-safe).
- `Kernel.AFCP/ChannelManager.cs:162` `RunFuncRemote` — sends
  `PacketModFuncRequest { ChannelPath, FuncID, Params }`; server
  `ProcessFunctionRequestPacket` → `RunLocalFunc` → cached delegate → `PacketModFuncAnswer.Out`.
  V3's `CallRequest`/`CallResponse` mirror this shape (`object[] Args` / `object? ReturnValue`),
  swapping `FuncID` for `MethodName`+`ParamTypeNames`.
- `Kernel.Modules.Base/MKCalls/` — V2's `MKCallClient`/`MKCallReceiver`/`MKCallRequest`/
  `MKCallResult` was a typed request/result RPC *syscall* surface (GetModuleInterface,
  SubModule CRUD) using a function-pointer bridge (`delegate*<MKCallRequest,MKCallResult>`,
  IL-emit dynamic type) so modules never held the receiver object. **V3 does not need this** —
  the kernel/user split is already enforced by interface injection (`Vfs`/`Host`/`Data`) and
  `ScopedModuleHost`; Layer 3 is purely the *remote* method-call path, not a local syscall
  indirection. The function-pointer security trick is moot in V3.

### Decisions diverging from V2

| Concern | V2 | V3 (this plan) | Why |
|---|---|---|---|
| Local calls | always proxied | direct dispatch, proxy only for remote paths | V3 design (zero local overhead) |
| Method ID | `uint` index (shared enumeration) | `MethodName` + `ParamTypeNames` | overload-safe, stateless, no sync contract |
| Server invoke | `FastMethodInfo` compiled delegate | `FastMethodInfo` compiled delegate (ported) | identical mechanism |
| Args/return wire | `object[] Params` / `object Out` | `object[] Args` / `object? ReturnValue` | identical mechanism (DerivedSerializer) |
| Local syscall indirection | `MKCallClient`/`MKCallReceiver` + fn-pointer | none | V3's injection model already enforces the split |

---

## Implementation notes (what changed during the pass)

- **`FastMethodInfo` ported immediately**, not deferred — the user asked for it not to
  be slow despite using strings. Method *resolution* is still string-based
  (`MethodName` + `ParamTypeNames`), but each resolved method is compiled to a
  `Expression` delegate and cached; the per-call cost is one dictionary lookup +
  one delegate invoke, no reflection.
- **`IModuleHost.GetModuleInterface<T>` gained a `class` constraint.** `DispatchProxy`
  requires `T : class`; module interfaces are reference types, so this is correct and
  non-breaking in spirit. Rippled to `EmptyModuleHost`, `ScopedModuleHost`,
  `ModuleHost`.
- **Self-test drives the proxy reflectively.** `HCore.Main` cannot reference the
  `Sensor`/`TestDemo` packages, so `afcp test` resolves `ILidar` via
  `ModuleHost.GetModuleInterfaceType(moduleName)` and builds the proxy via
  `ModuleHost.GetRemoteModuleInterface(Type, path)` (reflection over
  `RemoteModuleProxy<T>.Create`). Calls are dispatched via `MethodInfo.Invoke` on the
  proxy, which virtual-dispatches into `DispatchProxy`'s generated override — the same
  wire path as a typed call.
- **Surfaced + fixed a latent serializer bug:** `StringSerializer`'s deserializer threw
  `IndexOutOfRangeException` on an empty string (`""`) — `Ldelema` on element 0 of a
  zero-length body array, the same class of bug as the C7a empty-array fix. Never hit
  before because existing messages used `null`, never `""`; `CallResponse.Error`
  defaults to `""`. Fixed in `AFCP/Serializer/Serializers/StringSerializer.cs`.
- **`RemoteModuleProxy<T>` must not be `sealed`** — `DispatchProxy` generates a derived
  type at runtime. (V2's `ModuleProxy<T>` was unsealed.)
- **`object?[] Args` / `object? ReturnValue`** — the serializer handles `object` as a
  `CanBeDerived` class via `DerivedSerializer` (each value carries its runtime type
  tag), so mixed-type argument lists and polymorphic returns serialize without a
  per-arg wrapper. Verified before implementation with a standalone round-trip test.

