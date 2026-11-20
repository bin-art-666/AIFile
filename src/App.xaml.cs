using Microsoft.UI.Xaml;

namespace AIFile;

public partial class App : Application
{
    Window? window;

    public App()
    {
        UnhandledException += (_, e) => LogStartupError(e.Exception);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length == 3 && arguments[1] == "--verify") {
            try {
                PanelTheme.ApplyDefaults();
                var verification = new MainWindow(temporary: true);
                window = verification;
                verification.Activate();
                _ = verification.VerifyAsync(arguments[2]);
            }
            catch (Exception error) {
                LogStartupError(error);
                Exit();
            }
            return;
        }

        PanelTheme.ApplyDefaults();
        window = new MainWindow(initialPath: arguments.Length == 2 ? arguments[1] : null);
        window.Activate();
    }

    static void LogStartupError(Exception error)
    {
        System.Diagnostics.Trace.WriteLine(error);
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length != 3 || arguments[1] != "--verify" || !Path.IsPathFullyQualified(arguments[2])) return;
        try { File.AppendAllText(Path.Combine(arguments[2], "error.log"), error + Environment.NewLine); }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
    }
}
