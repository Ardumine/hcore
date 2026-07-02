using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using HCore.Modules.Base;

namespace HCore.Packages.Hpm.Mod;

public sealed class HpmImplement : BaseImplement, IOneshotCommand
{
    private string[] _args = [];

    public void SetArguments(string[] args) => _args = args;

    public void Run()
    {
        if (_args.Length < 2)
        {
            Console.WriteLine("usage: hpm install|list|remove|pack [args]");
            return;
        }

        switch (_args[1].ToLowerInvariant())
        {
            case "install": Install(_args.Skip(2).ToArray()); return;
            case "list":    List();                            return;
            case "remove":  Remove(_args.Skip(2).ToArray());   return;
            case "pack":    Pack(_args.Skip(2).ToArray());     return;
            default:
                Console.WriteLine($"hpm: unknown sub-command '{_args[1]}'");
                return;
        }
    }

    private void Install(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: hpm install <path-to-hpk>");
            return;
        }

        var hpkPath = Vfs.ResolvePath(args[0]);
        if (!Vfs.Exists(hpkPath))
        {
            Console.WriteLine($"hpm install: file not found: {hpkPath}");
            return;
        }

        string? packName = null;

        // First pass — read manifest.json to get the package name
        using (var stream = Vfs.OpenFileStream(hpkPath, FileMode.Open, FileAccess.Read))
        using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
        using (var reader = new TarReader(gzip))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                var name = NormalizeEntryName(entry.Name);
                if (name == "manifest.json" && entry.DataStream is not null)
                {
                    using var ms = new MemoryStream();
                    entry.DataStream.CopyTo(ms);
                    var doc = JsonDocument.Parse(ms.ToArray());
                    packName = doc.RootElement.GetProperty("name").GetString();
                    break;
                }
            }
        }

        if (packName is null)
        {
            Console.WriteLine("hpm install: invalid .hpk — no manifest.json found");
            return;
        }

        var dest = $"/packs/{packName}";
        if (!Vfs.Exists(dest))
            Vfs.CreateDirectory(dest);

        // Second pass — extract everything
        using (var stream = Vfs.OpenFileStream(hpkPath, FileMode.Open, FileAccess.Read))
        using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
        using (var reader = new TarReader(gzip))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.DataStream is null) continue;

                var name = NormalizeEntryName(entry.Name);
                if (name.Length == 0) continue; // skip root directory entry

                var destFile = $"{dest}/{name}";
                var dir = Path.GetDirectoryName(name);
                if (!string.IsNullOrEmpty(dir) && dir != ".")
                {
                    var destDir = $"{dest}/{dir}";
                    if (!Vfs.Exists(destDir))
                        Vfs.CreateDirectory(destDir);
                }

                using var fs = Vfs.OpenFileStream(destFile, FileMode.Create, FileAccess.Write);
                entry.DataStream.CopyTo(fs);
            }
        }

        Console.WriteLine($"hpm: installed {packName} to {dest}");
        Console.WriteLine("Restart the shell for new commands to appear.");
    }

    private static string NormalizeEntryName(string name) =>
        name.StartsWith("./") ? name[2..] : name;

    private void List()
    {
        Console.WriteLine("Installed packages:");
        try
        {
            foreach (var pack in Vfs.ListDirectory("/packs"))
            {
                var manifestPath = $"/packs/{pack}/manifest.json";
                if (Vfs.Exists(manifestPath))
                {
                    try
                    {
                        var json = Vfs.ReadAllText(manifestPath);
                        var doc = JsonDocument.Parse(json);
                        var name = doc.RootElement.GetProperty("name").GetString();
                        var version = "?";
                        if (doc.RootElement.TryGetProperty("version", out var v))
                            version = v.GetString() ?? "?";
                        Console.WriteLine($"  {name}  v{version}");
                    }
                    catch
                    {
                        Console.WriteLine($"  {pack}  (invalid manifest)");
                    }
                }
                else
                {
                    Console.WriteLine($"  {pack}  (no manifest)");
                }
            }
        }
        catch
        {
            Console.WriteLine("  (unable to list /packs)");
        }
    }

    private void Remove(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: hpm remove <package-name>");
            return;
        }

        var packPath = $"/packs/{args[0]}";
        if (!Vfs.Exists(packPath))
        {
            Console.WriteLine($"hpm remove: package not found: {args[0]}");
            return;
        }

        Vfs.DeleteDirectory(packPath, recursive: true);
        Console.WriteLine($"hpm: removed {args[0]}");
        Console.WriteLine("Restart the shell for command changes to take effect.");
    }

    private void Pack(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: hpm pack <project-directory>");
            return;
        }

        var projectPath = args[0];
        if (!Directory.Exists(projectPath))
        {
            Console.WriteLine($"hpm pack: directory not found: {projectPath}");
            return;
        }

        var manifestPath = Path.Combine(projectPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.WriteLine("hpm pack: manifest.json not found in project directory");
            return;
        }

        string packName;
        try
        {
            var json = File.ReadAllText(manifestPath);
            var doc = JsonDocument.Parse(json);
            packName = doc.RootElement.GetProperty("name").GetString()!;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"hpm pack: failed to read manifest.json: {ex.Message}");
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"hpm-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            Console.WriteLine($"hpm pack: building {projectPath}...");

            var psi = new ProcessStartInfo("dotnet", $"publish -c Release -o {tempDir}")
            {
                WorkingDirectory = projectPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi)!;
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var err = process.StandardError.ReadToEnd();
                Console.WriteLine($"hpm pack: dotnet publish failed with exit code {process.ExitCode}");
                if (err.Length > 0) Console.WriteLine(err);
                return;
            }

            // Copy manifest.json and mpd into the publish output
            File.Copy(manifestPath, Path.Combine(tempDir, "manifest.json"), overwrite: true);

            var mpdPath = Path.Combine(projectPath, "mpd");
            if (File.Exists(mpdPath))
                File.Copy(mpdPath, Path.Combine(tempDir, "mpd"), overwrite: true);

            // Create .hpk (tar.gz)
            var outputFile = $"{packName}.hpk";
            Console.WriteLine($"hpm pack: packaging {outputFile}...");

            using var fs = new FileStream(outputFile, FileMode.Create);
            using var gzip = new GZipStream(fs, CompressionLevel.Optimal);
            using var writer = new TarWriter(gzip);

            foreach (var file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(tempDir, file);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, relPath)
                {
                    DataStream = File.OpenRead(file)
                };
                writer.WriteEntry(entry);
            }

            Console.WriteLine($"hpm pack: created {outputFile}");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
