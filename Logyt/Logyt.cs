using System.Diagnostics;

namespace Logyt;

public abstract class Logyt
{
    public string Description { get; protected set; } = "";
    /// <summary>
    /// Write info
    /// <param name="message">The message</param>
    /// </summary>
    public abstract void I(string message);

    /// <summary>
    /// Write warning
    /// </summary>
    /// <param name="message">The message</param>
    public abstract void W(string message);

    private readonly Stopwatch _stopwatch =  Stopwatch.StartNew();

    public string GenerateTimeStampString()
    {
        string str = $"{(double)_stopwatch.ElapsedTicks  / Stopwatch.Frequency:0.0000}";
        return str;
    }
}