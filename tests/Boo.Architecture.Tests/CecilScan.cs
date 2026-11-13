using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Boo.Architecture.Tests;

/// <summary>
/// Reference scan over a compiled assembly (Mono.Cecil, the library NetArchTest is built on). A type references X when
/// X is its base type or an interface, the type of one of its fields, properties, events, parameters, return values or
/// locals, a custom attribute on it or its members, or when its code calls, reads, writes, constructs, casts to or
/// takes typeof of a member or type of X (generic arguments and arrays included: List&lt;X&gt;, X[]). What counts is
/// decided by the caller's predicates (a type predicate, and a member predicate for calls and field reads), so a rule
/// can be an allowlist ("only these framework types") instead of a list of bad types that always misses one.
/// Results are per outermost type (compiler-made nested types such as lambdas and async state machines count towards
/// their owner), each with the references that hit, for the failure message. Not scanned: generic constraints, catch
/// clause types, typeof inside attribute arguments (none can do anything by itself), and compiled XAML (BAML).
/// </summary>
internal static class CecilScan
{
    public static IReadOnlyDictionary<string, SortedSet<string>> TypesReferencing(
        Assembly assembly, Func<TypeReference, bool> bannedType, Func<MemberReference, bool>? bannedMember = null)
    {
        var offenders = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        using var module = ModuleDefinition.ReadModule(assembly.Location);
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
    /// generates one) into a DLL the predicate rejects, an [UnsafeAccessor] method (binds to any member by its name,
    /// always counted), or (when <paramref name="countComImports"/>) a COM class or interface ([ComImport]).</summary>
    public static IReadOnlyCollection<string> TypesCallingNative(Assembly assembly, Func<string, bool> bannedDll, bool countComImports)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        using var module = ModuleDefinition.ReadModule(assembly.Location);
        foreach (var type in module.GetTypes())
        {
            var native = type.Methods.Any(m => (m.IsPInvokeImpl && m.PInvokeInfo is not null && bannedDll(m.PInvokeInfo.Module.Name))
                                               || m.CustomAttributes.Any(a => a.AttributeType.FullName == UnsafeAccessor));
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

        void Attributes(Mono.Cecil.ICustomAttributeProvider provider)
        {
            foreach (var a in provider.CustomAttributes) Type(a.AttributeType);
        }

        Type(type.BaseType);
        foreach (var i in type.Interfaces) Type(i.InterfaceType);
        Attributes(type);
        foreach (var f in type.Fields) { Type(f.FieldType); Attributes(f); }
        foreach (var p in type.Properties) { Type(p.PropertyType); Attributes(p); }
        foreach (var e in type.Events) { Type(e.EventType); Attributes(e); }
        foreach (var method in type.Methods)
        {
            Type(method.ReturnType);
            foreach (var p in method.Parameters) Type(p.ParameterType);
            Attributes(method);
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
