using System.Text;
using HCore.Main.Internal;
using HCore.Modules.Base;

namespace HCore.Main.Vfs;

/// <summary>
/// Synthetic, read-only filesystem that exposes the kernel's RUNNING modules as
/// a directory tree — the way Linux and Plan 9 expose processes under /proc.
///
/// It is a live VIEW, not stored data: the tree is rebuilt from the module host
/// on every access, so a module shows up here only once it has actually been
/// created (lazily, on first use), exactly like a process appears in /proc only
/// while it is running. Composite instance names ("usb/device0") are split into
/// real nested directories, so a child renders at /proc/usb/device0 regardless
/// of depth.
///
/// Note the boundary: this is the ADDRESSING/discovery layer (browse with `ls`,
/// see who is running). Calling a module still goes through the host — you do
/// not walk INTO /proc/&lt;name&gt; to invoke a method.
/// </summary>
public sealed class ProcFileSystem : IVirtualFileSystem
{
    private readonly ModuleHost _host;
    private readonly DataHost _dataHost;

    public ProcFileSystem(ModuleHost host, DataHost dataHost)
    {
        _host = host;
        _dataHost = dataHost;
    }

    public string Name => "procfs";
    public bool IsReadOnly => true;

    public IVirtualDirectory Root
    {
        get
        {
            var root = new ReadOnlyVirtualDirectory("/");
            var nodes = new Dictionary<string, ReadOnlyVirtualDirectory> { [""] = root };

            // Parents sort before children ("usb" < "usb/device0" ordinally), so
            // by the time a child is processed its owner's directory already exists.
            foreach (var module in _host.GetRunningModules().OrderBy(m => m.InstanceName, StringComparer.Ordinal))
            {
                var moduleDir = GetOrCreateDirectory(nodes, module.InstanceName.Split('/'));

                var info =
                    $"instance:   {module.InstanceName}\n" +
                    $"module:     {module.ModuleName}\n" +
                    $"friendly:   {module.FriendlyName}\n" +
                    $"interface:  {module.InterfaceType.FullName}\n" +
                    $"implements: {module.ImplementType.FullName}\n" +
                    (module.Details is null ? "" : module.Details + "\n");
                moduleDir.AddChild(new ReadOnlyVirtualFile("info", moduleDir, Encoding.UTF8.GetBytes(info)));
            }

            // Data facets: one read-only file per facet, named after the facet,
            // holding the producer's formatted current value (the "cat" path).
            // Rebuilt on every access, so `cat /proc/<m>/<facet>` sees the latest.
            foreach (var facet in _dataHost.GetFacetsForProc())
            {
                if (!nodes.TryGetValue(facet.InstanceName, out var moduleDir))
                {
                    continue; // instance gone between the two snapshots — skip.
                }

                var body = facet.FormatForCat() ?? "(no data published yet)\n";
                if (!body.EndsWith('\n'))
                {
                    body += "\n";
                }

                moduleDir.AddChild(new ReadOnlyVirtualFile(facet.FacetName, moduleDir, Encoding.UTF8.GetBytes(body)));
            }

            return root;
        }
    }

    /// <summary>
    /// Walk/create intermediate directories for a composite instance key like
    /// "usb/device0", reusing any node already created for an ancestor.
    /// Intermediate-only segments (no module registered at that exact key)
    /// simply get no `info` file — just the directory.
    /// </summary>
    private static ReadOnlyVirtualDirectory GetOrCreateDirectory(Dictionary<string, ReadOnlyVirtualDirectory> nodes, string[] segments)
    {
        var path = "";
        var parent = nodes[""];
        foreach (var segment in segments)
        {
            path = path.Length == 0 ? segment : $"{path}/{segment}";
            if (!nodes.TryGetValue(path, out var dir))
            {
                dir = new ReadOnlyVirtualDirectory(segment, parent);
                parent.AddChild(dir);
                nodes[path] = dir;
            }

            parent = dir;
        }

        return parent;
    }
}
