using System.Diagnostics.CodeAnalysis;

namespace HCore.Modules.Base;

/// <summary>
/// The instance-resolution driver door — a non-generic lookup slice injected
/// into a driver module (e.g. the AFCP Nexus connector) so it can resolve an
/// instance by path for the serve-side MKCall path. Unlike
/// <see cref="IModuleHost"/> (the full process/IPC surface with spawn, kill,
/// and typed lookup), this is the unprivileged resolution-only door.
/// </summary>
public interface IModuleResolver
{
    /// <summary>
    /// Non-generic instance lookup by <c>/proc</c> path (or bare instance name).
    /// Returns <c>false</c> if nothing is running at that path. Kernel-space
    /// <c>@</c>-services are intentionally NOT reachable here.
    /// </summary>
    bool TryResolveInstance(string instancePath, [MaybeNullWhen(false)] out IModule instance);
}
