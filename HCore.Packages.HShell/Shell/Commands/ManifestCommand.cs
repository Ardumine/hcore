using HCore.Modules.Base;

namespace HCore.Packages.HShell.Shell.Commands;

/// <summary>
/// A lazy proxy command created from a <c>manifest.json</c> entry.
/// On invocation, spawns the declared module as a child of the shell
/// (<c>/proc/init/console/__cmd_&lt;name&gt;_&lt;guid&gt;</c>), passes the
/// argv, runs it to completion, and kills the child.
/// </summary>
internal sealed class ManifestCommand : ICommand
{
    public string Name { get; }
    public string Description { get; }
    private readonly string _moduleName;

    public ManifestCommand(string name, string description, string moduleName)
    {
        Name = name;
        Description = description;
        _moduleName = moduleName;
    }

    public void Execute(IReadOnlyList<string> args, ShellContext ctx)
    {
        var leafName = $"__cmd_{Name}_{Guid.NewGuid():N}";
        var child = ctx.Host.SpawnChildByName<IOneshotCommand>(_moduleName, leafName, null);
        try
        {
            child.SetArguments(args.ToArray());
            child.Run();
        }
        finally
        {
            ctx.Host.KillChild(leafName);
        }
    }
}
