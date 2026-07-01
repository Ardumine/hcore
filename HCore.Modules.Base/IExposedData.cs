namespace HCore.Modules.Base;

/// <summary>
/// The producer handle returned by <see cref="IDataHost.ExposeData{T}"/>. The
/// module pushes frames through it; the kernel fans each <see cref="Publish"/>
/// out to every subscriber according to the facet's dispatch policy.
/// </summary>
/// <remarks>
/// Zero-copy local: <see cref="Publish"/> passes the reference straight to
/// subscribers. The producer MUST treat <paramref name="value"/> as immutable
/// after publishing (freeze-after-publish contract) — allocate a fresh frame per
/// publish rather than mutating a reused buffer. Not enforced; a producer that
/// breaks it owns the resulting torn reads.
/// </remarks>
public interface IExposedData<T> where T : class
{
    /// <summary>Fan <paramref name="value"/> out to every subscriber.</summary>
    void Publish(T value);

    /// <summary>V2-parity alias for <see cref="Publish"/>.</summary>
    void Set(T value);
}
