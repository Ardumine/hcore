namespace HCore.Packages.Sensor;

/// <summary>
/// A single lidar scan frame (demo payload). Lives in the producing package
/// because both producer and consumer are in the same package/ALC; for a
/// CROSS-package subscriber it would have to live in HCore.Modules.Base (a
/// different AssemblyLoadContext yields a different Type, so Subscribe&lt;T&gt;
/// would not match ExposeData&lt;T&gt;).
/// </summary>
public sealed record ScanFrame(int FrameIndex, double AngleMin, double AngleMax, double[] Ranges);
