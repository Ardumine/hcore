using HCore.Modules.Base;

namespace HCore.Packages.Sensor.RemoteSlam;

/// <summary>
/// Snapshot of what this consumer has received, exposed as a <c>recv_status</c>
/// Cell facet so an observer (the AFCP self-test) can read progress through the
/// ordinary <c>cat /proc/&lt;instance&gt;/recv_status</c> path — no shared type
/// needed on the reader side, since it reads the formatted text.
/// </summary>
public sealed record RecvStatus(long Received, long LastSequence, string State);

/// <summary>
/// Demo consumer for AFCP Layer 2 (subscribe-push). Subscribes to a scan_data
/// facet by path via the ordinary <see cref="IDataHost.Subscribe{T}"/> — the path
/// may be LOCAL (<c>/proc/lidar/scan_data</c>) or REMOTE through a mount
/// (<c>/selftest/proc/lidar/scan_data</c>); the consumer neither knows nor cares
/// (9P-style transparency). It records how many frames arrived, the last sequence,
/// and the subscription state on a <c>recv_status</c> facet.
///
/// The subscribe target is read from the VFS file <see cref="TargetFile"/> if it
/// exists (the self-test writes it before spawning, which crosses no type boundary),
/// otherwise it defaults to the local lidar. A production deployment would take this
/// from the config system (TODO.md §C4), which does not exist yet.
/// </summary>
public sealed class RemoteSlamImplement : BaseImplement, IRemoteSlam, IRunnable
{
    /// <summary>VFS path the self-test writes the subscribe target into.</summary>
    public const string TargetFile = "/tmp/remote_slam_target";

    private const string DefaultTarget = "/proc/lidar/scan_data";

    private ISubscription? _sub;
    private IExposedData<RecvStatus>? _status;
    private long _received;
    private long _lastSequence = -1;

    public void Run()
    {
        var target = DefaultTarget;
        try
        {
            if (Vfs.Exists(TargetFile))
            {
                var configured = Vfs.ReadAllText(TargetFile).Trim();
                if (configured.Length > 0) target = configured;
            }
        }
        catch
        {
            // Fall back to the default target on any read error.
        }

        _status = Data.ExposeData<RecvStatus>("recv_status", FacetKind.Cell, formatter: FormatStatus);
        Publish("Subscribing");

        Logger.I($"subscribing to '{target}'");
        _sub = Data.Subscribe<ScanFrame>(target, OnFrame, OnDisconnected);
        Publish("Active");
    }

    private ValueTask OnFrame(DataEvent<ScanFrame> e, CancellationToken ct)
    {
        _received++;
        _lastSequence = e.Sequence;
        Logger.I($"recv seq={e.Sequence} frame={e.Data.FrameIndex} ranges={e.Data.Ranges.Length}");
        Publish("Active");
        return ValueTask.CompletedTask;
    }

    private void OnDisconnected(DisconnectReason reason)
    {
        Logger.W($"remote scan stream disconnected: {reason}");
        Publish(reason.ToString());
    }

    private void Publish(string state)
        => _status?.Publish(new RecvStatus(_received, _lastSequence, state));

    private static string FormatStatus(RecvStatus s)
        => $"received={s.Received} lastSeq={s.LastSequence} state={s.State}";

    protected override void OnKilled()
    {
        _sub?.Dispose();
    }
}
