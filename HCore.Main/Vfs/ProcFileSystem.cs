using System.Text;
using HCore.Main.Internal;

namespace HCore.Main.Vfs;

/// <summary>
/// Synthetic, read-only filesystem that exposes the kernel's RUNNING modules as
/// a directory tree — the way Linux and Plan 9 expose processes under /proc.
///
/// It is a live VIEW, not stored data: the tree is rebuilt from the module host
/// on every access, so a module shows up here only once it has actually been
/// created (lazily, on first use), exactly like a process appears in /proc only
/// while it is running.
///
/// Note the boundary: this is the ADDRESSING/discovery layer (browse with `ls`,
/// see who is running). Calling a module still goes through the host — you do
/// not walk INTO /proc/&lt;name&gt; to invoke a method.
/// </summary>
public sealed class ProcFileSystem : IVirtualFileSystem
{
    private readonly ModuleHost _host;

    public ProcFileSystem(ModuleHost host) => _host = host;

    public string Name => "procfs";
    public bool IsReadOnly => true;

    public IVirtualDirectory Root
    {
        get
        {
            var root = new ReadOnlyVirtualDirectory("/");
            foreach (var module in _host.GetRunningModules())
            {
                var moduleDir = new ReadOnlyVirtualDirectory(module.InstanceName, root);
                root.AddChild(moduleDir);

                var info =
                    $"instance:   {module.InstanceName}\n" +
                    $"module:     {module.ModuleName}\n" +
                    $"friendly:   {module.FriendlyName}\n" +
                    $"interface:  {module.InterfaceType.FullName}\n" +
                    $"implements: {module.ImplementType.FullName}\n";
                moduleDir.AddChild(new ReadOnlyVirtualFile("info", moduleDir, Encoding.UTF8.GetBytes(info)));
            }

            return root;
        }
    }
}
