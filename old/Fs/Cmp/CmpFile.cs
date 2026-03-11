using System.Text.RegularExpressions;

namespace HCore.Main.Fs.Cmp;

public abstract partial class CmpFile
{
    [GeneratedRegex(@"^SL\r?\n" +
                    @"POINTS TO (?<pointType>.*?)\r?\n" +
                    @"(?<content>.*)", RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex CmpHeaderRegex();

    protected virtual string? PointType { get; }

    protected CmpFile()
    {
        
    }


    protected CmpFile(string pointType)
    {
        PointType = pointType ?? throw new ArgumentNullException(nameof(pointType));
    }
    
    
    
    public static CmpFile Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be null or empty", nameof(text));

        var match = CmpHeaderRegex().Match(text);
        if (!match.Success)
            throw new FormatException("Invalid CMP file format");

        var pointType = match.Groups["pointType"].Value.Trim();
        var content = match.Groups["content"].Value;

        return pointType.ToUpperInvariant() switch
        {
            "MODULE" => CmpModulePointer.Parse(content),
            "ENTRY" => CmpEntryPointer.Parse(content),
            _ => throw new NotSupportedException($"Unsupported point type: {pointType}")
        };
    }

    public virtual string Serialize()
    {
        return $"SL{Environment.NewLine}" +
               $"POINTS TO {PointType}{Environment.NewLine}";
    }
}