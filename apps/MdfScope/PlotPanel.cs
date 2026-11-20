using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;

namespace MdfScope;

/// <summary>One plot area: several signals over a shared time axis, a cursor line and event markers.</summary>
internal sealed class PlotPanel : Border
{
    static readonly string[] Palette =
        ["#4FC3F7", "#FFB74D", "#81C784", "#E57373", "#BA68C8", "#FFF176", "#4DB6AC", "#F06292", "#90A4AE", "#A1887F"];

    readonly AvaPlot plot = new();
    readonly List<SignalData> signals = new();
    readonly List<Avalonia.Media.Color> colors = new();
    readonly List<IPlottable> eventMarks = new();
    readonly List<bool> onRight = new();
    string? leftUnit, rightUnit;
    VerticalLine? cursor;
    bool dark;
    bool suppress;
    bool isActive;

    public event Action? Activated;
    public event Action<double, double>? XLimitsChanged;
    public event Action<double>? CursorMoved;

    public IReadOnlyList<SignalData> Signals => signals;
    public IReadOnlyList<Avalonia.Media.Color> Colors => colors;

    public PlotPanel(bool darkTheme)
    {
        Child = plot;
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        ClipToBounds = true;
        dark = darkTheme;

        plot.Plot.Axes.Bottom.Label.Text = "Time [s]";
        plot.Plot.Legend.IsVisible = true;
        plot.Plot.Legend.Alignment = Alignment.UpperLeft;
        plot.Plot.Axes.Top.MinimumSize = 20;
        plot.Plot.Axes.Right.IsVisible = false;
        ApplyTheme(dark);

        plot.Plot.RenderManager.AxisLimitsChanged += (_, _) =>
        {
            if (suppress) return;
            var limits = plot.Plot.Axes.GetLimits();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => XLimitsChanged?.Invoke(limits.Left, limits.Right));
        };
        plot.PointerPressed += (_, _) => Activated?.Invoke();
        plot.PointerMoved += (_, e) =>
        {
            if (signals.Count == 0 && cursor == null) return;
            var p = e.GetPosition(plot);
            float scale = plot.DisplayScale;
            var c = plot.Plot.GetCoordinates(new Pixel((float)p.X * scale, (float)p.Y * scale));
            CursorMoved?.Invoke(c.X);
        };
    }

    /// <summary>Only the lowest panel needs the time axis title.</summary>
    public bool ShowTimeTitle
    {
        set { plot.Plot.Axes.Bottom.Label.IsVisible = value; Refresh(); }
    }

    public bool IsActivePanel
    {
        get => isActive;
        set { isActive = value; UpdateBorder(); }
    }

    public (double Min, double Max) XLimits
    {
        get { var l = plot.Plot.Axes.GetLimits(); return (l.Left, l.Right); }
    }

    public void Add(SignalData signal)
    {
        if (signals.Any(s => ReferenceEquals(s, signal))) return;
        var color = ScottPlot.Color.FromHex(Palette[(signals.Count + PaletteOffset) % Palette.Length]);
        signals.Add(signal);
        colors.Add(Avalonia.Media.Color.FromRgb(color.R, color.G, color.B));

        var line = plot.Plot.Add.SignalXY(signal.X, signal.Y);
        line.Color = color;
        line.LineWidth = 1.5f;
        line.LegendText = string.IsNullOrEmpty(signal.Unit) ? signal.Name : $"{signal.Name} [{signal.Unit}]";

        // The first unit goes on the left axis, the second on the right; further units share the left one.
        bool right = false;
        if (leftUnit == null) leftUnit = signal.Unit;
        else if (signal.Unit != leftUnit && (rightUnit == null || rightUnit == signal.Unit))
        {
            rightUnit = signal.Unit;
            right = true;
            line.Axes.YAxis = plot.Plot.Axes.Right;
        }
        onRight.Add(right);
        plot.Plot.Axes.Left.Label.Text = AxisLabel(false);
        plot.Plot.Axes.Right.Label.Text = AxisLabel(true);
        plot.Plot.Axes.Right.IsVisible = onRight.Contains(true);
        plot.Plot.Axes.Bottom.Label.Text = signal.XUnit == "s" ? "Time [s]" : $"[{signal.XUnit}]";
        FitY();
    }

    string AxisLabel(bool right)
    {
        var units = signals.Where((_, i) => onRight[i] == right).Select(x => x.Unit).Distinct().ToList();
        return units.Count == 1 ? units[0] : string.Empty;
    }

    // A second panel starts further along the palette, so its first signal does not repeat the first panel's colour.
    public int PaletteOffset { get; set; }

    public bool Contains(object key) => signals.Any(s => ReferenceEquals(s.Key, key));

    public bool Remove(object key)
    {
        var keep = signals.Where(s => !ReferenceEquals(s.Key, key)).ToList();
        if (keep.Count == signals.Count) return false;
        var (min, max) = XLimits;
        Clear();
        foreach (var s in keep) Add(s);
        SetXLimits(min, max);
        FitY();
        return true;
    }

    public void Clear()
    {
        foreach (var p in plot.Plot.GetPlottables().ToList())
            if (p != cursor && !eventMarks.Contains(p)) plot.Plot.Remove(p);
        signals.Clear();
        colors.Clear();
        onRight.Clear();
        leftUnit = rightUnit = null;
        plot.Plot.Axes.Left.Label.Text = string.Empty;
        plot.Plot.Axes.Right.Label.Text = string.Empty;
        plot.Plot.Axes.Right.IsVisible = false;
        Refresh();
    }

    public void FitY()
    {
        if (signals.Count == 0) return;
        suppress = true;
        FitAxis(false, plot.Plot.Axes.Left);
        FitAxis(true, plot.Plot.Axes.Right);
        suppress = false;
        Refresh();
    }

    void FitAxis(bool right, IYAxis axis)
    {
        var (min, max) = XLimits;
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int k = 0; k < signals.Count; k++)
        {
            if (onRight[k] != right) continue;
            var s = signals[k];
            int a = Math.Max(0, s.IndexAt(min)), b = Math.Min(s.X.Length - 1, Math.Max(0, s.IndexAt(max) + 1));
            if (a > b) { a = 0; b = s.X.Length - 1; }
            for (int i = a; i <= b; i++)
            {
                double v = s.Y[i];
                if (double.IsNaN(v)) continue;
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
        }
        if (double.IsInfinity(lo)) return;
        double pad = hi > lo ? (hi - lo) * 0.08 : Math.Max(1, Math.Abs(hi) * 0.1);
        plot.Plot.Axes.SetLimitsY(lo - pad, hi + pad, axis);
    }

    public void SetXLimits(double min, double max)
    {
        if (!(max > min)) return;
        suppress = true;
        plot.Plot.Axes.SetLimitsX(min, max);
        suppress = false;
        Refresh();
    }

    public void SetCursor(double x)
    {
        if (double.IsNaN(x)) return;
        if (cursor == null)
        {
            cursor = plot.Plot.Add.VerticalLine(x);
            cursor.LineWidth = 1;
            cursor.LinePattern = LinePattern.Dashed;
            ColorCursor();
        }
        cursor.X = x;
        Refresh();
    }

    public void SetEvents(IReadOnlyList<(double Time, string Name)> events, bool showLabels)
    {
        foreach (var m in eventMarks) plot.Plot.Remove(m);
        eventMarks.Clear();
        foreach (var (time, name) in events)
        {
            var line = plot.Plot.Add.VerticalLine(time);
            line.Color = ScottPlot.Color.FromHex("#FF8A65").WithAlpha(0.75);
            line.LineWidth = 1;
            line.LinePattern = LinePattern.Dotted;
            if (showLabels)
            {
                line.LabelText = name;
                line.LabelOppositeAxis = true;
                line.LabelFontColor = ScottPlot.Color.FromHex("#FF8A65");
                line.LabelBackgroundColor = ScottPlot.Colors.Transparent;
                line.LabelFontSize = 11;
            }
            eventMarks.Add(line);
        }
        Refresh();
    }

    public void ApplyTheme(bool darkTheme)
    {
        dark = darkTheme;
        var p = plot.Plot;
        if (dark)
        {
            p.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1F22");
            p.DataBackground.Color = ScottPlot.Color.FromHex("#232529");
            p.Axes.Color(ScottPlot.Color.FromHex("#B9BEC6"));
            p.Grid.MajorLineColor = ScottPlot.Color.FromHex("#33363C");
            p.Legend.BackgroundColor = ScottPlot.Color.FromHex("#2B2D31").WithAlpha(0.9);
            p.Legend.FontColor = ScottPlot.Color.FromHex("#D5D8DD");
            p.Legend.OutlineColor = ScottPlot.Color.FromHex("#3A3D43");
        }
        else
        {
            p.FigureBackground.Color = ScottPlot.Colors.White;
            p.DataBackground.Color = ScottPlot.Color.FromHex("#FBFBFC");
            p.Axes.Color(ScottPlot.Color.FromHex("#3C4048"));
            p.Grid.MajorLineColor = ScottPlot.Color.FromHex("#E6E8EB");
            p.Legend.BackgroundColor = ScottPlot.Colors.White.WithAlpha(0.9);
            p.Legend.FontColor = ScottPlot.Color.FromHex("#202226");
            p.Legend.OutlineColor = ScottPlot.Color.FromHex("#D0D3D8");
        }
        ColorCursor();
        UpdateBorder();
        Refresh();
    }

    void ColorCursor()
    {
        if (cursor != null)
            cursor.Color = dark ? ScottPlot.Color.FromHex("#E0E0E0").WithAlpha(0.7) : ScottPlot.Color.FromHex("#303030").WithAlpha(0.7);
    }

    void UpdateBorder()
    {
        BorderBrush = new SolidColorBrush(isActive
            ? Avalonia.Media.Color.Parse("#4FC3F7")
            : Avalonia.Media.Color.Parse(dark ? "#2E3036" : "#DADDE2"));
    }

    void Refresh() => plot.Refresh();
}
