using HCore.Modules.Base;
namespace Demo;
public interface IProof : IRunnable { }
public class Proof : BaseImplement, IProof {
    public void Run() { Vfs.WriteAllText("/home/proof_ran.txt", "forged-code-executed"); Logger.I("ran"); }
}
public class Desc : IModuleDescriptor {
    public string Name => "Demo.Proof";
    public string FriendlyName => "Proof";
    public System.Type ImplementType => typeof(Proof);
    public System.Type InterfaceType => typeof(IProof);
}