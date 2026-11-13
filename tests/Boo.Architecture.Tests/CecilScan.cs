using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Boo.Architecture.Tests;

/// <summary>
/// Reference scan over a compiled assembly (Mono.Cecil, the library NetArchTest is built on). A type references X when
/// X is its base type or an interface, the type of one of its fields, properties, events, parameters, return values or
/// locals, a custom attribute on it or its members, or when its code calls, reads, writes, constructs, casts to or
/// takes typeof of a member or type of X (generic arguments and arrays included: List&lt;X&gt;, X[]). Which X counts is
/// decided by the caller's predicate, so a rule can deny a whole namespace and allow a short list of harmless types
/// (default-deny), instead of listing the bad types one by one and missing one. Offenders are reported per outermost
/// type, so compiler-made nested types (lambdas, async state machines) count towards their owner. Not scanned (harmless
/// for the rules that use it): generic constraints, catch clause types, typeof inside attribute arguments.
/// </summary>
internal static class CecilScan
{
    public static IReadOnlyCollection<string> TypesReferencing(
        Assembly assembly, Func<TypeReference, bool> bannedType, Func<MethodReference, bool>? bannedMethod = null)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        using var module = ModuleDefinition.ReadModule(assembly.Location);
        foreach (var type in module.GetTypes())
        {
            if (References(type, bannedType, bannedMethod ?? (_ => false))) offenders.Add(Outermost(type).FullName);
        }
        return offenders;
    }

    /// <summary>Types that declare a native call (DllImport / LibraryImport) into a DLL the predicate rejects.</summary>
    public static IReadOnlyCollection<string> TypesCallingNative(Assembly assembly, Func<string, bool> bannedDll)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        using var module = ModuleDefinition.ReadModule(assembly.Location);
        foreach (var type in module.GetTypes())
        {
            if (type.Methods.Any(m => m.IsPInvokeImpl && m.PInvokeInfo is not null && bannedDll(m.PInvokeInfo.Module.Name)))
            {
                offenders.Add(Outermost(type).FullName);
            }
        }
        return offenders;
    }

    /// <summary>The namespace of a type reference, taken from its outermost declaring type (nested types have none).</summary>
    public static string NamespaceOf(TypeReference reference)
    {
        while (reference.DeclaringType is not null) reference = reference.DeclaringType;
        return reference.Namespace;
    }

    /// <summary>True when <paramref name="ns"/> is one of <paramref name="roots"/> or lies under one of them.</summary>
    public static bool InNamespace(string ns, IEnumerable<string> roots) =>
        roots.Any(root => ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal));

    private static bool References(TypeDefinition type, Func<TypeReference, bool> bannedType, Func<MethodReference, bool> bannedMethod)
    {
        bool Hit(TypeReference? reference) => reference is not null && Mentions(reference, bannedType);

        bool HitAttributes(Mono.Cecil.ICustomAttributeProvider provider) => provider.CustomAttributes.Any(a => Hit(a.AttributeType));

        if (Hit(type.BaseType) || type.Interfaces.Any(i => Hit(i.InterfaceType)) || HitAttributes(type)) return true;
        if (type.Fields.Any(f => Hit(f.FieldType) || HitAttributes(f))) return true;
        if (type.Properties.Any(p => Hit(p.PropertyType) || HitAttributes(p))) return true;
        if (type.Events.Any(e => Hit(e.EventType) || HitAttributes(e))) return true;
        foreach (var method in type.Methods)
        {
            if (Hit(method.ReturnType) || method.Parameters.Any(p => Hit(p.ParameterType)) || HitAttributes(method)) return true;
            if (!method.HasBody) continue;
            if (method.Body.Variables.Any(v => Hit(v.VariableType))) return true;
            foreach (var instruction in method.Body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case MethodReference m when bannedMethod(m) || Hit(m.DeclaringType) || Hit(m.ReturnType)
                                                || m.Parameters.Any(p => Hit(p.ParameterType))
                                                || (m is GenericInstanceMethod g && g.GenericArguments.Any(Hit)):
                        return true;
                    case FieldReference f when Hit(f.DeclaringType) || Hit(f.FieldType):
                        return true;
                    case TypeReference t when Hit(t):
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>True when the reference is a banned type, or wraps one (array, by-ref, pointer, generic argument).</summary>
    private static bool Mentions(TypeReference reference, Func<TypeReference, bool> bannedType)
    {
        switch (reference)
        {
            case GenericInstanceType generic:
                return Mentions(generic.ElementType, bannedType) || generic.GenericArguments.Any(a => Mentions(a, bannedType));
            case TypeSpecification specification:
                return Mentions(specification.ElementType, bannedType);
            case GenericParameter:
                return false;
            default:
                return bannedType(reference);
        }
    }

    private static TypeDefinition Outermost(TypeDefinition type)
    {
        while (type.DeclaringType is not null) type = type.DeclaringType;
        return type;
    }
}
