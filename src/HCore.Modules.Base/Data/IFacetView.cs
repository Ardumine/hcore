namespace HCore.Modules.Base;

/// <summary>
/// The facet driver door — a read-only view of the kernel's facet registry
/// injected into a driver module (e.g. the AFCP Nexus connector) so it can
/// discover facets for the serve-side subscribe path. Unlike
/// <see cref="IDataHost"/> (the full user-space data-plane door with expose,
/// read, and subscribe), this is the unprivileged facet-lookup-only door.
/// </summary>
public interface IFacetView
{
    /// <summary>
    /// Find a facet by its absolute <c>/proc/&lt;instance&gt;/&lt;facet&gt;</c> path.
    /// Returns <c>null</c> if no facet exists at that path.
    /// </summary>
    IFacet? FindFacet(string facetPath);
}
