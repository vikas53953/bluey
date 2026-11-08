using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Boo.Ui.Tests;

/// <summary>Runs a class's facts in ascending <see cref="StepAttribute"/> order (one live app, one story).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StepAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}

public sealed class StepOrderer : ITestCaseOrderer
{
    public const string TypeName = "Boo.Ui.Tests.StepOrderer";
    public const string AssemblyName = "Boo.Ui.Tests";

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase =>
        testCases.OrderBy(t => OrderOf(t)).ThenBy(t => t.TestMethod.Method.Name, StringComparer.Ordinal);

    private static int OrderOf(ITestCase testCase) =>
        testCase.TestMethod.Method
            .GetCustomAttributes(typeof(StepAttribute).AssemblyQualifiedName)
            .Select(a => (int)a.GetConstructorArguments().First())
            .DefaultIfEmpty(int.MaxValue)
            .First();
}
