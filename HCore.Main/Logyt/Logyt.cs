using System.Diagnostics;
using System.Globalization;

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

    private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public string GenerateTimeStampString()
    {
        return FormatTimestamp((double)_stopwatch.ElapsedTicks / Stopwatch.Frequency);
    }

    internal static string FormatTimestamp(double seconds)
    {
        return seconds.ToString("0.0000", CultureInfo.InvariantCulture);
    }
}
