namespace HCore.Modules.Base;

/// <summary>
/// A module that receives the privileged driver doors — injected by the kernel
/// after construction, before <c>Run()</c>. Normal modules never get this; only
/// filesystem-driver modules (e.g. AFCP Nexus) qualify.
/// </summary>
public interface IDriverModule : IModule
{
    /// <summary>
    /// One-shot injection of the driver contract surfaces. Called by the kernel
    /// exactly once, after spawn but before Run(). The module stores these
    /// handles internally.
    /// </summary>
    void Init(IKernelVfs kernelVfs, IFacetView facetView, IModuleResolver moduleResolver);
}
