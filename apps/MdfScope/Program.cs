using Avalonia;

namespace MdfScope;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Command line: MdfScope [file] | MdfScope --demo <path> (writes the demo measurement and exits)
        if (args.Length == 2 && args[0] == "--demo")
        {
            DemoFile.Write(args[1]);
            return 0;
        }

        if (args.Length >= 5 && args[0] == "--capture")
            return HeadlessCapture.Run(args[1], int.Parse(args[2]), double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
                args[4], args.Contains("--listen-only"));

        // MdfScope --check <files...>: open each file, load every channel, print a summary (no window)
        if (args.Length >= 2 && args[0] == "--check")
        {
            foreach (var path in args.Skip(1)) Check(path);
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    static void Check(string path)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var doc = MdfDocument.Open(path);
            int loaded = 0, failed = 0, unsupported = 0;
            long samples = 0;
            string? firstError = null;
            foreach (var (_, cg) in doc.ChannelGroups)
            foreach (var ch in cg.Channels.Where(c => !c.IsMaster))
            {
                if (!ch.CanReadRaw) { unsupported++; continue; }
                try { samples += doc.Load(cg, ch).X.Length; loaded++; }
                catch (Exception e) { failed++; firstError ??= $"{ch.Name}: {e.GetType().Name}: {e.Message}"; }
            }
            Console.WriteLine($"{Path.GetFileName(path)}: MDF {doc.Header.Version}, {doc.ChannelGroups.Count()} groups, " +
                $"{loaded} channels ({samples:N0} samples), {unsupported} unsupported, {failed} failed, " +
                $"model {(doc.FileModelError == null ? "ok" : "error: " + doc.FileModelError)}, {watch.ElapsedMilliseconds} ms" +
                (firstError != null ? Environment.NewLine + "    " + firstError : ""));
        }
        catch (Exception e)
        {
            Console.WriteLine($"{Path.GetFileName(path)}: cannot open: {e.GetType().Name}: {e.Message}");
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
