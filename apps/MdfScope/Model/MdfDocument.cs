using MdfLib;

namespace MdfScope;

/// <summary>An open measurement file: catalog, file model and a cache of loaded signals.</summary>
internal sealed class MdfDocument : IDisposable
{
    readonly FileStream stream;
    readonly MdfReader reader;
    readonly Dictionary<(int Group, int Channel), SignalData> cache = new();
    readonly object gate = new();

    public string Path { get; }
    public long FileSize { get; }
    public MdfFileHeader Header => reader.Header;
    public IReadOnlyList<MdfDataGroup> DataGroups { get; }
    public MdfFileModel? FileModel { get; }
    public string? FileModelError { get; }

    MdfDocument(string path)
    {
        Path = path;
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        FileSize = stream.Length;
        try
        {
            reader = MdfReader.Open(stream, leaveOpen: true);
            DataGroups = reader.ReadMetadata();
            try { FileModel = reader.ReadFileModel(); }
            catch (Exception e) when (e is MdfFormatException or MdfResourceLimitException or NotSupportedException)
            {
                FileModelError = e.Message;
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static MdfDocument Open(string path) => new(path);

    public IEnumerable<(MdfDataGroup DataGroup, MdfChannelGroup Group)> ChannelGroups =>
        DataGroups.SelectMany(dg => dg.ChannelGroups.Select(cg => (dg, cg)));

    public int ChannelCount => ChannelGroups.Sum(g => g.Group.Channels.Count(c => !c.IsMaster));

    public long SampleCount => ChannelGroups.Sum(g => (long)g.Group.SampleCount);

    /// <summary>Loads one channel with its group's master (time) channel; results are cached.</summary>
    public SignalData Load(MdfChannelGroup group, MdfChannel channel)
    {
        var key = (GroupKey(group), channel.Index);
        lock (gate)
        {
            if (cache.TryGetValue(key, out var cached))
                return cached;
        }

        var signal = Read(group, channel);
        lock (gate)
            cache[key] = signal;
        return signal;
    }

    int GroupKey(MdfChannelGroup group)
    {
        int key = 0;
        foreach (var (_, cg) in ChannelGroups)
        {
            if (ReferenceEquals(cg, group))
                return key;
            key++;
        }
        return -1;
    }

    SignalData Read(MdfChannelGroup group, MdfChannel channel)
    {
        MdfChannel? master = group.Channels.FirstOrDefault(c => c.IsMaster && c.Array == null);
        int count = checked((int)Math.Min(group.SampleCount, int.MaxValue));
        var x = new double[count];
        var y = new double[count];
        string[]? texts = null;

        bool textual = channel.Conversion?.Texts is { Count: > 0 } && channel.CanReadRaw;
        var physical = textual || !channel.CanReadPhysical ? MdfValueRepresentation.Raw : MdfValueRepresentation.Physical;
        if (textual)
            texts = new string[count];

        var indices = master != null && master.Index != channel.Index
            ? new[] { channel.Index, master.Index }
            : new[] { channel.Index };

        int row = 0;
        lock (gate)
        {
            foreach (var batch in reader.ReadSamples(group, indices, 0, (ulong)count, 65536, physical))
            {
                for (int i = 0; i < batch.SampleCount && row < count; i++, row++)
                {
                    y[row] = ToDouble(batch.GetValue(i, 0));
                    x[row] = indices.Length > 1 ? ToDouble(batch.GetValue(i, 1)) : row;
                }
            }

            if (texts != null)
            {
                row = 0;
                foreach (var batch in reader.ReadSamples(group, new[] { channel.Index }, 0, (ulong)count, 65536,
                             MdfValueRepresentation.Physical))
                {
                    for (int i = 0; i < batch.SampleCount && row < count; i++, row++)
                    {
                        var v = batch.GetValue(i, 0);
                        texts[row] = v.Kind == MdfValueKind.Text ? v.TextValue : string.Empty;
                    }
                }
            }
        }

        if (row < count)
        {
            Array.Resize(ref x, row);
            Array.Resize(ref y, row);
            if (texts != null) Array.Resize(ref texts, row);
        }

        // Physical time from a master that is not monotonic (rare, damaged files) still plots,
        // but a sample index axis keeps the signal readable.
        if (!IsSorted(x))
            for (int i = 0; i < x.Length; i++) x[i] = i;

        return new SignalData(channel, channel.Name, channel.Unit ?? string.Empty, x, y, texts,
            master != null ? master.Unit ?? "s" : "sample");
    }

    static bool IsSorted(double[] values)
    {
        for (int i = 1; i < values.Length; i++)
            if (!(values[i] >= values[i - 1])) return false;
        return true;
    }

    static double ToDouble(MdfSampleValue value)
    {
        if (!value.IsValid) return double.NaN;
        return value.Kind switch
        {
            MdfValueKind.FloatingPoint => value.FloatingPointValue,
            MdfValueKind.SignedInteger => value.SignedValue,
            MdfValueKind.UnsignedInteger => value.UnsignedValue,
            MdfValueKind.Complex => Math.Sqrt(value.RealPart * value.RealPart + value.ImaginaryPart * value.ImaginaryPart),
            _ => double.NaN,
        };
    }

    /// <summary>Signals decoded from the file's CAN bus log with a DBC, one group per message.</summary>
    public IReadOnlyList<DecodedGroup> Decoded => decoded;
    readonly List<DecodedGroup> decoded = new();

    /// <summary>Decodes every CAN data frame of the bus log groups with <paramref name="dbc"/>; returns the frame count.</summary>
    public long DecodeBusLog(DbcDatabase dbc)
    {
        var series = new Dictionary<DbcSignal, (List<double> X, List<double> Y)>();
        var owner = new Dictionary<DbcSignal, DbcMessage>();
        long frames = 0;
        lock (gate)
        {
            foreach (var m in MdfBusReader.ReadMessages(reader))
            {
                if (m.Type != MdfBusMessageType.CanDataFrame || m.Data == null) continue;
                var message = dbc.Find(m.Id, m.IsExtendedId);
                if (message == null) continue;
                frames++;
                double t = m.Timestamp;
                message.Decode(m.Data, (signal, value) =>
                {
                    if (!series.TryGetValue(signal, out var s)) { series[signal] = s = (new List<double>(), new List<double>()); owner[signal] = message; }
                    s.X.Add(t);
                    s.Y.Add(value);
                });
            }
        }

        decoded.RemoveAll(g => g.Dbc.Path == dbc.Path);
        foreach (var message in dbc.Messages.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            var signals = new List<SignalData>();
            foreach (var signal in message.Signals)
            {
                if (!series.TryGetValue(signal, out var s)) continue;
                double[] x = s.X.ToArray(), y = s.Y.ToArray();
                Array.Sort(x, y);
                string[]? texts = signal.ValueTable != null ? y.Select(v => signal.TextOf(v) ?? string.Empty).ToArray() : null;
                signals.Add(new SignalData(signal, signal.Name, signal.Unit, x, y, texts, "s"));
            }
            if (signals.Count > 0) decoded.Add(new DecodedGroup(dbc, message, signals));
        }
        return frames;
    }

    public void Dispose()
    {
        reader.Dispose();
        stream.Dispose();
    }
}

/// <summary>One loaded signal: time axis, values and, for value-to-text channels, the text of every sample.</summary>
/// <param name="Key">What the signal was loaded from: an <see cref="MdfChannel"/> or a decoded <see cref="DbcSignal"/>.</param>
internal sealed record SignalData(object Key, string Name, string Unit, double[] X, double[] Y, string[]? Texts, string XUnit)
{
    /// <summary>Index of the last sample at or before <paramref name="x"/>, or -1.</summary>
    public int IndexAt(double x)
    {
        int i = Array.BinarySearch(X, x);
        if (i < 0) i = ~i - 1;
        return Math.Min(i, X.Length - 1);
    }

    public string FormatAt(double x)
    {
        int i = IndexAt(x);
        if (i < 0) return "–";
        if (Texts != null && !string.IsNullOrEmpty(Texts[i])) return Texts[i];
        double v = Y[i];
        if (double.IsNaN(v)) return "invalid";
        string text = Math.Abs(v) >= 1e6 || (Math.Abs(v) < 1e-3 && v != 0) ? v.ToString("G6") : v.ToString("0.###");
        return string.IsNullOrEmpty(Unit) ? text : $"{text} {Unit}";
    }
}

/// <summary>The decoded signals of one DBC message.</summary>
internal sealed record DecodedGroup(DbcDatabase Dbc, DbcMessage Message, IReadOnlyList<SignalData> Signals);
