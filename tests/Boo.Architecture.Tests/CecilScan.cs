using System.IO;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Boo.Architecture.Tests;

/// <summary>
/// Reference scan over a compiled assembly (Mono.Cecil, the library NetArchTest is built on). A type references X when
/// X is its base type or an interface, the type of one of its fields, properties, events, parameters, return values or
/// locals, a custom attribute on it, its members, their parameters, return values or generic parameters, or when its
/// code calls, reads, writes, constructs, casts to or takes typeof of a member or type of X (generic arguments and arrays included: List&lt;X&gt;, X[]). What counts is
/// decided by the caller's predicates (a type predicate, and a member predicate for calls and field reads), so a rule
/// can be an allowlist ("only these framework types") instead of a list of bad types that always misses one.
/// Results are per outermost type (compiler-made nested types such as lambdas and async state machines count towards
/// their owner), each with the references that hit, for the failure message. Types named inside attribute arguments
/// (typeof) are scanned too; a function pointer type always counts. Not scanned: generic constraints, catch clause
/// types (neither can run code by itself), attributes on the assembly or module itself (the rules check the one that
/// matters, [module: UnverifiableCode], separately), and compiled XAML (BAML).
/// </summary>
internal static class CecilScan
{
    /// <summary>Reads a module with a resolver that finds the framework (runtime and WPF) and the test output folder,
    /// so attribute arguments that use enums from other assemblies can be read. A failure throws; nothing is skipped.</summary>
    private static ModuleDefinition Read(Assembly assembly)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var dir in new[]
                 {
                     Path.GetDirectoryName(assembly.Location), AppContext.BaseDirectory,
                     Path.GetDirectoryName(typeof(object).Assembly.Location),
                     Path.GetDirectoryName(typeof(System.Windows.Window).Assembly.Location),
                     Path.GetDirectoryName(typeof(System.Windows.Forms.Form).Assembly.Location),
                 }.Distinct())
        {
            if (!string.IsNullOrEmpty(dir)) resolver.AddSearchDirectory(dir);
        }
        return ModuleDefinition.ReadModule(assembly.Location, new ReaderParameters { AssemblyResolver = resolver });
    }

    private static readonly string RuntimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    private static readonly Lazy<MetadataResolver> RuntimeOnly = new(() =>
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var dir in resolver.GetSearchDirectories()) resolver.RemoveSearchDirectory(dir);
        resolver.AddSearchDirectory(RuntimeDirectory);
        return new MetadataResolver(resolver);
    });

    /// <summary>True when the type really exists in the .NET runtime installed on this machine (the shared framework
    /// folder, type forwarders followed), not just in an assembly that borrows a framework name or key token. Looked up
    /// with a resolver that searches only that folder; a type that cannot be found there is not from the runtime.</summary>
    public static bool IsFromInstalledRuntime(TypeReference type)
    {
        TypeDefinition? definition;
        try
        {
            definition = RuntimeOnly.Value.Resolve(type);
        }
        catch (AssemblyResolutionException)
        {
            return false; // the claimed assembly is not part of the installed runtime: not approved
        }
        return definition is not null
               && string.Equals(Path.GetDirectoryName(definition.Module.FileName), RuntimeDirectory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Framework references of <paramref name="assembly"/> (names in <paramref name="frameworkNames"/>) whose copy
    /// actually loaded at run time is not the installed runtime's: a package can ship its own System.Linq.dll with a
    /// higher version and Microsoft's public key, and the .NET host then loads that copy instead. The test project
    /// references Core and the app, so every dependency that flows on to it (all of them today) is in its deps.json and
    /// is loaded here the way the app would load it; its file must be in the shared framework folder. A dependency an
    /// app project keeps private (PrivateAssets="all") would not flow here. A reference that cannot be loaded also counts.</summary>
    public static IReadOnlyCollection<string> FrameworkReferencesNotFromRuntime(Assembly assembly, IReadOnlyCollection<string> frameworkNames)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        using var module = Read(assembly);
        foreach (var reference in module.AssemblyReferences.Where(r => frameworkNames.Contains(r.Name)))
        {
            string? folder;
            try
            {
                folder = Path.GetDirectoryName(Assembly.Load(new AssemblyName(reference.FullName)).Location);
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                folder = null;
            }
            if (!string.Equals(folder, RuntimeDirectory, StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add($"<{reference.Name} {reference.Version} not loaded from the installed .NET runtime>");
            }
        }
        return offenders;
    }

    /// <summary>True when the assembly was compiled with unsafe code allowed ([module: UnverifiableCode]): raw pointers
    /// can reach any address, so no reference scan can vouch for it.</summary>
    public static bool AllowsUnsafeCode(Assembly assembly)
    {
        using var module = Read(assembly);
        return module.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Security.UnverifiableCodeAttribute");
    }

    public static IReadOnlyDictionary<string, SortedSet<string>> TypesReferencing(
        Assembly assembly, Func<TypeReference, bool> bannedType, Func<MemberReference, bool>? bannedMember = null)
    {
        var offenders = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        using var module = Read(assembly);
        foreach (var type in module.GetTypes())
        {
            var hits = new SortedSet<string>(StringComparer.Ordinal);
            Collect(type, bannedType, bannedMember ?? (_ => false), hits);
            if (hits.Count == 0) continue;
            var owner = Outermost(type).FullName;
            if (!offenders.TryGetValue(owner, out var all)) offenders[owner] = all = new SortedSet<string>(StringComparer.Ordinal);
            all.UnionWith(hits);
        }
        return offenders;
    }

    /// <summary>Types that reach code the type scan cannot see: a native call (DllImport, or LibraryImport, which
    /// generates one) into a DLL the predicate rejects, an [UnsafeAccessor] method (binds to any member by its name),
    /// a call through a raw address (calli), all always counted, or (when <paramref name="countComImports"/>) a COM class
    /// or interface ([ComImport]).</summary>
    public static IReadOnlyCollection<string> TypesCallingNative(Assembly assembly, Func<string, bool> bannedDll, bool countComImports)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        using var module = Read(assembly);
        foreach (var type in module.GetTypes())
        {
            var native = type.Methods.Any(m => (m.IsPInvokeImpl && m.PInvokeInfo is not null && bannedDll(m.PInvokeInfo.Module.Name))
                                               || m.CustomAttributes.Any(a => a.AttributeType.FullName == UnsafeAccessor)
                                               || (m.HasBody && m.Body.Instructions.Any(i => i.OpCode.Code == Code.Calli)));
            if (native || (countComImports && type.IsImport)) offenders.Add(Outermost(type).FullName);
        }
        return offenders;
    }

    private const string UnsafeAccessor = "System.Runtime.CompilerServices.UnsafeAccessorAttribute";

    /// <summary>The namespace of a type reference, taken from its outermost declaring type (nested types have none).</summary>
    public static string NamespaceOf(TypeReference reference)
    {
        while (reference.DeclaringType is not null) reference = reference.DeclaringType;
        return reference.Namespace;
    }

    /// <summary>The full name of a type without generic arguments (List`1, not List`1&lt;string&gt;).</summary>
    public static string OpenName(TypeReference reference) =>
        reference is GenericInstanceType generic ? generic.ElementType.FullName : reference.FullName;

    /// <summary>True when <paramref name="ns"/> is one of <paramref name="roots"/> or lies under one of them.</summary>
    public static bool InNamespace(string ns, IEnumerable<string> roots) =>
        roots.Any(root => ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal));

    private static void Collect(TypeDefinition type, Func<TypeReference, bool> bannedType, Func<MemberReference, bool> bannedMember, SortedSet<string> hits)
    {
        void Type(TypeReference? reference)
        {
            if (reference is not null) Mentions(reference, bannedType, hits);
        }

        void Argument(CustomAttributeArgument argument)
        {
            switch (argument.Value)
            {
                case TypeReference t: Type(t); break; // typeof(X) inside an attribute
                case CustomAttributeArgument inner: Argument(inner); break;
                case CustomAttributeArgument[] items: foreach (var item in items) Argument(item); break;
            }
        }

        void Attributes(Mono.Cecil.ICustomAttributeProvider provider)
        {
            foreach (var a in provider.CustomAttributes)
            {
                Type(a.AttributeType);
                foreach (var argument in a.ConstructorArguments) Argument(argument);
                foreach (var named in a.Fields.Concat(a.Properties)) Argument(named.Argument);
            }
        }

        Type(type.BaseType);
        foreach (var i in type.Interfaces) Type(i.InterfaceType);
        Attributes(type);
        foreach (var g in type.GenericParameters) Attributes(g);
        foreach (var f in type.Fields) { Type(f.FieldType); Attributes(f); }
        foreach (var p in type.Properties) { Type(p.PropertyType); Attributes(p); }
        foreach (var e in type.Events) { Type(e.EventType); Attributes(e); }
        foreach (var method in type.Methods)
        {
            Type(method.ReturnType);
            foreach (var p in method.Parameters) { Type(p.ParameterType); Attributes(p); }
            Attributes(method);
            Attributes(method.MethodReturnType);
            foreach (var g in method.GenericParameters) Attributes(g);
            if (!method.HasBody) continue;
            foreach (var v in method.Body.Variables) Type(v.VariableType);
            foreach (var instruction in method.Body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case MethodReference m:
                        if (bannedMember(m)) hits.Add($"{OpenName(m.DeclaringType)}::{m.Name}");
                        Type(m.DeclaringType);
                        Type(m.ReturnType);
                        foreach (var p in m.Parameters) Type(p.ParameterType);
                        if (m is GenericInstanceMethod g) foreach (var a in g.GenericArguments) Type(a);
                        break;
                    case FieldReference f:
                        if (bannedMember(f)) hits.Add($"{OpenName(f.DeclaringType)}::{f.Name}");
                        Type(f.DeclaringType);
                        Type(f.FieldType);
                        break;
                    case TypeReference t:
                        Type(t);
                        break;
                }
            }
        }
    }

    /// <summary>Adds every banned type the reference is, or wraps (array, by-ref, pointer, generic argument).</summary>
    private static void Mentions(TypeReference reference, Func<TypeReference, bool> bannedType, SortedSet<string> hits)
    {
        switch (reference)
        {
            case GenericInstanceType generic:
                Mentions(generic.ElementType, bannedType, hits);
                foreach (var a in generic.GenericArguments) Mentions(a, bannedType, hits);
                break;
            case FunctionPointerType pointer:
                hits.Add("function pointer " + pointer.FullName); // calls a raw address; never approved
                break;
            case TypeSpecification specification:
                Mentions(specification.ElementType, bannedType, hits);
                break;
            case GenericParameter:
                break;
            default:
                if (bannedType(reference)) hits.Add(reference.FullName);
                break;
        }
    }

    private static TypeDefinition Outermost(TypeDefinition type)
    {
        while (type.DeclaringType is not null) type = type.DeclaringType;
        return type;
    }
}
