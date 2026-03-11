https://claude.ai/share/4beb3e49-b43b-441f-9ea2-6714373b0c18


Vamos organizar tudo do zero. Primeiro mapeio o que existe, depois proponho os nomes.

---

## O que existe no HCore (conceitos)

```
1. O núcleo em si              → HCore
2. O pacote distribuível       → ???
3. A definição de um módulo    → ???
4. O módulo em execução        → ???
5. Interface base de módulo    → ???
6. Módulo com ponto de entrada → ???
7. O descritor do módulo       → ???
8. O sistema de ficheiros      → VFS
```

---

## Proposta de nomes

| Conceito | Nome proposto | Raciocínio |
|---|---|---|
| O núcleo | `HCore` | mantém-se |
| Pacote distribuível | `Package` | universal, sem conotações |
| Definição do módulo | `Module` | é literalmente isso |
| Módulo em execução | `ModuleInstance` | claro e direto |
| Interface base | `IModule` | mínimo e limpo |
| Módulo executável | `IRunnable` | comunica intenção — "pode correr" |
| Descritor | `ModuleDescriptor` | nome completo, sem ambiguidade |
| Sistema de ficheiros | `VFS` | mantém-se |

---

## Como fica a estrutura do código

```
HCore.Package.TestDemo/
├── HCore.Package.TestDemo.csproj
├── PackageDescriptor.cs
│
├── Module1/
│   ├── IModule1.cs              → public interface IModule1 : IModule
│   ├── ModuleDescriptor.cs      → descreve o Module1
│   └── Module1.cs               → implementação
│
└── Module2/
    ├── IModule2.cs              → public interface IModule2 : IRunnable
    ├── ModuleDescriptor.cs      → descreve o Module2
    └── Module2.cs               → implementação
```

---

## Como ficam as interfaces base

```csharp
// Base — todo o módulo herda daqui
public interface IModule { }

// Módulo com ponto de entrada
public interface IRunnable : IModule
{
    void Run();
}
```

---

## Como fica o ModuleDescriptor

```csharp
public class ModuleDescriptor : IModuleDescriptor
{
    public string Name        => "TestDemo.Module1";
    public string FriendlyName => "Demo Module 1";
    public Type Implementation => typeof(Module1);
    public Type Interface      => typeof(IModule1);
}
```

---

## Como fica o PackageDescriptor

```csharp
public class PackageDescriptor : IPackageDescriptor
{
    public string Name    => "TestDemo";
    public string Version => "1.0.0";

    public IEnumerable<IModuleDescriptor> Modules =>
    [
        new Module1.ModuleDescriptor(),
        new Module2.ModuleDescriptor()
    ];
}
```

---

## A hierarquia completa

```
HCore
├── VFS
├── Package
│   ├── PackageDescriptor
│   └── Module
│       ├── ModuleDescriptor
│       ├── IModule
│       │   └── IRunnable
│       └── ModuleInstance
```

---

Isto faz sentido para ti? Há algum nome que não te convença?