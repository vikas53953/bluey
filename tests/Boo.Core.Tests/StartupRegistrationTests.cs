using Boo.Core;

namespace Boo.Core.Tests;

public sealed class StartupRegistrationTests
{
    private const string Exe = @"C:\Users\vikas\AppData\Local\Programs\Boo\Boo.exe";
    private const string ExeWithSpaces = @"C:\Program Files\Boo App\Boo.exe";

    private sealed class FakeRegistry : IStartupRegistry
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];

        public string? Read(string valueName)
        {
            Calls.Add($"read:{valueName}");
            return Values.TryGetValue(valueName, out var value) ? value : null;
        }

        public void Write(string valueName, string command)
        {
            Calls.Add($"write:{valueName}");
            Values[valueName] = command;
        }

        public void Delete(string valueName)
        {
            Calls.Add($"delete:{valueName}");
            Values.Remove(valueName);
        }

        public int Mutations => Calls.Count(c => !c.StartsWith("read:", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_key_constants_target_hkcu_run_value_named_boo()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistration.RunKeyPath);
        Assert.Equal("Boo", StartupRegistration.ValueName);
        Assert.Equal("--background", StartupRegistration.BackgroundArgument);
    }

    [Fact]
    public void Command_quotes_the_path_and_adds_background_argument()
    {
        Assert.Equal("\"" + Exe + "\" --background", StartupRegistration.BuildCommand(Exe));
    }

    [Fact]
    public void Path_with_spaces_is_quoted()
    {
        Assert.Equal("\"C:\\Program Files\\Boo App\\Boo.exe\" --background", StartupRegistration.BuildCommand(ExeWithSpaces));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\Bad\"Path\\Boo.exe")]
    public void Invalid_exe_path_is_rejected(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() => StartupRegistration.BuildCommand(path));
    }

    [Fact]
    public void Null_exe_path_is_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => StartupRegistration.BuildCommand(null!));
    }

    [Fact]
    public void Enabled_and_absent_plans_a_write()
    {
        var change = StartupRegistration.Plan(true, ExeWithSpaces, currentValue: null);

        Assert.Equal(StartupChangeKind.Write, change.Kind);
        Assert.Equal("\"C:\\Program Files\\Boo App\\Boo.exe\" --background", change.Command);
    }

    [Fact]
    public void Enabled_and_already_registered_plans_nothing()
    {
        var change = StartupRegistration.Plan(true, Exe, "\"" + Exe + "\" --background");

        Assert.Equal(StartupChangeKind.None, change.Kind);
        Assert.Null(change.Command);
    }

    [Fact]
    public void Existing_value_matching_except_for_case_is_treated_as_registered()
    {
        var change = StartupRegistration.Plan(true, Exe, ("\"" + Exe + "\" --background").ToUpperInvariant());

        Assert.Equal(StartupChangeKind.None, change.Kind);
    }

    [Fact]
    public void Enabled_with_stale_path_plans_a_rewrite()
    {
        var change = StartupRegistration.Plan(true, Exe, "\"D:\\Old\\Boo.exe\" --background");

        Assert.Equal(StartupChangeKind.Write, change.Kind);
        Assert.Equal("\"" + Exe + "\" --background", change.Command);
    }

    [Fact]
    public void Enabled_with_unquoted_or_argumentless_value_plans_a_rewrite()
    {
        Assert.Equal(StartupChangeKind.Write, StartupRegistration.Plan(true, Exe, Exe).Kind);
        Assert.Equal(StartupChangeKind.Write, StartupRegistration.Plan(true, Exe, "\"" + Exe + "\"").Kind);
    }

    [Fact]
    public void Disabled_and_present_plans_a_removal()
    {
        var change = StartupRegistration.Plan(false, Exe, "\"" + Exe + "\" --background");

        Assert.Equal(StartupChangeKind.Remove, change.Kind);
        Assert.Null(change.Command);
    }

    [Fact]
    public void Disabled_and_absent_plans_nothing()
    {
        Assert.Equal(StartupChangeKind.None, StartupRegistration.Plan(false, Exe, null).Kind);
    }

    [Fact]
    public void Apply_enable_writes_once_and_is_idempotent()
    {
        var registry = new FakeRegistry();

        var first = StartupRegistration.Apply(true, ExeWithSpaces, registry);
        var second = StartupRegistration.Apply(true, ExeWithSpaces, registry);

        Assert.Equal(StartupChangeKind.Write, first.Kind);
        Assert.Equal(StartupChangeKind.None, second.Kind);
        Assert.Equal(1, registry.Mutations);
        Assert.Equal("\"C:\\Program Files\\Boo App\\Boo.exe\" --background", registry.Values["Boo"]);
    }

    [Fact]
    public void Apply_disable_removes_once_and_is_idempotent()
    {
        var registry = new FakeRegistry();
        registry.Values["Boo"] = "\"" + Exe + "\" --background";

        var first = StartupRegistration.Apply(false, Exe, registry);
        var second = StartupRegistration.Apply(false, Exe, registry);

        Assert.Equal(StartupChangeKind.Remove, first.Kind);
        Assert.Equal(StartupChangeKind.None, second.Kind);
        Assert.Equal(1, registry.Mutations);
        Assert.False(registry.Values.ContainsKey("Boo"));
        Assert.Contains("delete:Boo", registry.Calls);
    }

    [Fact]
    public void Apply_disabled_on_clean_registry_touches_nothing()
    {
        var registry = new FakeRegistry();

        var change = StartupRegistration.Apply(false, Exe, registry);

        Assert.Equal(StartupChangeKind.None, change.Kind);
        Assert.Equal(0, registry.Mutations);
    }

    [Fact]
    public void Apply_never_touches_other_run_values()
    {
        var registry = new FakeRegistry();
        registry.Values["OneDrive"] = "\"C:\\OneDrive\\OneDrive.exe\" /background";

        StartupRegistration.Apply(true, Exe, registry);
        StartupRegistration.Apply(false, Exe, registry);

        Assert.Equal("\"C:\\OneDrive\\OneDrive.exe\" /background", registry.Values["OneDrive"]);
        Assert.All(registry.Calls, c => Assert.EndsWith(":Boo", c, StringComparison.Ordinal));
    }
}
