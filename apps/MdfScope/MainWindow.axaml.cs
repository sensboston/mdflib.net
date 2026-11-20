using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using MdfLib;

namespace MdfScope;

public partial class MainWindow : Window
{
    MdfDocument? document;
    bool unsavedCapture;
    readonly TextBlock capturingOverlay = new()
    {
        Text = "Capturing: the live signals are on the Capture tab.\nThe capture opens here when it stops.",
        TextAlignment = Avalonia.Media.TextAlignment.Center, Opacity = 0.6, FontSize = 15, IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    readonly CaptureView capture;
    readonly List<PlotPanel> panels = new();
    PlotPanel? active;
    double cursorX = double.NaN;
    bool syncing;

    public MainWindow()
    {
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        capture = new CaptureView { Margin = new Thickness(4, 8) };
        // One document at a time: a capture replaces the open file, and after Stop the capture is the document.
        capture.BeforeStart = async () =>
        {
            if (unsavedCapture && !await Confirm("Discard the unsaved capture and start a new one?")) return false;
            if (document != null && !unsavedCapture && !await Confirm($"Close {Path.GetFileName(document.Path)} and start a capture?")) return false;
            CloseDocument();
            capturingOverlay.IsVisible = true;
            return true;
        };
        capture.Stopped += async (path, dbcPath) =>
        {
            capturingOverlay.IsVisible = false;
            await OpenAsync(path, force: true);
            if (dbcPath != null) await DecodeWith(dbcPath);
            unsavedCapture = true;
            Title = "Capture (unsaved) – MdfScope";
            Tabs.SelectedIndex = 0;
        };
        capture.Saved += async (path, dbcPath) =>
        {
            await OpenAsync(path, force: true);
            if (dbcPath != null) await DecodeWith(dbcPath);
            Tabs.SelectedIndex = 0;
        };
        if (PlotHost.Parent is Grid signalsGrid)
        {
            Grid.SetColumn(capturingOverlay, 0);
            signalsGrid.Children.Add(capturingOverlay);
        }
        CaptureTab.Content = capture;
        // The left panel shows the file's channels on Signals/File and the DBC signals on Capture.
        // Both trees share the remaining space of the panel: a DockPanel fills only its last child, so they
        // sit together in one grid (otherwise the file tree docks left at its content width).
        capture.SignalPanel.IsVisible = false;
        LeftPanel.Children.Remove(ChannelTree);
        var trees = new Grid();
        trees.Children.Add(ChannelTree);
        trees.Children.Add(capture.SignalPanel);
        LeftPanel.Children.Add(trees);
        Tabs.SelectionChanged += (_, _) =>
        {
            bool capturing = Tabs.SelectedIndex == 2;
            ChannelTree.IsVisible = !capturing;
            capture.SignalPanel.IsVisible = capturing;
            LeftHint.Text = capturing
                ? "Tick a signal to plot it live; values update while capturing."
                : "Tick a channel to plot it in the active panel, untick to remove it; right-click for a new panel.";
            capture.Filter(FilterBox.Text?.Trim() ?? string.Empty);
        };
        Closing += async (_, _) => await capture.StopAsync(closing: true);
        Opened += (_, _) => FitToScreen();
        FilterBox.TextChanged += (_, _) =>
        {
            if (Tabs.SelectedIndex == 2) capture.Filter(FilterBox.Text?.Trim() ?? string.Empty);
            else BuildTree();
        };
        AddHandler(DragDrop.DropEvent, OnDrop);
        capture.TreeChanged += FitLeftPanel;
        LeftSplitter.DragCompleted += (_, _) =>
        {
            Settings.Current.LeftPanelWidth = MainGrid.ColumnDefinitions[0].ActualWidth;
            Settings.Current.Save();
        };
        LeftSplitter.DoubleTapped += (_, _) =>
        {
            Settings.Current.LeftPanelWidth = null;
            Settings.Current.Save();
            FitLeftPanel();
        };
        FitLeftPanel();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control)) OnOpen(this, new RoutedEventArgs());
            else if (e.Key == Key.F && FocusManager?.GetFocusedElement() is not TextBox) FitAll();
        };
        AddPanel();
        ApplyTheme(dark: true);
    }

    bool scripted;

    void FitToScreen()
    {
        if (scripted || Screens.ScreenFromWindow(this) is not { } screen) return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        double w = Math.Min(Width, area.Width / scale * 0.92), h = Math.Min(Height, area.Height / scale * 0.9);
        Width = w;
        Height = h;
        Position = new PixelPoint(area.X + (int)((area.Width - w * scale) / 2), area.Y + (int)((area.Height - h * scale) / 2));
    }

    /// <summary>Scripted capture for screenshots: demo bus + DBC for some seconds, then a PNG of the window.</summary>
    public void RunCaptureDemo(string dbcPath, string signals, double seconds, string screenshot)
    {
        scripted = true;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                Tabs.SelectedIndex = 2;
                capture.StartDemo(dbcPath, signals.Split(',', StringSplitOptions.RemoveEmptyEntries));
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
                bitmap.Render(this);
                bitmap.Save(screenshot);
                await capture.StopAsync();
            }
            finally { Close(); }
        });
    }

    public void ReportError(Exception e) =>
        StatusText.Text = $"Unexpected error: {e.GetType().Name}: {e.Message} (details in {App.ErrorLog})";

    public void OpenOnStart(string path) => Dispatcher.UIThread.Post(() => _ = OpenAsync(path));

    /// <summary>
    /// Scripted run for screenshots: open a file, plot panels given as "a,b;c;d,e" (channel names, one
    /// panel per ';'), optionally move the cursor and zoom, save the window as PNG and close.
    /// </summary>
    public void RunScript(string path, string plotSpec, string? cursor, string? range, string screenshot, bool showFileTab, string? dbc = null)
    {
        scripted = true;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await OpenAsync(path);
                if (document == null) { Close(); return; }
                if (dbc != null) await DecodeWith(dbc);
                var specs = plotSpec.Split(';', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < specs.Length; i++)
                {
                    var panel = i == 0 ? panels[0] : AddPanel();
                    panel.PaletteOffset = i * 3;
                    foreach (var name in specs[i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var match = document.ChannelGroups.SelectMany(g => g.Group.Channels.Select(c => (g.Group, c)))
                            .FirstOrDefault(t => t.c.Name == name.Trim());
                        var decodedMatch = document.Decoded.SelectMany(g => g.Signals).FirstOrDefault(x => x.Name == name.Trim());
                        if (decodedMatch != null) PlotDecoded(decodedMatch, panel);
                        else if (match.c != null) await PlotAsync(match.Group, match.c, panel);
                    }
                }
                FitAll();
                if (range?.Split(':') is [var a, var b] && double.TryParse(a, CultureInfo.InvariantCulture, out var lo)
                    && double.TryParse(b, CultureInfo.InvariantCulture, out var hi))
                {
                    foreach (var p in panels) { p.SetXLimits(lo, hi); p.FitY(); }
                }
                if (double.TryParse(cursor, CultureInfo.InvariantCulture, out var cx))
                {
                    cursorX = cx;
                    foreach (var p in panels) p.SetCursor(cx);
                    UpdateReadout();
                }
                if (showFileTab && this.FindControl<TabControl>("Tabs") is { } tabs) tabs.SelectedIndex = 1;
                await Task.Delay(1200);
                var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
                bitmap.Render(this);
                bitmap.Save(screenshot);
            }
            finally
            {
                Close();
            }
        });
    }

    // ---------------------------------------------------------------- files

    async void OnOpen(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open measurement",
            AllowMultiple = false,
            SuggestedStartLocation = await Settings.StartIn(StorageProvider, Settings.Current.MeasurementFolder),
            FileTypeFilter =
            [
                new FilePickerFileType("MDF files") { Patterns = ["*.mf4", "*.mdf", "*.dat", "*.MF4", "*.MDF"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            Settings.Remember((s, d) => s.MeasurementFolder = d, path);
            await OpenAsync(path);
        }
    }

    async void OnCreateDemo(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Create demo measurement",
            SuggestedFileName = "demo-drive.mf4",
            SuggestedStartLocation = await Settings.StartIn(StorageProvider, Settings.Current.MeasurementFolder),
            DefaultExtension = "mf4",
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        Settings.Remember((s, d) => s.MeasurementFolder = d, path);
        if (!await MayReplaceDocument()) return;
        StatusText.Text = "Writing the demo measurement…";
        try
        {
            await Task.Run(() => DemoFile.Write(path));
            await OpenAsync(path, force: true);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Demo file failed: " + ex.Message;
        }
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        var item = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        if (item?.TryGetLocalPath() is { } path)
            _ = OpenAsync(path);
    }

    /// <summary>Closes the open file or capture: empty tree, plots and file information.</summary>
    void CloseDocument()
    {
        document?.Dispose();
        document = null;
        unsavedCapture = false;
        foreach (var p in panels) p.Clear();
        BuildTree();
        BuildInfo();
        UpdateEvents();
        UpdateReadout();
        ExportItem.IsEnabled = false;
        Title = "MdfScope";
    }

    /// <summary>A yes/no question in a small modal window.</summary>
    async Task<bool> Confirm(string question)
    {
        var dialog = new Window
        {
            Title = "MdfScope", Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var yes = new Button { Content = "Yes", MinWidth = 80, IsDefault = true };
        var no = new Button { Content = "No", MinWidth = 80, IsCancel = true };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        var body = new StackPanel { Margin = new Thickness(18), Spacing = 16 };
        body.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(buttons);
        dialog.Content = body;
        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>Opening a file replaces the document; an unsaved capture is not dropped without asking.</summary>
    async Task<bool> MayReplaceDocument()
    {
        if (capture.IsCapturing) { StatusText.Text = "Stop the capture before opening a file."; return false; }
        return !unsavedCapture || await Confirm("Discard the unsaved capture?");
    }

    async Task OpenAsync(string path, bool force = false)
    {
        if (!force && !await MayReplaceDocument()) return;
        StatusText.Text = "Opening " + Path.GetFileName(path) + "…";
        var watch = Stopwatch.StartNew();
        MdfDocument opened;
        try
        {
            opened = await Task.Run(() => MdfDocument.Open(path));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Cannot open {Path.GetFileName(path)}: {ex.Message}";
            return;
        }

        document?.Dispose();
        document = opened;
        unsavedCapture = false;
        capturingOverlay.IsVisible = false;
        foreach (var p in panels) p.Clear();
        Title = $"{Path.GetFileName(path)} – MdfScope";
        BuildTree();
        BuildInfo();
        ExportItem.IsEnabled = true;
        StatusText.Text =
            $"{Path.GetFileName(path)}  ·  MDF {document.Header.Version}  ·  {FormatSize(document.FileSize)}  ·  " +
            $"{document.ChannelGroups.Count()} channel groups, {document.ChannelCount} channels, " +
            $"{document.SampleCount:N0} records  ·  catalog read in {watch.ElapsedMilliseconds} ms";
        UpdateEvents();
    }

    void OnExit(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        document?.Dispose();
        base.OnClosed(e);
    }

    // ---------------------------------------------------------------- channel tree

    void BuildTree()
    {
        var items = new List<TreeViewItem>();
        checks.Clear();
        if (document != null)
        {
            string filter = FilterBox.Text?.Trim() ?? string.Empty;
            foreach (var (dg, cg) in document.ChannelGroups)
            {
                var channels = cg.Channels
                    .Where(c => !c.IsMaster && (filter.Length == 0 || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (channels.Count == 0) continue;

                string groupName = string.IsNullOrEmpty(cg.Name) ? $"Group {dg.Index}.{cg.Index}" : cg.Name;
                bool busLog = cg.Channels.Any(c => c.Name.StartsWith("CAN_", StringComparison.Ordinal) && c.Name.IndexOf('.') < 0);
                var groupItem = new TreeViewItem
                {
                    Header = GroupHeader(groupName, cg),
                    IsExpanded = !(busLog && document.Decoded.Count > 0) || filter.Length > 0,
                };
                ToolTip.SetTip(groupItem, Describe(cg));
                var children = new List<TreeViewItem>();
                foreach (var ch in channels)
                {
                    var box = new CheckBox { Content = ChannelHeader(ch), IsEnabled = ch.CanReadRaw, IsChecked = panels.Any(p => p.Contains(ch)) };
                    box.IsCheckedChanged += (_, _) =>
                    {
                        if (updatingChecks) return;
                        if (box.IsChecked == true) Plot(cg, ch, active);
                        else Unplot(ch);
                    };
                    checks[ch] = box;
                    var item = new TreeViewItem { Header = box, Tag = (cg, ch) };
                    item.ContextMenu = ChannelMenu(cg, ch);
                    string tip = string.Join("\n", new[] { ch.Name, ch.Comment, ch.Source?.Name }.Where(s => !string.IsNullOrEmpty(s)));
                    ToolTip.SetTip(item, tip);
                    children.Add(item);
                }
                groupItem.ItemsSource = children;
                items.Add(groupItem);
            }

            int decodedAt = 0;
            foreach (var group in document.Decoded)
            {
                var signals = group.Signals.Where(s => filter.Length == 0 || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
                if (signals.Count == 0) continue;
                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                header.Children.Add(new TextBlock { Text = group.Message.Name, FontWeight = FontWeight.SemiBold });
                header.Children.Add(new TextBlock
                {
                    Text = $"{(group.Message.IsExtended ? group.Message.Id.ToString("X8") : group.Message.Id.ToString("X3"))} · {group.Dbc.Name}",
                    Opacity = 0.55, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                });
                var groupItem = new TreeViewItem { Header = header, IsExpanded = true };
                if (group.Message.Comment.Length > 0) ToolTip.SetTip(groupItem, group.Message.Comment);
                var children = new List<TreeViewItem>();
                foreach (var signal in signals)
                {
                    var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    label.Children.Add(new TextBlock { Text = signal.Name });
                    if (signal.Unit.Length > 0)
                        label.Children.Add(new TextBlock { Text = signal.Unit, Opacity = 0.55, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                    var box = new CheckBox { Content = label, IsChecked = panels.Any(p => p.Contains(signal.Key)) };
                    var s = signal;
                    box.IsCheckedChanged += (_, _) =>
                    {
                        if (updatingChecks) return;
                        if (box.IsChecked == true) PlotDecoded(s, active);
                        else Unplot(s.Key);
                    };
                    checks[signal.Key] = box;
                    var item = new TreeViewItem { Header = box };
                    if (signal.Key is DbcSignal { Comment.Length: > 0 } ds) ToolTip.SetTip(item, ds.Comment);
                    children.Add(item);
                }
                groupItem.ItemsSource = children;
                items.Insert(decodedAt++, groupItem);
            }
        }
        ChannelTree.ItemsSource = items;
        fileTreeWidth = PreferredWidth(TreeTexts());
        FitLeftPanel();
    }

    readonly Dictionary<object, CheckBox> checks = new();
    double fileTreeWidth;

    /// <summary>The texts of the file tree's rows: group headers and channel labels.</summary>
    IEnumerable<(string Text, bool Group)> TreeTexts()
    {
        if (document == null) yield break;
        foreach (var (dg, cg) in document.ChannelGroups)
        {
            yield return ($"{(string.IsNullOrEmpty(cg.Name) ? $"Group {dg.Index}.{cg.Index}" : cg.Name)}  {cg.SampleCount:N0}", true);
            foreach (var ch in cg.Channels.Where(c => !c.IsMaster))
                yield return ($"{ch.Name}  {ch.Unit}", false);
        }
        foreach (var g in document.Decoded)
        {
            yield return ($"{g.Message.Name}  {g.Message.Id:X8} · {g.Dbc.Name}", true);
            foreach (var s in g.Signals) yield return ($"{s.Name}  {s.Unit}", false);
        }
    }

    /// <summary>The panel width that shows the longest row in full, kept between 200 and 360.</summary>
    internal static double PreferredWidth(IEnumerable<(string Text, bool Group)> rows)
    {
        double widest = 0;
        var probe = new TextBlock();
        foreach (var (text, group) in rows)
        {
            probe.Text = text;
            probe.FontWeight = group ? FontWeight.SemiBold : FontWeight.Normal;
            probe.Measure(Size.Infinity);
            // tree indentation, expander and tick box, and the scroll bar
            widest = Math.Max(widest, probe.DesiredSize.Width + (group ? 40 : 72) + 16);
        }
        return Math.Clamp(widest, 200, 360);
    }

    /// <summary>Sizes the channel panel to the longer of the two trees, unless the user has dragged it.</summary>
    void FitLeftPanel()
    {
        if (Settings.Current.LeftPanelWidth is double chosen)
        {
            MainGrid.ColumnDefinitions[0].Width = new GridLength(chosen);
            return;
        }
        double width = Math.Max(fileTreeWidth, capture.PreferredTreeWidth);
        MainGrid.ColumnDefinitions[0].Width = new GridLength(width > 0 ? width : 300);
    }
    bool updatingChecks;

    /// <summary>Ticks exactly the channels that are on some panel.</summary>
    void SyncChecks()
    {
        updatingChecks = true;
        foreach (var (ch, box) in checks) box.IsChecked = panels.Any(p => p.Contains(ch));
        updatingChecks = false;
    }

    void Unplot(object key)
    {
        foreach (var p in panels) p.Remove(key);
        UpdateReadout();
        SyncChecks();
    }

    static Control GroupHeader(string name, MdfChannelGroup cg)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"{cg.SampleCount:N0}", Opacity = 0.55, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    static Control ChannelHeader(MdfChannel ch)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = ch.Name });
        if (!string.IsNullOrEmpty(ch.Unit))
            panel.Children.Add(new TextBlock { Text = ch.Unit, Opacity = 0.55, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        if (!ch.CanReadRaw)
            panel.Children.Add(new TextBlock { Text = "unsupported", Foreground = Brushes.IndianRed, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    static string Describe(MdfChannelGroup cg)
    {
        var lines = new List<string> { $"{cg.SampleCount:N0} records of {cg.RecordByteCount} bytes" };
        if (!string.IsNullOrEmpty(cg.Comment)) lines.Add(cg.Comment);
        if (cg.Source != null && !string.IsNullOrEmpty(cg.Source.Name)) lines.Add("Source: " + cg.Source.Name);
        return string.Join("\n", lines);
    }

    ContextMenu ChannelMenu(MdfChannelGroup cg, MdfChannel ch)
    {
        var plotHere = new MenuItem { Header = "Plot in active panel" };
        plotHere.Click += (_, _) => Plot(cg, ch, active);
        var plotNew = new MenuItem { Header = "Plot in new panel" };
        plotNew.Click += (_, _) => Plot(cg, ch, AddPanel());
        return new ContextMenu { ItemsSource = new[] { plotHere, plotNew } };
    }

    // ---------------------------------------------------------------- plotting

    async void Plot(MdfChannelGroup cg, MdfChannel ch, PlotPanel? panel) => await PlotAsync(cg, ch, panel);

    async Task PlotAsync(MdfChannelGroup cg, MdfChannel ch, PlotPanel? panel)
    {
        if (document == null || !ch.CanReadRaw) return;
        panel ??= panels.LastOrDefault() ?? AddPanel();
        var doc = document;
        StatusText.Text = $"Loading {ch.Name}…";
        var watch = Stopwatch.StartNew();
        SignalData signal;
        try
        {
            signal = await Task.Run(() => doc.Load(cg, ch));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Cannot read {ch.Name}: {ex.Message}";
            SyncChecks();
            return;
        }
        if (doc != document) return;

        bool first = panels.All(p => p.Signals.Count == 0);
        panel.Add(signal);
        if (first) FitAll();
        else panel.FitY();
        SetActive(panel);
        UpdateEvents();
        UpdateReadout();
        SyncChecks();
        StatusText.Text = $"{ch.Name}: {signal.X.Length:N0} samples loaded in {watch.ElapsedMilliseconds} ms";
    }

    void PlotDecoded(SignalData signal, PlotPanel? panel)
    {
        panel ??= panels.LastOrDefault() ?? AddPanel();
        bool first = panels.All(p => p.Signals.Count == 0);
        panel.Add(signal);
        if (first) FitAll(); else panel.FitY();
        SetActive(panel);
        UpdateEvents();
        UpdateReadout();
        SyncChecks();
    }

    async void OnDecodeBus(object? sender, RoutedEventArgs e)
    {
        if (document == null) { StatusText.Text = "Open a measurement with a CAN bus log first."; return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Decode the CAN bus log with a DBC",
            SuggestedStartLocation = await Settings.StartIn(StorageProvider, Settings.Current.DbcFolder),
            FileTypeFilter = [new FilePickerFileType("DBC files") { Patterns = ["*.dbc", "*.DBC"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        Settings.Remember((s, d) => s.DbcFolder = d, path);
        await DecodeWith(path);
    }

    async Task DecodeWith(string dbcPath)
    {
        if (document == null) return;
        var doc = document;
        StatusText.Text = "Decoding with " + Path.GetFileName(dbcPath) + "…";
        var watch = Stopwatch.StartNew();
        try
        {
            var dbc = await Task.Run(() => DbcDatabase.Load(dbcPath));
            long frames = await Task.Run(() => doc.DecodeBusLog(dbc));
            if (doc != document) return;
            BuildTree();
            int signals = doc.Decoded.Where(g => g.Dbc.Path == dbc.Path).Sum(g => g.Signals.Count);
            StatusText.Text = frames == 0
                ? $"No CAN frame in this file matches {dbc.Name}."
                : $"Decoded {frames:N0} frames into {signals} signals with {dbc.Name} in {watch.ElapsedMilliseconds} ms";
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or ArgumentException or MdfFormatException)
        {
            StatusText.Text = "Decoding failed: " + ex.Message;
        }
    }

    PlotPanel AddPanel()
    {
        var panel = new PlotPanel(DarkItem.IsChecked);
        panel.Activated += () => SetActive(panel);
        panel.XLimitsChanged += (min, max) => SyncX(panel, min, max);
        panel.CursorMoved += x => { cursorX = x; foreach (var p in panels) p.SetCursor(x); UpdateReadout(); };
        panels.Add(panel);
        LayoutPanels();
        if (panels.Count > 1)
        {
            var (min, max) = panels[0].XLimits;
            panel.SetXLimits(min, max);
        }
        panel.SetCursor(cursorX);
        SetActive(panel);
        UpdateEvents();
        return panel;
    }

    void LayoutPanels()
    {
        PlotHost.Children.Clear();
        PlotHost.RowDefinitions.Clear();
        for (int i = 0; i < panels.Count; i++)
        {
            PlotHost.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            Grid.SetRow(panels[i], i);
            panels[i].Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0);
            panels[i].ShowTimeTitle = i == panels.Count - 1;
            PlotHost.Children.Add(panels[i]);
        }
    }

    void SetActive(PlotPanel panel)
    {
        active = panel;
        foreach (var p in panels) p.IsActivePanel = p == panel && panels.Count > 1;
    }

    void SyncX(PlotPanel source, double min, double max)
    {
        if (syncing) return;
        syncing = true;
        foreach (var p in panels)
            if (p != source) p.SetXLimits(min, max);
        syncing = false;
    }

    void FitAll()
    {
        var all = panels.SelectMany(p => p.Signals).Where(s => s.X.Length > 0).ToList();
        if (all.Count == 0) return;
        double min = all.Min(s => s.X[0]), max = all.Max(s => s.X[^1]);
        foreach (var p in panels)
        {
            p.FitY();
            p.SetXLimits(min, max);
        }
    }

    void UpdateReadout()
    {
        ReadoutPanel.Children.Clear();
        CursorText.Text = double.IsNaN(cursorX) ? "Cursor" : $"Cursor  {cursorX.ToString("0.000", CultureInfo.InvariantCulture)} s";
        foreach (var panel in panels)
        {
            foreach (var (signal, color) in panel.Signals.Zip(panel.Colors))
            {
                var row = new DockPanel();
                var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(color), Margin = new Thickness(0, 4, 8, 0), VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = signal.Name, FontSize = 12, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock
                {
                    Text = double.IsNaN(cursorX) ? "–" : signal.FormatAt(cursorX),
                    FontSize = 15, FontWeight = FontWeight.SemiBold,
                });
                row.Children.Add(text);
                ReadoutPanel.Children.Add(row);
            }
        }
    }

    void UpdateEvents()
    {
        var events = EventsItem.IsChecked && document?.FileModel?.Events is { } list
            ? list.Where(e => e.SyncType == MdfEventSyncType.Time).Select(e => (e.SyncValue, e.Name ?? string.Empty)).ToList()
            : new List<(double, string)>();
        for (int i = 0; i < panels.Count; i++)
            panels[i].SetEvents(events, showLabels: i == 0);
    }

    void OnAddPanel(object? sender, RoutedEventArgs e) => AddPanel();

    void OnRemovePanel(object? sender, RoutedEventArgs e)
    {
        if (active == null || panels.Count <= 1) { active?.Clear(); UpdateReadout(); return; }
        panels.Remove(active);
        LayoutPanels();
        SetActive(panels[^1]);
        UpdateEvents();
        UpdateReadout();
        SyncChecks();
    }

    void OnClearPlots(object? sender, RoutedEventArgs e)
    {
        while (panels.Count > 1) panels.RemoveAt(panels.Count - 1);
        panels[0].Clear();
        LayoutPanels();
        SetActive(panels[0]);
        UpdateEvents();
        UpdateReadout();
        SyncChecks();
    }

    void OnFit(object? sender, RoutedEventArgs e) => FitAll();

    void OnToggleEvents(object? sender, RoutedEventArgs e) => UpdateEvents();

    void OnToggleTheme(object? sender, RoutedEventArgs e) => ApplyTheme(DarkItem.IsChecked);

    void ApplyTheme(bool dark)
    {
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        foreach (var p in panels) p.ApplyTheme(dark);
        capture?.ApplyTheme(dark);
        UpdateReadout();
    }

    // ---------------------------------------------------------------- export

    async void OnExport(object? sender, RoutedEventArgs e)
    {
        var signals = panels.SelectMany(p => p.Signals).ToList();
        if (signals.Count == 0) { StatusText.Text = "Plot some signals first; the export writes the plotted ones."; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export plotted signals",
            SuggestedStartLocation = await Settings.StartIn(StorageProvider, Settings.Current.CsvFolder),
            SuggestedFileName = Path.GetFileNameWithoutExtension(document?.Path ?? "signals") + ".csv",
            DefaultExtension = "csv",
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        Settings.Remember((s, d) => s.CsvFolder = d, path);
        var (min, max) = panels[0].XLimits;
        await Task.Run(() => CsvExport.Write(path, signals, min, max));
        StatusText.Text = $"Exported {signals.Count} signals between {min:0.###} and {max:0.###} s to {Path.GetFileName(path)}";
    }

    // ---------------------------------------------------------------- file information

    void BuildInfo()
    {
        InfoPanel.Children.Clear();
        if (document == null) return;
        var h = document.Header;
        var m = document.FileModel;

        Section("Measurement",
        [
            ("File", document.Path),
            ("Size", FormatSize(document.FileSize)),
            ("Format", "MDF " + h.Version),
            ("Program", h.ProgramIdentifier),
            ("Start", StartTime(h)),
            ("Description", m?.Description),
            ("Author", m?.Author ?? h.Author),
            ("Organization", m?.Organization ?? h.Organization),
            ("Project", m?.Project ?? h.Project),
            ("Subject", m?.Subject ?? h.Subject),
        ]);

        if (m?.FileHistory is { Count: > 0 } history)
            Section("History", history.Select(fh => ((string?)$"#{fh.Index}",
                (string?)string.Join("  ·  ", new[] { fh.ToolIdentifier, fh.ToolVersion, fh.ToolVendor, fh.UserName, fh.Text }
                    .Where(s => !string.IsNullOrEmpty(s))))).ToList());

        if (m?.Attachments is { Count: > 0 } attachments)
            Section("Attachments", attachments.Select(a => ((string?)a.FileName,
                (string?)$"{a.MimeType}  ·  {FormatSize((long)a.OriginalSize)}{(a.IsEmbedded ? " embedded" : " external")}{(a.IsCompressed ? ", compressed" : "")}  {a.Comment}")).ToList());

        if (m?.Events is { Count: > 0 } events)
            Section("Events", events.Select(ev => ((string?)ev.Name,
                (string?)$"{ev.Type}  ·  {(ev.SyncType == MdfEventSyncType.Time ? ev.SyncValue.ToString("0.###", CultureInfo.InvariantCulture) + " s" : ev.SyncValue.ToString(CultureInfo.InvariantCulture))}  {ev.Comment}")).ToList());

        Section("Channel groups", document.ChannelGroups.Select(g => ((string?)(string.IsNullOrEmpty(g.Group.Name) ? $"Group {g.DataGroup.Index}.{g.Group.Index}" : g.Group.Name),
            (string?)$"{g.Group.Channels.Count} channels  ·  {g.Group.SampleCount:N0} records  ·  {g.Group.RecordByteCount} bytes each{(g.Group.Source?.Name is { Length: > 0 } s ? "  ·  source " + s : "")}")).ToList());

        if (document.FileModelError != null)
            Section("Note", [("File model", document.FileModelError)]);
    }

    void Section(string title, IReadOnlyList<(string? Key, string? Value)> rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), Margin = new Thickness(0, 4, 0, 0) };
        int r = 0;
        foreach (var (key, value) in rows)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var k = new TextBlock { Text = key, Opacity = 0.6, Margin = new Thickness(0, 2) };
            var v = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2) };
            Grid.SetRow(k, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
            grid.Children.Add(k); grid.Children.Add(v);
            r++;
        }
        if (r == 0) return;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(grid);
        InfoPanel.Children.Add(panel);
    }

    static string StartTime(MdfFileHeader h)
    {
        if (h.RawStartTimeNanoseconds == 0) return string.Join(" ", new[] { h.DateText, h.TimeText }.Where(s => !string.IsNullOrEmpty(s)));
        var utc = DateTimeOffset.FromUnixTimeMilliseconds((long)(h.RawStartTimeNanoseconds / 1_000_000));
        return utc.ToOffset(TimeSpan.FromMinutes(h.UtcOffsetMinutes)).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    }

    static string FormatSize(long bytes) =>
        bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):0.0} GB" :
        bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" :
        bytes >= 1 << 10 ? $"{bytes / 1024.0:0.0} KB" : $"{bytes} B";
}

static class CsvExport
{
    /// <summary>Writes the plotted signals between two times, one row per distinct time stamp (sample-and-hold).</summary>
    public static void Write(string path, IReadOnlyList<SignalData> signals, double min, double max)
    {
        var times = new SortedSet<double>();
        foreach (var s in signals)
            for (int i = Math.Max(0, s.IndexAt(min)); i < s.X.Length && s.X[i] <= max; i++)
                times.Add(s.X[i]);

        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.Write("time");
        foreach (var s in signals) w.Write("," + Quote(string.IsNullOrEmpty(s.Unit) ? s.Name : $"{s.Name} [{s.Unit}]"));
        w.WriteLine();
        var cursor = new int[signals.Count];
        foreach (double t in times)
        {
            w.Write(t.ToString("R", CultureInfo.InvariantCulture));
            for (int k = 0; k < signals.Count; k++)
            {
                var s = signals[k];
                while (cursor[k] + 1 < s.X.Length && s.X[cursor[k] + 1] <= t) cursor[k]++;
                w.Write(',');
                if (s.X.Length == 0 || s.X[cursor[k]] > t) continue;
                if (s.Texts != null) w.Write(Quote(s.Texts[cursor[k]]));
                else w.Write(s.Y[cursor[k]].ToString("R", CultureInfo.InvariantCulture));
            }
            w.WriteLine();
        }
    }

    static string Quote(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
