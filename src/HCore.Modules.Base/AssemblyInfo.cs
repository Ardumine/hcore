using System.Runtime.CompilerServices;

// The kernel (HCore.Main) needs to invoke the lifecycle hooks it injects itself
// into (OnKilled, DescribeForProc) directly on a BaseImplement reference. Kept
// `protected internal` rather than `public` so only the kernel (via this grant)
// or a module's own subclass can ever call them — a sibling package holding a
// concrete instance cannot reach into it (protected access is hierarchy-scoped).
[assembly: InternalsVisibleTo("HCore.Main")]
