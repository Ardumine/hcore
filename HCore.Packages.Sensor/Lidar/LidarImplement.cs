using HCore.Modules.Base;
using HCore.Modules.Robotics;

namespace HCore.Packages.Sensor.Lidar;

/// <summary>
/// Demo producer: exposes a <c>scan_data</c> stream facet and publishes synthetic
/// scan frames on a background loop. <see cref="Run"/> starts the loop and returns
/// (the instance stays alive at <c>/proc/lidar</c>); <see cref="OnKilled"/> stops it.
/// </summary>
public sealed class LidarImplement : BaseImplement, ILidar, IRunnable
{
    private IExposedData<ScanFrame>? _scan;
    private int _frameRateHz = 10;

    public void Run()
    {
        _scan = Data.ExposeData<ScanFrame>("scan_data", FacetKind.Stream, formatter: FormatFrame);
        // Observe the kernel-managed StopToken: kill cancels it and the loop ends.
        _ = Task.Run(() => PublishLoop(StopToken));
    }

    // --- ILidar (exercised by the AFCP Layer 3 MKCall self-test) ---

    public void SetFrameRate(int hz) => _frameRateHz = hz;

    public int GetFrameRate() => _frameRateHz;

    public string GetName() => "lidar-demo";

    private async Task PublishLoop(CancellationToken ct)
    {
        var rng = new Random();
        var index = 0;
        while (!ct.IsCancellationRequested)
        {
            var ranges = new double[360];
            for (var i = 0; i < ranges.Length; i++)
            {
                ranges[i] = rng.NextDouble() * 5.0;
            }

            _scan!.Publish(new ScanFrame(index++, -Math.PI, Math.PI, ranges));

            try
            {
                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static string FormatFrame(ScanFrame f)
        => $"frame:       {f.FrameIndex}\n" +
           $"angle_min:   {f.AngleMin:F3}\n" +
           $"angle_max:   {f.AngleMax:F3}\n" +
           $"ranges:      [{f.Ranges.Length} samples]";
}
