using System.Text.RegularExpressions;

namespace HCore.Main.Fs.Cmp;

public partial class CmpEntryPointer : CmpFile
{
    public string FilePath { get; }
    protected override string PointType => "ENTRY";

    [GeneratedRegex(@"^(?<filepath>.*?)?$", 
        RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex FileRegex();

    public CmpEntryPointer(string filePath)
    {
        FilePath = filePath.Trim() ?? throw new ArgumentNullException(nameof(filePath));
    }

    internal new static CmpEntryPointer Parse(string content)
    {
        var match = FileRegex().Match(content);
        if (!match.Success)
            throw new FormatException("Invalid file pointer format");

        return new CmpEntryPointer(match.Groups["filepath"].Value.Trim());
    }

    public override string Serialize()
    {
        var result = base.Serialize()
                     + $"{FilePath}{Environment.NewLine}";
        return result;
    }
}