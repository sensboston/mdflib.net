using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MdfLib;
using MdfScope.Can;
using VAlign = Avalonia.Layout.VerticalAlignment;
using HAlign = Avalonia.Layout.HorizontalAlignment;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;

namespace MdfScope;

/// <summary>
/// The Capture tab. Frames are kept in memory while capturing; with a DBC loaded the signal tree shows every
/// message and signal of the database with its current value, and ticked signals are plotted live. Save writes
/// the captured frames as an ASAM MF4 bus log and opens it, decoded with the same DBC.
/// </summary>
public sealed class CaptureView : DockPanel
{
    static readonly string[] Palette =
        ["#4FC3F7", "#FFB74D", "#81C784", "#E57373", "#BA68C8", "#FFF176", "#4DB6AC", "#F06292"];

    readonly ComboBox adapterBox = new() { MinWidth = 280 };
    readonly ComboBox bitrateBox = new() { MinWidth = 110 };
    readonly CheckBox listenOnly = new() { Content = "Listen only" };
    readonly Button refresh = new() { Content = "↻" };
    readonly Button loadDbc = new() { Content = "Load DBC…" };
    readonly TextBlock dbcName = new() { Text = "no DBC: raw frames only", Opacity = 0.6, VerticalAlignment = VAlign.Center };
    readonly Button startStop = new() { Content = "Start", MinWidth = 80 };
    readonly Button clear = new() { Content = "Clear" };
    readonly Button save = new() { Content = "Save MF4…", IsEnabled = false };
    readonly TextBlock stats = new() { Opacity = 0.85, VerticalAlignment = VAlign.Center };
    readonly TreeView signalTree = new();
    readonly TextBlock treeHint = new() { Text = "Load a DBC to see messages and signals.", Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
    readonly StackPanel rows = new();
    readonly AvaPlot plot = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly IReadOnlyList<ICanAdapterProvider> providers;
    readonly string pluginProblems;
    readonly object gate = new();

    // Captured data (guarded by gate): the frames for saving, per-identifier rows, per-signal series.
    readonly List<CanFrame> frames = new();
    readonly Dictionary<(uint, bool), Row> byId = new();
    readonly Dictionary<DbcSignal, Series> series = new();
    readonly Dictionary<DbcSignal, SignalItem> items = new();
    DbcDatabase? dbc;
    ulong firstNs;
    long lastCount;

    ICanChannel? channel;
    CancellationTokenSource? cts;
    Task? loop;
    Stopwatch runClock = new();
    string? lastError;
    int colorIndex;

    /// <summary>Raised after Save with the written file and the DBC used for decoding (or null).</summary>
    public event Action<string, string?>? Saved;

    /// <summary>Raised after Stop with the capture written to a temporary MF4 file, and the DBC (or null).</summary>
    public event Action<string, string?>? Stopped;

    /// <summary>Asked before a capture starts; returning false cancels the start (the window closes its file first).</summary>
    public Func<Task<bool>>? BeforeStart { get; set; }

    readonly TextBlock rawNote = new()
    {
        Text = "Raw capture: frames are recorded and listed below.\nLoad a DBC to see and plot signals.",
        TextAlignment = TextAlignment.Center, Opacity = 0.6, FontSize = 15,
        HorizontalAlignment = HAlign.Center, VerticalAlignment = VAlign.Center,
    };

    public bool IsCapturing => channel != null;

    /// <summary>Width that shows the DBC tree's longest row with its value; 0 without a DBC.</summary>
    public double PreferredTreeWidth { get; private set; }

    /// <summary>Raised when the DBC tree was rebuilt (its preferred width may have changed).</summary>
    public event Action? TreeChanged;

    public CaptureView()
    {
        providers = PluginHost.LoadProviders(out var problems);
        pluginProblems = string.Join("; ", problems);

        // Card 1: setup (adapter, bit rate, mode, DBC) and session (start/clear/save, status).
        var setup = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 8, ColumnSpacing = 12 };
        var adapterRow = Row3(adapterBox, refresh, Label("Bit rate"), bitrateBox, listenOnly);
        var dbcRow = Row3(loadDbc, dbcName);
        var sessionRow = Row3(startStop, clear, save, stats);
        AddRow(setup, 0, "Adapter", adapterRow);
        AddRow(setup, 1, "Database", dbcRow);
        AddRow(setup, 2, "Capture", sessionRow);
        startStop.Classes.Add("accent");
        var setupCard = Card(setup);
        setupCard.Margin = new Thickness(0, 0, 0, 8);
        SetDock(setupCard, Dock.Top);
        Children.Add(setupCard);

        // Card 2: live plot. Card 3: frames by identifier.
        var plotHost = new Grid();
        plotHost.Children.Add(plot);
        plotHost.Children.Add(rawNote);
        var plotCard = Card(plotHost, padding: 4);
        var table = new DockPanel();
        var title = new TextBlock { Text = "Frames", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(title, Dock.Top);
        table.Children.Add(title);
        var header = RowGrid(true, "Identifier", "Message", "DLC", "Data", "Count", "Rate");
        DockPanel.SetDock(header, Dock.Top);
        table.Children.Add(header);
        table.Children.Add(new ScrollViewer { Content = rows });
        var tableCard = Card(table);

        var body = new Grid { RowDefinitions = new RowDefinitions("3*,8,2*") };
        Grid.SetRow(plotCard, 0);
        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brushes.Transparent };
        Grid.SetRow(split, 1);
        Grid.SetRow(tableCard, 2);
        body.Children.Add(plotCard);
        body.Children.Add(split);
        body.Children.Add(tableCard);
        Children.Add(body);

        // The signal tree lives in the window's left panel (like the file's channels on the Signals tab).
        DockPanel.SetDock(treeHint, Dock.Top);
        SignalPanel.Children.Add(treeHint);
        SignalPanel.Children.Add(signalTree);

        ToolTip.SetTip(listenOnly, "Receive without acknowledging frames. Only for a bus that already has other nodes; a single ECU on a bench needs this off.");
        foreach (int rate in new[] { 1_000_000, 800_000, 500_000, 250_000, 125_000, 100_000, 83_333, 50_000, 33_333, 20_000, 10_000 })
            bitrateBox.Items.Add(new ComboBoxItem { Content = rate >= 1_000_000 ? "1 Mbit/s" : $"{rate / 1000.0:0.###} kbit/s", Tag = rate });
        bitrateBox.SelectedIndex = 3;

        plot.Plot.Axes.Bottom.Label.Text = "Time [s]";
        plot.Plot.Legend.IsVisible = true;
        plot.Plot.Legend.Alignment = ScottPlot.Alignment.UpperLeft;
        ApplyTheme(true);

        refresh.Click += (_, _) => FillAdapters();
        loadDbc.Click += async (_, _) => await PickDbc();
        startStop.Click += async (_, _) => { if (channel == null) await StartAsync(); else await StopAsync(); };
        clear.Click += (_, _) => Clear();
        save.Click += async (_, _) => await SaveAsync();
        timer.Tick += (_, _) => UpdateView();
        FillAdapters();
        if (Settings.Current.LastDbc is { } last && File.Exists(last)) LoadDbc(last);
        UpdateMode();
    }

    /// <summary>Without a DBC the capture is raw: the start button and the plot say so.</summary>
    void UpdateMode()
    {
        rawNote.IsVisible = dbc == null;
        plot.IsVisible = dbc != null;
        if (channel == null) startStop.Content = dbc == null ? "Start (raw)" : "Start";
    }

    static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VAlign.Center, Opacity = 0.75 };

    /// <summary>The DBC signal tree with live values, shown in the window's left panel on this tab.</summary>
    public DockPanel SignalPanel { get; } = new();

    /// <summary>Hides the signals whose name does not contain the text (the left panel's filter box).</summary>
    public void Filter(string text)
    {
        foreach (var item in items.Values)
            if (item.Container != null)
                item.Container.IsVisible = text.Length == 0 || item.Signal.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    static StackPanel Row3(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in controls) row.Children.Add(c);
        return row;
    }

    static void AddRow(Grid grid, int row, string label, Control content)
    {
        var l = new TextBlock { Text = label, Opacity = 0.6, VerticalAlignment = VAlign.Center };
        Grid.SetRow(l, row);
        Grid.SetRow(content, row);
        Grid.SetColumn(content, 1);
        grid.Children.Add(l);
        grid.Children.Add(content);
    }

    static Border Card(Control child, double padding = 12)
    {
        var card = new Border { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(6), ClipToBounds = true };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("PanelBackground"));
        return card;
    }

    // ---------------------------------------------------------------- adapters

    void FillAdapters()
    {
        adapterBox.Items.Clear();
        foreach (var p in providers)
        {
            if (!p.IsAvailable)
            {
                adapterBox.Items.Add(new ComboBoxItem { Content = $"{p.Name}: {p.UnavailableReason}", IsEnabled = false });
                continue;
            }
            IReadOnlyList<CanDevice> devices;
            try { devices = p.GetDevices(); }
            catch (Exception e) when (e is CanAdapterException or IOException or InvalidOperationException)
            {
                adapterBox.Items.Add(new ComboBoxItem { Content = $"{p.Name}: {e.Message}", IsEnabled = false });
                continue;
            }
            if (devices.Count == 0)
                adapterBox.Items.Add(new ComboBoxItem { Content = $"{p.Name}: no device attached", IsEnabled = false });
            foreach (var d in devices)
                adapterBox.Items.Add(new ComboBoxItem { Content = $"{p.Name} · {d.DisplayName}{(d.IsInUse ? " (in use)" : "")}", Tag = (p, d) });
        }
        int real = -1, demo = -1;
        for (int i = 0; i < adapterBox.Items.Count; i++)
        {
            if (adapterBox.Items[i] is not ComboBoxItem { Tag: (ICanAdapterProvider p, CanDevice _) }) continue;
            if (p is DemoBusProvider) { if (demo < 0) demo = i; }
            else if (real < 0) real = i;
        }
        adapterBox.SelectedIndex = real >= 0 ? real : demo;
        stats.Text = pluginProblems.Length > 0 ? "Plugin problems: " + pluginProblems : "Ready";
    }

    // ---------------------------------------------------------------- DBC

    async Task PickDbc()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load a DBC",
            SuggestedStartLocation = await Settings.StartIn(storage, Settings.Current.DbcFolder),
            FileTypeFilter = [new FilePickerFileType("DBC files") { Patterns = ["*.dbc", "*.DBC"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            Settings.Remember((s, d) => s.DbcFolder = d, path);
            LoadDbc(path);
        }
    }

    /// <summary>Loads a DBC; the tree shows its messages and signals at once, before any frame arrives.</summary>
    public void LoadDbc(string path)
    {
        DbcDatabase loaded;
        try { loaded = DbcDatabase.Load(path); }
        catch (Exception e) when (e is IOException or FormatException or ArgumentException or InvalidOperationException)
        {
            stats.Text = "Cannot load the DBC: " + e.Message;
            return;
        }
        lock (gate)
        {
            dbc = loaded;
            series.Clear();
            // Already captured frames are decoded with the new database too.
            foreach (var f in frames) Decode(f);
        }
        Settings.Current.LastDbc = path;
        Settings.Current.Save();
        dbcName.Text = $"{loaded.Name}: {loaded.Messages.Count} messages";
        dbcName.Opacity = 0.85;
        BuildSignalTree();
        ResetPlot();
        UpdateMode();
    }

    void BuildSignalTree()
    {
        items.Clear();
        var groups = new List<TreeViewItem>();
        if (dbc != null)
        {
            foreach (var m in dbc.Messages.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                header.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeight.SemiBold });
                header.Children.Add(new TextBlock { Text = m.IsExtended ? m.Id.ToString("X8") : m.Id.ToString("X3"), Opacity = 0.55, FontSize = 11, VerticalAlignment = VAlign.Center });
                var group = new TreeViewItem { Header = header, IsExpanded = true };
                var children = new List<TreeViewItem>();
                foreach (var s in m.Signals)
                {
                    var item = new SignalItem(s);
                    item.Box.IsCheckedChanged += (_, _) => { if (item.Box.IsChecked == true) AddToPlot(item); else RemoveFromPlot(item); };
                    items[s] = item;
                    var tvi = new TreeViewItem { Header = item.View };
                    item.Container = tvi;
                    if (s.Comment.Length > 0) ToolTip.SetTip(tvi, s.Comment);
                    children.Add(tvi);
                }
                group.ItemsSource = children;
                groups.Add(group);
            }
        }
        signalTree.ItemsSource = groups;
        PreferredTreeWidth = dbc == null ? 0 : MainWindow.PreferredWidth(
            dbc.Messages.SelectMany(m => new[] { ($"{m.Name}  {m.Id:X8}", true) }
                .Concat(m.Signals.Select(s => ($"{s.Name}  {s.Unit}  -00000.000 {s.Unit}", false)))));
        TreeChanged?.Invoke();
        treeHint.IsVisible = dbc == null;
    }

    /// <summary>Scripted demo (screenshots): the simulated bus, a DBC, some ticked signals, capturing.</summary>
    public void StartDemo(string dbcPath, IEnumerable<string> signals)
    {
        for (int i = 0; i < adapterBox.Items.Count; i++)
            if (adapterBox.Items[i] is ComboBoxItem { Tag: (DemoBusProvider, CanDevice) }) adapterBox.SelectedIndex = i;
        string? remembered = Settings.Current.LastDbc;   // a scripted run must not change the user's last DBC
        LoadDbc(dbcPath);
        Settings.Current.LastDbc = remembered;
        Settings.Current.Save();
        foreach (var name in signals)
            foreach (var item in items.Values.Where(x => x.Signal.Name == name)) item.Box.IsChecked = true;
        _ = StartAsync();
    }

    // ---------------------------------------------------------------- capture

    async Task StartAsync()
    {
        if (adapterBox.SelectedItem is not ComboBoxItem { Tag: (ICanAdapterProvider provider, CanDevice device) }) return;
        if (BeforeStart != null && !await BeforeStart()) return;
        Clear();
        int bitrate = bitrateBox.SelectedItem is ComboBoxItem { Tag: int r } ? r : 500_000;
        lastError = null;
        try
        {
            channel = provider.Open(device, new CanChannelOptions(bitrate, listenOnly.IsChecked == true));
        }
        catch (Exception e) when (e is CanAdapterException or DllNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            stats.Text = "Cannot open: " + e.Message;
            return;
        }

        cts = new CancellationTokenSource();
        var token = cts.Token;
        var ch = channel;
        loop = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in ch.ReadAllAsync(token).ConfigureAwait(false))
                    Accept(frame);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is CanAdapterException or IOException or InvalidOperationException) { lastError = e.Message; }
        });
        runClock = Stopwatch.StartNew();
        timer.Start();
        startStop.Content = "Stop";
        SetControls(capturing: true);
    }

    void Accept(in CanFrame frame)
    {
        lock (gate)
        {
            if (frames.Count == 0) firstNs = frame.TimestampNs;
            frames.Add(frame);
            var key = (frame.Id, frame.IsExtended);
            if (!byId.TryGetValue(key, out var row))
                byId[key] = row = new Row(frame.Id, frame.IsExtended, dbc?.Find(frame.Id, frame.IsExtended)?.Name ?? "");
            row.Update(frame);
            Decode(frame);
        }
    }

    // Caller holds the gate.
    void Decode(in CanFrame frame)
    {
        if (dbc == null || frame.Kind != CanFrameKind.Data) return;
        var message = dbc.Find(frame.Id, frame.IsExtended);
        if (message == null) return;
        double t = (frame.TimestampNs - firstNs) / 1e9;
        message.Decode(frame.Data, (signal, value) =>
        {
            if (!series.TryGetValue(signal, out var s)) series[signal] = s = new Series();
            s.X.Add(t);
            s.Y.Add(value);
        });
    }

    /// <summary>Stops capturing; unless the window is closing, the capture is handed over as a temporary MF4 file.</summary>
    public async Task StopAsync(bool closing = false)
    {
        if (channel == null) return;
        timer.Stop();
        cts?.Cancel();
        channel.Dispose();
        if (loop != null) await loop.ConfigureAwait(true);
        channel = null;
        UpdateView();
        SetControls(capturing: false);
        UpdateMode();
        if (closing) return;

        List<CanFrame> copy;
        ulong first;
        lock (gate) { copy = frames.ToList(); first = firstNs; }
        if (copy.Count == 0) return;
        string folder = Path.Combine(Path.GetTempPath(), "MdfScope");
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.mf4");
        string network = (adapterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CAN";
        try { await Task.Run(() => WriteBusLog(temp, copy, first, network)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or MdfFormatException)
        {
            stats.Text = "Cannot hand over the capture: " + e.Message;
            return;
        }
        Stopped?.Invoke(temp, dbc?.Path);
    }

    void Clear()
    {
        lock (gate)
        {
            frames.Clear();
            byId.Clear();
            series.Clear();
            lastCount = 0;
        }
        rows.Children.Clear();
        ResetPlot();
        foreach (var item in items.Values) item.SetValue("–", false);
        save.IsEnabled = false;
        stats.Text = "Cleared";
    }

    void SetControls(bool capturing)
    {
        adapterBox.IsEnabled = bitrateBox.IsEnabled = listenOnly.IsEnabled = refresh.IsEnabled = clear.IsEnabled = !capturing;
        save.IsEnabled = !capturing && frames.Count > 0;
    }

    // ---------------------------------------------------------------- view refresh (UI thread, 5 Hz)

    void UpdateView()
    {
        List<Row> snapshot;
        long count;
        var latest = new Dictionary<DbcSignal, double>();
        lock (gate)
        {
            snapshot = byId.Values.OrderBy(r => r.Extended).ThenBy(r => r.Id).ToList();
            count = frames.Count;
            foreach (var r in snapshot) r.Snapshot();
            foreach (var (signal, s) in series)
                if (s.Y.Count > 0) latest[signal] = s.Y[^1];
            foreach (var item in items.Values)
                if (item.Logger != null && series.TryGetValue(item.Signal, out var s))
                    item.Feed(s);
        }

        double seconds = runClock.Elapsed.TotalSeconds;
        long delta = count - lastCount;
        lastCount = count;
        string bus = channel?.BusState ?? "stopped";
        double memoryMb = count * 48 / 1e6;
        stats.Text = lastError ?? $"{count:N0} frames  ·  {(channel != null ? delta / timer.Interval.TotalSeconds : 0):N0}/s  ·  {snapshot.Count} identifiers  ·  bus {bus}  ·  {seconds:0} s  ·  ~{memoryMb:0.#} MB in memory";

        if (rows.Children.Count != snapshot.Count)
        {
            rows.Children.Clear();
            foreach (var r in snapshot) rows.Children.Add(r.View);
        }
        foreach (var r in snapshot) r.Render();

        foreach (var (signal, item) in items)
            if (latest.TryGetValue(signal, out double v)) item.SetValue(Format(signal, v), true);

        if (items.Values.Any(i => i.Logger != null))
        {
            double end = series.Values.Where(s => s.X.Count > 0).Select(s => s.X[^1]).DefaultIfEmpty(0).Max();
            plot.Plot.Axes.SetLimitsX(Math.Max(0, end - 30), Math.Max(30, end));
            foreach (var axis in unitAxes.Values) plot.Plot.Axes.AutoScaleY(axis);
            plot.Refresh();
        }
    }

    static string Format(DbcSignal s, double v)
    {
        string? text = s.TextOf(v);
        if (text != null) return text;
        string number = Math.Abs(v) >= 1e6 || (Math.Abs(v) < 1e-3 && v != 0) ? v.ToString("G6", CultureInfo.InvariantCulture) : v.ToString("0.###", CultureInfo.InvariantCulture);
        return s.Unit.Length > 0 ? $"{number} {s.Unit}" : number;
    }

    // ---------------------------------------------------------------- live plot

    void AddToPlot(SignalItem item)
    {
        var color = ScottPlot.Color.FromHex(Palette[colorIndex++ % Palette.Length]);
        var logger = plot.Plot.Add.DataLogger();
        logger.Color = color;
        logger.LineWidth = 1.5f;
        logger.LegendText = item.Signal.Unit.Length > 0 ? $"{item.Signal.Name} [{item.Signal.Unit}]" : item.Signal.Name;
        logger.ManageAxisLimits = false;
        // One Y axis per unit: the first on the left, every further unit gets its own axis on the right.
        string unit = item.Signal.Unit;
        if (!unitAxes.TryGetValue(unit, out var axis))
        {
            axis = unitAxes.Count == 0 ? plot.Plot.Axes.Left : plot.Plot.Axes.AddRightAxis();
            axis.Label.Text = unit;
            unitAxes[unit] = axis;
        }
        logger.Axes.YAxis = axis;
        item.Logger = logger;
        item.Fed = 0;
        item.SetColor(Avalonia.Media.Color.FromRgb(color.R, color.G, color.B));
        lock (gate)
            if (series.TryGetValue(item.Signal, out var s)) item.Feed(s);
        plot.Plot.Axes.AutoScale();
        plot.Refresh();
    }

    void RemoveFromPlot(SignalItem item)
    {
        if (item.Logger != null) plot.Plot.Remove(item.Logger);
        item.Logger = null;
        item.SetColor(null);
        // An axis whose unit no plotted signal uses any more goes away with the last one.
        string unit = item.Signal.Unit;
        if (unitAxes.TryGetValue(unit, out var axis) && !items.Values.Any(i => i.Logger != null && i.Signal.Unit == unit))
        {
            if (axis == plot.Plot.Axes.Left) axis.Label.Text = string.Empty;
            else plot.Plot.Axes.Remove(axis);
            unitAxes.Remove(unit);
        }
        plot.Refresh();
    }

    readonly Dictionary<string, ScottPlot.IYAxis> unitAxes = new();

    void ResetPlot()
    {
        plot.Plot.Clear();
        foreach (var axis in unitAxes.Values.Where(a => a != plot.Plot.Axes.Left)) plot.Plot.Axes.Remove(axis);
        unitAxes.Clear();
        plot.Plot.Axes.Left.Label.Text = string.Empty;
        foreach (var item in items.Values) { item.Logger = null; item.Fed = 0; }
        foreach (var item in items.Values.Where(i => i.Box.IsChecked == true).ToList()) AddToPlot(item);
        plot.Refresh();
    }

    public void ApplyTheme(bool dark)
    {
        var p = plot.Plot;
        p.FigureBackground.Color = ScottPlot.Color.FromHex(dark ? "#1E1F22" : "#FFFFFF");
        p.DataBackground.Color = ScottPlot.Color.FromHex(dark ? "#232529" : "#FBFBFC");
        p.Axes.Color(ScottPlot.Color.FromHex(dark ? "#B9BEC6" : "#3C4048"));
        p.Grid.MajorLineColor = ScottPlot.Color.FromHex(dark ? "#33363C" : "#E6E8EB");
        p.Legend.BackgroundColor = ScottPlot.Color.FromHex(dark ? "#2B2D31" : "#FFFFFF").WithAlpha(0.9);
        p.Legend.FontColor = ScottPlot.Color.FromHex(dark ? "#D5D8DD" : "#202226");
        p.Legend.OutlineColor = ScottPlot.Color.FromHex(dark ? "#3A3D43" : "#D0D3D8");
        plot.Refresh();
    }

    // ---------------------------------------------------------------- save

    async Task SaveAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the capture as MF4",
            SuggestedStartLocation = await Settings.StartIn(storage, Settings.Current.MeasurementFolder),
            SuggestedFileName = $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.mf4",
            DefaultExtension = "mf4",
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        Settings.Remember((s, d) => s.MeasurementFolder = d, path);

        List<CanFrame> copy;
        ulong first;
        lock (gate) { copy = frames.ToList(); first = firstNs; }
        string network = (adapterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CAN";
        try
        {
            await Task.Run(() => WriteBusLog(path, copy, first, network));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or MdfFormatException)
        {
            stats.Text = "Save failed: " + e.Message;
            return;
        }
        stats.Text = $"Saved {copy.Count:N0} frames to {Path.GetFileName(path)}";
        Saved?.Invoke(path, dbc?.Path);
    }

    static void WriteBusLog(string path, IReadOnlyList<CanFrame> list, ulong firstNs, string network)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var logger = MdfBusLogger.Create(stream, MdfBusType.Can, leaveOpen: true, new MdfBusLoggerOptions
        {
            Version = "4.10",
            StartTimeNanoseconds = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L),
            NetworkName = network,
            CompressData = true,
        });
        logger.StartMeasurement();
        foreach (var f in list)
        {
            logger.Write(new MdfBusMessage
            {
                Type = f.Kind switch
                {
                    CanFrameKind.Remote => MdfBusMessageType.CanRemoteFrame,
                    CanFrameKind.Error => MdfBusMessageType.CanErrorFrame,
                    _ => MdfBusMessageType.CanDataFrame,
                },
                Timestamp = (f.TimestampNs - firstNs) / 1e9,
                BusChannel = 1,
                Id = f.Id,
                IsExtendedId = f.IsExtended,
                Direction = f.IsTransmitted ? MdfBusDirection.Tx : MdfBusDirection.Rx,
                Data = f.Data,
                Dlc = f.Dlc,
                IsFd = f.IsFd,
                BitRateSwitch = f.BitRateSwitch,
            });
        }
        logger.StopMeasurement();
        logger.FinalizeMeasurement();
    }

    // ---------------------------------------------------------------- pieces

    sealed class Series
    {
        public readonly List<double> X = new();
        public readonly List<double> Y = new();
    }

    /// <summary>One DBC signal in the tree: tick box, name, unit and the current value.</summary>
    sealed class SignalItem
    {
        public readonly DbcSignal Signal;
        public readonly CheckBox Box = new();
        public readonly StackPanel View = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        readonly TextBlock name;
        readonly TextBlock value = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VAlign.Center, Opacity = 0.9 };
        readonly Border swatch = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VAlign.Center };
        public DataLogger? Logger;
        public int Fed;
        public TreeViewItem? Container;

        public SignalItem(DbcSignal signal)
        {
            Signal = signal;
            name = new TextBlock { Text = signal.Name, VerticalAlignment = VAlign.Center, Opacity = 0.5 };
            View.Children.Add(Box);
            View.Children.Add(swatch);
            View.Children.Add(name);
            View.Children.Add(value);
        }

        /// <summary>The current value; a signal that has not been seen on the bus stays dimmed.</summary>
        public void SetValue(string text, bool seen)
        {
            value.Text = text;
            name.Opacity = seen ? 1 : 0.5;
        }

        public void SetColor(Avalonia.Media.Color? color) =>
            swatch.Background = color is { } c ? new SolidColorBrush(c) : null;

        // Caller holds the gate.
        public void Feed(Series s)
        {
            if (Logger == null) return;
            for (; Fed < s.X.Count; Fed++) Logger.Add(s.X[Fed], s.Y[Fed]);
        }
    }

    static Grid RowGrid(bool header, params string[] cells)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,150,40,220,90,80"), Margin = new Thickness(0, 1) };
        for (int i = 0; i < cells.Length; i++)
        {
            var t = new TextBlock
            {
                Text = cells[i],
                FontFamily = header || i == 1 ? FontFamily.Default : new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
                Opacity = header ? 0.6 : 1,
                FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                HorizontalAlignment = i >= 4 ? HAlign.Right : HAlign.Left,
                Margin = new Thickness(0, 0, i >= 4 ? 12 : 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        return g;
    }

    sealed class Row(uint id, bool extended, string message)
    {
        public uint Id => id;
        public bool Extended => extended;
        Grid? view;
        public Grid View => view ??= RowGrid(false, extended ? $"{id:X8} x" : $"{id:X3}", message, "", "", "", "");
        long count, shownCount;
        ulong lastNs;
        byte dlc;
        byte[] data = [];
        CanFrameKind kind;
        string cDlc = "", cData = "", cCount = "", cRate = "";
        bool changed;
        readonly Queue<ulong> recent = new();

        public void Update(in CanFrame f)
        {
            count++;
            lastNs = f.TimestampNs;
            dlc = f.Dlc;
            data = f.Data;
            kind = f.Kind;
            recent.Enqueue(f.TimestampNs);
            while (recent.Count > 1 && f.TimestampNs - recent.Peek() > 2_000_000_000UL) recent.Dequeue();
        }

        public void Snapshot()
        {
            cDlc = dlc.ToString(CultureInfo.InvariantCulture);
            cData = kind switch
            {
                CanFrameKind.Remote => "remote request",
                CanFrameKind.Error => "error frame",
                _ => string.Join(" ", data.Select(b => b.ToString("X2"))),
            };
            cCount = count.ToString("N0", CultureInfo.InvariantCulture);
            double span = recent.Count > 1 ? (lastNs - recent.Peek()) / 1e9 : 0;
            cRate = span > 0 ? $"{(recent.Count - 1) / span:0.0} Hz" : "";
            changed = count != shownCount;
            shownCount = count;
        }

        public void Render()
        {
            Set(2, cDlc); Set(3, cData); Set(4, cCount); Set(5, cRate);
            ((TextBlock)View.Children[0]).Opacity = changed ? 1 : 0.5;
        }

        void Set(int column, string text) => ((TextBlock)View.Children[column]).Text = text;
    }
}
