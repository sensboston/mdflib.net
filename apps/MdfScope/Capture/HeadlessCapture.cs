using MdfLib;
using MdfScope.Can;

namespace MdfScope;

/// <summary>MdfScope --capture "&lt;adapter&gt;" &lt;bit/s&gt; &lt;seconds&gt; &lt;out.mf4&gt; [--listen-only]: records without a window.</summary>
static class HeadlessCapture
{
    public static int Run(string adapter, int bitrate, double seconds, string path, bool listenOnly)
    {
        var providers = PluginHost.LoadProviders(out var problems);
        foreach (var p in problems) Console.Error.WriteLine("plugin: " + p);
        var match = providers.Where(p => p.IsAvailable)
            .SelectMany(p => p.GetDevices().Select(d => (p, d)))
            .FirstOrDefault(t => $"{t.p.Name} {t.d.DisplayName}".Contains(adapter, StringComparison.OrdinalIgnoreCase));
        if (match.p == null)
        {
            Console.Error.WriteLine($"No adapter matches \"{adapter}\". Available:");
            foreach (var p in providers)
                Console.Error.WriteLine(p.IsAvailable ? $"  {p.Name}: " + string.Join(", ", p.GetDevices().Select(d => d.DisplayName)) : $"  {p.Name}: {p.UnavailableReason}");
            return 2;
        }

        using var channel = match.p.Open(match.d, new CanChannelOptions(bitrate, listenOnly));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        using var logger = MdfBusLogger.Create(stream, MdfBusType.Can, leaveOpen: true, new MdfBusLoggerOptions
        {
            Version = "4.10",
            StartTimeNanoseconds = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L),
            NetworkName = match.d.DisplayName,
            CompressData = true,
        });
        logger.StartMeasurement();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        long count = 0;
        ulong first = 0;
        var ids = new HashSet<(uint, bool)>();
        try
        {
            var e = channel.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                var f = e.Current;
                if (count++ == 0) first = f.TimestampNs;
                ids.Add((f.Id, f.IsExtended));
                logger.Write(new MdfBusMessage
                {
                    Type = f.Kind == CanFrameKind.Remote ? MdfBusMessageType.CanRemoteFrame
                         : f.Kind == CanFrameKind.Error ? MdfBusMessageType.CanErrorFrame : MdfBusMessageType.CanDataFrame,
                    Timestamp = (f.TimestampNs - first) / 1e9, BusChannel = 1, Id = f.Id, IsExtendedId = f.IsExtended,
                    Data = f.Data, Dlc = f.Dlc, IsFd = f.IsFd, BitRateSwitch = f.BitRateSwitch,
                });
            }
        }
        catch (OperationCanceledException) { }
        Console.WriteLine($"{match.p.Name} · {match.d.DisplayName}: {count:N0} frames, {ids.Count} identifiers, bus {channel.BusState}");
        logger.StopMeasurement();
        logger.FinalizeMeasurement();
        Console.WriteLine($"written {path} ({new FileInfo(path).Length:N0} bytes)");
        return 0;
    }
}
