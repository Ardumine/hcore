using System;

namespace HCore.Modules.Base;

public abstract class VirtualNode : IVirtualNode
{
    protected VirtualNode(string name, IVirtualDirectory? parent)
    {
        Name = name;
        Parent = parent;
    }

    public string Name { get; }
    public IVirtualDirectory? Parent { get; }
    public virtual string Path => Parent is null ? "/" : Parent.Path == "/" ? $"/{Name}" : $"{Parent.Path}/{Name}";
}
