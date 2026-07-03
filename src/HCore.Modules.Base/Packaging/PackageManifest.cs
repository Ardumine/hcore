namespace HCore.Modules.Base;

/// <summary>
/// Deserialized <c>manifest.json</c> content inside a .hpk archive.
/// </summary>
public sealed class PackageManifest
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? BaseVersion { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string>? Requires { get; set; }
    public PackageProvides? Provides { get; set; }
    public PackageSource? Source { get; set; }
    public List<PackageCommand>? Commands { get; set; }
}

public sealed class PackageProvides
{
    public List<ModuleEntry>? Modules { get; set; }
    public List<FacetEntry>? Facets { get; set; }
}

public sealed class ModuleEntry
{
    public string Name { get; set; } = "";
    public string? Interface { get; set; }
}

public sealed class FacetEntry
{
    public string? Path { get; set; }
    public string? Type { get; set; }
}

public sealed class PackageSource
{
    public string? Language { get; set; }
    public string? Framework { get; set; }
    public string? BuildCommand { get; set; }
    public List<string>? Files { get; set; }
}

public sealed class PackageCommand
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Mode { get; set; }
    public string? ModuleName { get; set; }
}

/// <summary>
/// Deserialized <c>bootstrap.json</c> — lists packages the kernel must
/// download before spawning init on first boot.
/// </summary>
public sealed class BootstrapConfig
{
    public List<BootstrapPackage> Essential { get; set; } = new();
}

public sealed class BootstrapPackage
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Url { get; set; }
    public string? Sha256 { get; set; }
}
