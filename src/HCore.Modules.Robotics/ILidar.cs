using HCore.Modules.Base;

namespace HCore.Modules.Robotics;

/// <summary>
/// LIDAR sensor interface. Producers implement this; consumers call it across
/// ALC boundaries via <c>Host.GetModuleInterface&lt;ILidar&gt;(path)</c>.
/// Lives in Robotics (not a package) so the type identity is shared across
/// all load contexts — same pattern as <see cref="IUsbDevice"/>.
/// </summary>
public interface ILidar : IModule
{
    /// <summary>Set the publish rate (Hz).</summary>
    void SetFrameRate(int hz);

    /// <summary>Read back the configured publish rate.</summary>
    int GetFrameRate();

    /// <summary>A fixed identifier string.</summary>
    string GetName();
}
