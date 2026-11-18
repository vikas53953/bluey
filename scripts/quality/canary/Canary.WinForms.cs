// Banned-API canary, Windows Forms part (docs/quality/RULES.md#canary): compiled only where UseWindowsForms is true.
// Same marker rules as Canary.Core.cs.
#pragma warning disable CA1416
namespace BooBannedApiCanary.WinForms;

internal static class Calls
{
    internal static void All()
    {
        global::System.Windows.Forms.Application.DoEvents(); // BAN M:System.Windows.Forms.Application.DoEvents
    }
}
