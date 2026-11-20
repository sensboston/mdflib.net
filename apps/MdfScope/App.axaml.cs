using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace MdfScope;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Where unexpected errors are written: %LOCALAPPDATA%/MdfScope/error.log.</summary>
    public static string ErrorLog => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdfScope", "error.log");

    public static void LogError(Exception e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLog)!);
            File.AppendAllText(ErrorLog, $"{DateTime.Now:O} {e}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // An unexpected error in the UI is logged and reported instead of closing the window silently.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            LogError(e.Exception);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow w })
                w.ReportError(e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) LogError(ex); };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            string[] args = desktop.Args ?? [];
            string? Option(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            if (Option("--capture-demo") is { } demoShot)
            {
                if (Option("--size")?.Split('x') is [var w2, var h2]) { window.Width = double.Parse(w2); window.Height = double.Parse(h2); }
                window.RunCaptureDemo(Option("--dbc") ?? Path.Combine(AppContext.BaseDirectory, "Demo", "demo-vehicle.dbc"),
                    Option("--plot") ?? "VehicleSpeed,EngineSpeed", double.Parse(Option("--seconds") ?? "12", System.Globalization.CultureInfo.InvariantCulture), demoShot);
            }
            else if (args.Length > 0 && File.Exists(args[0]))
            {
                if (Option("--screenshot") is { } shot)
                {
                    if (Option("--size")?.Split('x') is [var w, var h]) { window.Width = double.Parse(w); window.Height = double.Parse(h); }
                    window.RunScript(args[0], Option("--plot") ?? string.Empty, Option("--cursor"), Option("--range"), shot,
                        Array.IndexOf(args, "--file-tab") >= 0, Option("--dbc"));
                }
                else window.OpenOnStart(args[0]);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
