using HCore.Modules.Base;

namespace HCore.Packages.Sensor.Lidar;

public interface ILidar : IModule
{
    /// <summary>Set the synthetic publish rate (Hz). Used by the AFCP Layer 3 self-test.</summary>
    void SetFrameRate(int hz);

    /// <summary>Read back the configured publish rate.</summary>
    int GetFrameRate();

    /// <summary>A fixed identifier string — exercises a string return over the wire.</summary>
    string GetName();
}
