using System.Windows;
using System.Windows.Threading;
using Boo.Core;
using Microsoft.Win32;

namespace Boo.App;

/// <summary>
/// Live Windows accessibility preferences. Reading never throws: a locked-down registry or a missing
/// WinRT surface simply yields the defaults (100% text, animations on).
/// </summary>
public static class AccessibilitySettings
{
    public const string TextScaleKeyPath = @"Software\Microsoft\Accessibility";
    public const string TextScaleValueName = "TextScaleFactor";

    private static global::Windows.UI.ViewManagement.UISettings? _uiSettings;
    private static readonly object Gate = new();
    private static readonly List<(Dispatcher Dispatcher, Action Callback)> Listeners = new();

    /// <summary>Settings &gt; Accessibility &gt; Text size, as a factor 1.0 .. 2.25.</summary>
    public static double TextScaleFactor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(TextScaleKeyPath, writable: false);
            return TextScale.FromPercent(key?.GetValue(TextScaleValueName) as int?);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return 1.0;
        }
    }

    /// <summary>Windows "Animation effects" off, or Boo's own Reduced motion setting.</summary>
    public static bool ReducedMotion(bool appReducedMotion) =>
        MotionPreference.ReducedMotion(SystemParameters.ClientAreaAnimation, appReducedMotion);

    /// <summary>
    /// Calls <paramref name="callback"/> on <paramref name="dispatcher"/> whenever the user changes the text
    /// size while Boo runs. Best effort: on systems without the WinRT UISettings surface nothing fires and
    /// the window re-reads the factor the next time it is shown.
    /// </summary>
    public static void WatchTextScale(Dispatcher dispatcher, Action callback)
    {
        lock (Gate)
        {
            Listeners.Add((dispatcher, callback));
            if (_uiSettings is not null)
            {
                return;
            }
            try
            {
                _uiSettings = new global::Windows.UI.ViewManagement.UISettings();
                _uiSettings.TextScaleFactorChanged += (_, _) => NotifyTextScaleChanged();
            }
            catch (Exception)
            {
                // WinRT not available (older Windows, server core, QA desktop): keep the registry value only.
                _uiSettings = null;
            }
        }
    }

    private static void NotifyTextScaleChanged()
    {
        (Dispatcher, Action)[] snapshot;
        lock (Gate)
        {
            snapshot = Listeners.ToArray();
        }
        foreach (var (dispatcher, callback) in snapshot)
        {
            if (dispatcher.HasShutdownStarted)
            {
                continue;
            }
            dispatcher.BeginInvoke(DispatcherPriority.Normal, callback);
        }
    }
}
