namespace HCore.Modules.Base;

/// <summary>
/// A module that will run at startup
/// </summary>
public interface IRunnable : IModule
{
    public void Run();
}