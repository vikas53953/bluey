using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Boo.Architecture.Tests;

/// <summary>
/// Exact-type reference scan over a compiled assembly (Mono.Cecil, the library NetArchTest is built on). A type
/// references X when X is the type of one of its fields, properties, parameters, return values or locals, or when its
/// code calls, reads, writes or constructs a member declared on X. Generic arguments and arrays count (List&lt;X&gt;,
/// X[]). Offenders are reported per outermost type, so compiler-made nested types count towards their owner.
/// </summary>
internal static class CecilScan
{
    public static IReadOnlyCollection<string> TypesReferencing(Assembly assembly, params string[] typeFullNames)
    {
        var wanted = new HashSet<string>(typeFullNames, StringComparer.Ordinal);
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        using var module = ModuleDefinition.ReadModule(assembly.Location);
        foreach (var type in module.GetTypes())
        {
            if (References(type, wanted)) offenders.Add(Outermost(type).FullName);
        }
        return offenders;
    }

    private static bool References(TypeDefinition type, HashSet<string> wanted)
    {
        bool Hit(TypeReference? reference) => reference is not null && Mentions(reference, wanted);

        if (Hit(type.BaseType) || type.Interfaces.Any(i => Hit(i.InterfaceType))) return true;
        if (type.Fields.Any(f => Hit(f.FieldType))) return true;
        if (type.Properties.Any(p => Hit(p.PropertyType))) return true;
        foreach (var method in type.Methods)
        {
            if (Hit(method.ReturnType) || method.Parameters.Any(p => Hit(p.ParameterType))) return true;
            if (!method.HasBody) continue;
            if (method.Body.Variables.Any(v => Hit(v.VariableType))) return true;
            foreach (var instruction in method.Body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case MethodReference m when Hit(m.DeclaringType) || Hit(m.ReturnType) || m.Parameters.Any(p => Hit(p.ParameterType)):
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

    /// <summary>True when the reference is one of the wanted types, or wraps one (array, generic argument, pointer).</summary>
    private static bool Mentions(TypeReference reference, HashSet<string> wanted)
    {
        switch (reference)
        {
            case GenericInstanceType generic:
                return Mentions(generic.ElementType, wanted) || generic.GenericArguments.Any(a => Mentions(a, wanted));
            case TypeSpecification specification:
                return Mentions(specification.ElementType, wanted);
            default:
                return wanted.Contains(reference.FullName);
        }
    }

    private static TypeDefinition Outermost(TypeDefinition type)
    {
        while (type.DeclaringType is not null) type = type.DeclaringType;
        return type;
    }
}
