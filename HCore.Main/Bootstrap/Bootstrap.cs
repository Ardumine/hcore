using HCore.Modules.Base;
using System.Text.Json;

namespace HCore.Main.Bootstrap;

/// <summary>
/// First-boot bootstrap: detects missing essential packages and tries to
/// install them from local peer-repo build output or a remote URL.
///
/// Dev mode (no .hpk releases yet): checks for peer repos alongside the
/// kernel workspace (hinit/, hshell/, hpm/) and copies their build output
/// from <c>FS/packs/</c> into the kernel's <c>FS/packs/</c>.
/// </summary>
internal sealed class Bootstrap : IBootstrap
{
    public bool Run(IModuleFileSystem vfs, IModuleLogger logger)
    {
        var config = LoadBootstrapConfig();
        var essential = config.Essential;
        if (essential.Count == 0)
        {
            logger.I("Bootstrap: no essential packages listed — skipping.");
            return false;
        }

        var missing = new List<BootstrapPackage>();
        foreach (var pkg in essential)
        {
            var packPath = $"/packs/{pkg.Name}";
            if (!vfs.DirectoryExists(packPath))
                missing.Add(pkg);
        }

        if (missing.Count == 0)
        {
            logger.I("Bootstrap: all essential packages present — skipping.");
            return false;
        }

        logger.I($"Bootstrap: {missing.Count} essential package(s) missing. Creating FS skeleton...");

        CreateFsSkeleton(vfs);

        var workspaceRoot = FindWorkspaceRoot();
        var anyInstalled = false;

        foreach (var pkg in missing)
        {
            logger.I($"Bootstrap: installing {pkg.Name} v{pkg.Version}...");
            if (TryInstallFromPeerRepo(pkg, vfs, workspaceRoot, logger))
            {
                anyInstalled = true;
                continue;
            }

            if (!string.IsNullOrEmpty(pkg.Url))
            {
                if (TryInstallFromUrl(pkg, vfs, logger))
                {
                    anyInstalled = true;
                    continue;
                }
            }

            logger.W($"Bootstrap: could not install {pkg.Name}. " +
                     "Place the package in FS/packs/{pkg.Name}/ or set up network access.");
        }

        return anyInstalled;
    }

    private static BootstrapConfig LoadBootstrapConfig()
    {
        var assembly = typeof(Bootstrap).Assembly;
        var resourceName = $"{typeof(Bootstrap).Namespace}.bootstrap.json";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return new BootstrapConfig();

        return JsonSerializer.Deserialize<BootstrapConfig>(stream) ?? new BootstrapConfig();
    }

    private static void CreateFsSkeleton(IModuleFileSystem vfs)
    {
        foreach (var dir in new[] { "/packs", "/etc/services", "/home", "/data" })
        {
            if (!vfs.DirectoryExists(dir))
                vfs.CreateDirectory(dir);
        }
    }

    /// <summary>
    /// Walk up from the kernel executable to find the workspace root
    /// (the directory containing hcore/ and peer repos). Returns null
    /// if not found.
    /// </summary>
    private static string? FindWorkspaceRoot()
    {
        var dir = AppContext.BaseDirectory;
        // Walk up looking for the hcore/ directory marker (hcore.sln or src/)
        while (dir is not null && dir.Length > 0)
        {
            if (Directory.Exists(Path.Combine(dir, "src")) ||
                File.Exists(Path.Combine(dir, "hcore.sln")))
            {
                return dir; // we're inside hcore/, so workspace root is parent
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return null;
    }

    private static bool TryInstallFromPeerRepo(BootstrapPackage pkg, IModuleFileSystem vfs, string? workspaceRoot, IModuleLogger logger)
    {
        if (workspaceRoot is null)
            return false;

        // Map package name to peer repo directory name
        var repoName = pkg.Name switch
        {
            "HCore.Packages.HInit"       => "hinit",
            "HCore.Packages.HShell"      => "hshell",
            "HCore.Packages.Hpm"         => "hpm",
            "HCore.Packages.HShellUtils"  => "hshellutils",
            "HCore.Packages.HShellNetUtils" => "hshellnetutils",
            _                            => null
        };

        if (repoName is null)
            return false;

        var peerPackDir = Path.Combine(workspaceRoot, repoName, "FS", "packs", pkg.Name);
        if (!Directory.Exists(peerPackDir))
            return false;

        logger.I($"Bootstrap: copying from peer repo '{repoName}/'...");
        CopyDirectory(peerPackDir, $"/packs/{pkg.Name}", vfs);
        return true;
    }

    private static bool TryInstallFromUrl(BootstrapPackage pkg, IModuleFileSystem vfs, IModuleLogger logger)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var response = client.GetStreamAsync(pkg.Url!).GetAwaiter().GetResult();

            var targetPath = $"/packs/{pkg.Name}";
            logger.I($"Bootstrap: downloading {pkg.Url}...");
            HpkArchive.Extract(response, targetPath, vfs);
            logger.I($"Bootstrap: installed {pkg.Name} v{pkg.Version}.");
            return true;
        }
        catch (Exception ex)
        {
            logger.E($"Bootstrap: failed to fetch {pkg.Name}: {ex.Message}");
            return false;
        }
    }

    private static void CopyDirectory(string hostSrcDir, string vfsDestDir, IModuleFileSystem vfs)
    {
        if (!vfs.DirectoryExists(vfsDestDir))
            vfs.CreateDirectory(vfsDestDir);

        foreach (var file in Directory.GetFiles(hostSrcDir))
        {
            var destPath = $"{vfsDestDir}/{Path.GetFileName(file)}";
            using var srcStream = new FileStream(file, FileMode.Open, FileAccess.Read);
            using var destStream = vfs.OpenFileStream(destPath, FileMode.Create, FileAccess.Write);
            srcStream.CopyTo(destStream);
        }

        foreach (var subDir in Directory.GetDirectories(hostSrcDir))
        {
            var subDirName = Path.GetFileName(subDir);
            CopyDirectory(subDir, $"{vfsDestDir}/{subDirName}", vfs);
        }
    }
}
