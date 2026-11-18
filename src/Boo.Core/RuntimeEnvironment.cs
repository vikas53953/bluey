namespace Boo.Core;

/// <summary>Single environment reader for explicit Core policies; callers own validation and defaults.</summary>
public static class RuntimeEnvironment
{
    public static string? Read(string name) => Environment.GetEnvironmentVariable(name);
}
