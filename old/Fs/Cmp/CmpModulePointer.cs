using System.Text.RegularExpressions;

namespace HCore.Main.Fs.Cmp;

public partial class CmpModulePointer : CmpFile
{
    public string ModuleName { get; }
    public string PackName { get; }

    [GeneratedRegex(@"^(?<module>.*?)\r?\n" +
                    @"AT PACK\r?\n" +
                    @"(?<pack>.*)$", RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex ModuleRegex();

    protected override string PointType => "MODULE";
    
    public CmpModulePointer(string moduleName, string packName) 
    {
        ModuleName = moduleName.Trim() ?? throw new ArgumentNullException(nameof(moduleName));
        PackName = packName.Trim() ?? throw new ArgumentNullException(nameof(packName));
    }

    internal new static CmpModulePointer Parse(string content)
    {
        var match = ModuleRegex().Match(content);
        if (!match.Success)
            throw new FormatException("Invalid module pointer format");

        return new CmpModulePointer(
            moduleName: match.Groups["module"].Value.Trim(),
            packName: match.Groups["pack"].Value.Trim()
        );
    }

    public override string Serialize()
    {
        return base.Serialize() +
               $"{ModuleName}{Environment.NewLine}" +
               $"AT PACK{Environment.NewLine}" +
               $"{PackName}{Environment.NewLine}";
    }
}