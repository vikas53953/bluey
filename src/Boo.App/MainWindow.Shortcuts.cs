using System.Windows;
using Boo.Core;

namespace Boo.App;

/// <summary>
/// Shortcut-conflict and crash-recovery surfacing for the panel. The wording comes from
/// <see cref="ShortcutStatus"/> and <see cref="CrashReport"/> in Boo.Core; this file only places it.
/// </summary>
public partial class MainWindow
{
    /// <summary>True when the Ctrl+Alt+Space low-level hook is installed.</summary>
    public bool HoldToTalkAvailable => _talkHook is not null;

    /// <summary>
    /// Shows which shortcuts another app owns. The settings line persists (ordinary status updates cannot hide it);
    /// the bottom status line gets the same sentence once so the user sees it without opening settings.
    /// </summary>
    public void ShowShortcutStatus(ShortcutStatus status, bool announce)
    {
        var message = status.PanelMessage;
        ShortcutStatusText.Text = message ?? string.Empty;
        ShortcutStatusText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        if (announce && message is not null)
        {
            StatusText.Text = message;
        }
    }

    /// <summary>Plain notice after a recovered or previous crash; never includes exception text.</summary>
    public void ShowCrashNotice(string notice)
    {
        StatusText.Text = notice;
    }
}
