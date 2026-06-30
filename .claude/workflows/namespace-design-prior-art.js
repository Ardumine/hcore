export const meta = {
  name: 'namespace-design-prior-art',
  description: 'Research how existing systems bound their namespace and model invocation, for HCore inter-module comms design',
  phases: [
    { title: 'Research' },
    { title: 'Synthesize' },
  ],
}

const LENSES = [
  {
    key: 'dbus',
    prompt: `Research the D-Bus IPC system's object model in precise technical detail. I need accurate answers to: (1) How do object paths (e.g. /org/freedesktop/NetworkManager) relate to interfaces (e.g. org.freedesktop.NetworkManager.Device) and members (methods/signals/properties)? (2) Critically: when a method returns a value (say a string or a struct), does that returned value become a new addressable object path? Or does the namespace only contain explicitly-exported objects? Explain how D-Bus AVOIDS infinite path recursion — i.e. why /obj/Method/result/SubMethod does not exist. (3) How does Introspection work (org.freedesktop.DBus.Introspectable, the introspection XML)? How do typed language bindings/proxies get generated from introspection data? (4) How does a client get a reference to a remote object — by well-known bus name + object path? Search the web for authoritative sources (freedesktop.org spec, dbus tutorials). Return: the precise three-level model (path / interface / member), the rule for what is and isn't an addressable node, and how typed proxies sit on top of a dynamic message-passing substrate. Be concrete with examples.`,
  },
  {
    key: 'plan9',
    prompt: `Research Plan 9's "everything is a file" philosophy and the 9P protocol in precise technical detail. I need: (1) How do synthetic/virtual file servers decide WHICH files exist in their served directory? Concretely, how do servers like the ones for /net (the IP stack), /proc, or a window system expose control vs data via "ctl" and "data" files? (2) How is depth bounded — why isn't the tree infinitely deep? What makes something a file vs not? (3) How does invocation work: you write a command string to a ctl file and read results from a data file — explain this pattern. (4) The clone/ctl/conn directory pattern used by /net for connections. (5) Trade-offs: what do you GAIN (uniformity, scriptability, network transparency via 9P) and LOSE (no type safety, stringly-typed protocols, per-server ad hoc conventions)? Search authoritative sources (plan9 papers by Pike et al., "The Use of Name Spaces in Plan 9", "The Styx Architecture"). Return concrete examples of how a Plan 9 file server bounds its namespace and turns operations into file reads/writes.`,
  },
  {
    key: 'capabilities',
    prompt: `Research capability-based addressing and microkernel IPC in precise technical detail, covering seL4, L4, Mach ports, and Cap'n Proto RPC / the E language object-capability model. I need: (1) What is a capability as a reference — an unforgeable handle to an object/service — and how does it differ from a path/name in a global namespace? (2) How does a process obtain a capability to another service (initial endowment, capability passing in messages, a registry/nameserver)? (3) In Cap'n Proto / CapTP: how are object references passed in messages, and how does "promise pipelining" let you call a method on the result of a call without a round trip — and crucially, are those results addressable globally or only held as live references by the caller? (4) Mach ports and L4 endpoints as message destinations. (5) The security argument: capabilities = reference + authority bundled, no ambient authority, vs a global namespace where anyone who can name a path can access it. Search authoritative sources. Return: the capability model vs namespace model distinction, how references are obtained and passed, and the security implications for a module system.`,
  },
  {
    key: 'service-registries',
    prompt: `Research interface-based service registries in modular runtimes, in precise technical detail: OSGi (Java) service registry, gRPC + server reflection, Java's ServiceLoader, and COM (QueryInterface/IUnknown). I need: (1) OSGi: how do bundles register services by interface type into a service registry, and how do other bundles look them up by interface? How does this decouple bundles while still being typed? What about the dynamic nature (services come and go)? (2) gRPC server reflection: how does a client discover available services and methods at runtime without compile-time stubs, and how do generated typed stubs relate to the underlying Protobuf service descriptors? (3) COM: QueryInterface/IUnknown — how an object exposes multiple interfaces and a client negotiates which interface it wants at runtime, while calls are still vtable-typed. (4) The common pattern: a typed CONTRACT (interface/descriptor) published into a REGISTRY, looked up by identity, yielding a typed proxy — but where does the contract definition physically live so caller and callee agree without tight coupling? Search authoritative sources. Return: how each system separates "service identity + contract" from "implementation", where the interface definition lives, and how lookup yields a typed handle.`,
  },
  {
    key: 'actors-latebinding',
    prompt: `Research message-passing object models and late binding in precise technical detail: the Actor model (Erlang/OTP processes & PIDs, gen_server), Smalltalk message passing (doesNotUnderstand:), and Objective-C runtime messaging. I need: (1) Erlang: processes are addressed by PID or registered name; communication is purely asynchronous message send (!) with pattern-matched receive — there is NO shared interface/type the caller must import. How does a caller know what messages a process accepts (conventions, behaviours like gen_server, documentation)? (2) gen_server: how does it impose a call/cast structure (synchronous call vs async cast) on top of raw messages, giving a pseudo-RPC while staying message-based? (3) Smalltalk/Obj-C: late binding — sending a message the receiver may or may not understand, resolved at runtime; the doesNotUnderstand: / forwardInvocation: hook for dynamic dispatch and proxies. (4) The trade-off: zero compile-time coupling and easy network/process transparency, vs no static guarantee the receiver handles the message. Search authoritative sources. Return: how message-passing systems address entities WITHOUT a shared imported interface, how they recover RPC-like ergonomics (gen_server), and how late binding enables transparent proxies.`,
  },
]

phase('Research')
const findings = await parallel(LENSES.map(l => () =>
  agent(l.prompt, { label: `research:${l.key}`, phase: 'Research', schema: {
    type: 'object',
    additionalProperties: false,
    required: ['system', 'namespaceRule', 'invocationModel', 'howReferencesObtained', 'tradeoffs', 'lessonForHCore', 'concreteExamples'],
    properties: {
      system: { type: 'string', description: 'The system(s) covered' },
      namespaceRule: { type: 'string', description: 'The precise rule for what is and is not an addressable entity in the namespace — how depth/recursion is bounded' },
      invocationModel: { type: 'string', description: 'How a call/operation actually happens (typed vtable, message, file write, etc.)' },
      howReferencesObtained: { type: 'string', description: 'How a client obtains a reference/handle to another entity' },
      tradeoffs: { type: 'string', description: 'What is gained and lost by this approach' },
      lessonForHCore: { type: 'string', description: 'The specific transferable lesson for HCore: should /modules/mod1/Func1 exist? where does the path end?' },
      concreteExamples: { type: 'array', items: { type: 'string' }, description: '2-4 concrete examples (paths, message formats, API signatures)' },
    },
  }})
)).then(rs => rs.filter(Boolean))

phase('Synthesize')
const synth = await agent(
  `You are synthesizing prior-art research into a design framing for HCore, a C# microkernel-style runtime building inter-module communication.

HCore's open question, verbatim from the designer:
"Module1 exposes Func1, Module2 calls it. Should /modules/mod1/Func1 exist as a path, or only /modules/mod1? If only /modules/mod1 exists, any caller needs Module1's interface (compile-time dependency). If everything is exposed as files in a VFS, what is the depth? Would /modules/mod1/ASimpleString/Split exist? Where does this end? Is the path infinite? What IS HCore?"

Here are structured findings from researching 5 prior-art systems:

${JSON.stringify(findings, null, 2)}

Produce a synthesis that:
1. Identifies the SINGLE core conceptual confusion underlying the designer's question (hint: it relates to conflating two distinct things).
2. States the bounding rule that ALL these systems share for "what is an addressable node" — and why the infinite-path fear (ASimpleString/Split) is a non-problem in every one of them.
3. Lays out the 3 archetypal design choices for HCore (coarse/interface-only, fine/method-as-file, hybrid-introspectable) with the prior-art system that exemplifies each, and the concrete trade-offs.
4. Gives a clear recommendation for HCore with reasoning, including how typed C# interfaces (IModule1 / GetModuleInterface<T>) can sit as OPTIONAL sugar over a dynamic substrate (cite D-Bus/gRPC-reflection).
5. Answers crisply: "What is HCore?" in one or two sentences.

Be concrete and opinionated. This feeds a senior design discussion; assume the reader knows C# and OS concepts.`,
  { label: 'synthesize', phase: 'Synthesize' }
)

return { findings, synth }