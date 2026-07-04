namespace HCore.Main.Internal;

public class ModPackInfo
{
    public required string Name { get; init; }

    public required string DllName { get; init; }
    public required string? PdbName { get; init; }

    public required string Path { get; init; }
}