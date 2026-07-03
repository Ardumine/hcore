using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;

namespace HCore.Modules.Base;

/// <summary>
/// Shared .hpk reader used by both the kernel-side bootstrap and the
/// user-space <c>hpm install</c> command. A .hpk is a gzip-compressed
/// tar archive with a flat internal structure:
/// <c>manifest.json</c>, <c>mpd</c>, <c>lib/*.dll</c>, optional <c>src/</c>,
/// <c>etc/</c>, and <c>scripts/</c>.
/// </summary>
public static class HpkArchive
{
    /// <summary>
    /// Read and deserialize the <c>manifest.json</c> from a .hpk stream
    /// without extracting anything else.
    /// </summary>
    public static PackageManifest ReadManifest(Stream hpkStream)
    {
        using var gzip = new GZipStream(hpkStream, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new TarReader(gzip, leaveOpen: true);

        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.Name == "manifest.json")
            {
                return JsonSerializer.Deserialize<PackageManifest>(entry.DataStream!)
                       ?? throw new InvalidOperationException("manifest.json is empty.");
            }
        }

        throw new InvalidOperationException("manifest.json not found in .hpk archive.");
    }

    /// <summary>
    /// Extract all entries from a .hpk stream into <paramref name="targetPath"/>,
    /// flattening the tar's internal directory structure. The <c>lib/</c>
    /// prefix is stripped so DLLs land directly in the target directory
    /// (matching the existing <c>FS/packs/&lt;name&gt;/</c> layout).
    /// </summary>
    public static void Extract(Stream hpkStream, string targetPath, IModuleFileSystem vfs)
    {
        using var gzip = new GZipStream(hpkStream, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new TarReader(gzip, leaveOpen: true);

        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType == TarEntryType.Directory)
                continue;

            if (entry.DataStream is null)
                continue;

            var relativePath = entry.Name;

            // Flatten lib/ → root
            if (relativePath.StartsWith("lib/"))
                relativePath = relativePath["lib/".Length..];

            var destPath = vfs.ResolvePath($"{targetPath}/{relativePath}");

            var destDir = destPath[..destPath.LastIndexOf('/')];
            if (!vfs.DirectoryExists(destDir))
                vfs.CreateDirectory(destDir);

            using var destStream = vfs.OpenFileStream(destPath, FileMode.Create, FileAccess.Write);
            entry.DataStream.CopyTo(destStream);
        }
    }
}
