using System.Diagnostics;
using HCore.Modules.Base;

namespace HCore.Packages.Sensor.Slam;

/// <summary>
/// Demo consumer: subscribes to the lidar's <c>scan_data</c> stream and logs each
/// delivered frame (sequence + inter-frame delta + payload summary). Demonstrates
/// the push hot path and the <see cref="DisconnectReason.ProducerKilled"/> signal
/// when the lidar is reaped.
/// </summary>
public sealed class SlamImplement : BaseImplement, ISlam, IRunnable
{
    private ISubscription? _sub;

    public void Run()
    {
        // Snapshot (pull) before subscribing (push) — the two faces of one facet.
        var snapshot = Data.ReadData<ScanFrame>("/proc/lidar/scan_data");
        Logger.I(snapshot is null
            ? "no scan snapshot yet (producer hasn't published)"
            : $"snapshot frame={snapshot.FrameIndex} ranges={snapshot.Ranges.Length}");

        _sub = Data.Subscribe<ScanFrame>(
            "/proc/lidar/scan_data",
            OnFrame,
            reason => Logger.W($"scan stream disconnected: {reason}"));
    }

    private ValueTask OnFrame(DataEvent<ScanFrame> e, CancellationToken ct)
    {
        var deltaMs = e.InterFrameDelta is null
            ? 0.0
            : e.InterFrameDelta.Value * 1000.0 / Stopwatch.Frequency;

        Logger.I($"scan seq={e.Sequence} frame={e.Data.FrameIndex} interFrame={deltaMs:F1}ms ranges={e.Data.Ranges.Length}");
        return ValueTask.CompletedTask;
    }

    protected override void OnKilled()
    {
        _sub?.Dispose();
    }
}
