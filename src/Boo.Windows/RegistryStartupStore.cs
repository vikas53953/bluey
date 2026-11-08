using Boo.Core;
using Microsoft.Win32;

namespace Boo.Windows;

/// <summary>IStartupRegistry over HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
public sealed class RegistryStartupStore : IStartupRegistry
{
    public string? Read(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistration.RunKeyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void Write(string valueName, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupRegistration.RunKeyPath, writable: true);
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    public void Delete(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistration.RunKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
