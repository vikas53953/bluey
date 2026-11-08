using Velopack;

namespace Boo.App;

/// <summary>
/// Process entry point. Velopack must run before any WPF code: during install, uninstall and update the
/// installer starts Boo.exe with <c>--veloapp-*</c> arguments, expects the hooks to run, and exits. For a
/// normal launch <see cref="VelopackApp.Run"/> returns at once and WPF starts as before.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
