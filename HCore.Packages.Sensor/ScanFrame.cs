namespace HCore.Packages.Sensor;

/// <summary>
/// A single lidar scan frame (demo payload). Lives in the producing package
/// because both producer and consumer are in the same package/ALC; for a
/// CROSS-package subscriber it would have to live in HCore.Modules.Base (a
/// different AssemblyLoadContext yields a different Type, so Subscribe&lt;T&gt;
/// would not match ExposeData&lt;T&gt;).
///
/// The parameterless constructor is required by the AFCP serializer
/// (<c>ClassSerializer</c> constructs the instance then sets properties) — any
/// facet value type that crosses a remote subscribe must be AFCP-serializable.
/// The local data plane never needs it (it passes frames by reference).
/// </summary>
public sealed record ScanFrame(int FrameIndex, double AngleMin, double AngleMax, double[] Ranges)
{
    public ScanFrame() : this(0, 0.0, 0.0, Array.Empty<double>()) { }
}
