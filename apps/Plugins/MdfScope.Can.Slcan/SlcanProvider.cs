using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace MdfScope.Can.Slcan;

/// <summary>
/// Serial-line CAN (the Lawicel ASCII protocol): CANable and candleLight with slcan firmware, Lawicel CANUSB and
/// their many clones, on any operating system. Every serial port is offered; the adapter is recognised when it
/// answers the open sequence. Extra port names (for example pseudo-terminals) can be listed in the
/// <c>MDFSCOPE_SLCAN_PORTS</c> environment variable, separated by ';'.
/// </summary>
public sealed class SlcanProvider : ICanAdapterProvider
{
    public string Name => "SLCAN";
    public bool IsAvailable => true;
    public string? UnavailableReason => null;

    public IReadOnlyList<CanDevice> GetDevices()
    {
        var ports = new SortedSet<string>(StringComparer.Ordinal);
        try { foreach (var p in SerialPort.GetPortNames()) ports.Add(p); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        foreach (var p in (Environment.GetEnvironmentVariable("MDFSCOPE_SLCAN_PORTS") ?? string.Empty)
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            ports.Add(p);
        return ports.Select(p => new CanDevice(p, p, SupportsFd: false)).ToList();
    }

    public ICanChannel Open(CanDevice device, CanChannelOptions options)
    {
        if (options.DataBitrate != null) throw new CanAdapterException("SLCAN CAN FD is not supported yet.");
        char code = options.Bitrate switch
        {
            10_000 => '0', 20_000 => '1', 50_000 => '2', 100_000 => '3', 125_000 => '4',
            250_000 => '5', 500_000 => '6', 800_000 => '7', 1_000_000 => '8',
            _ => throw new CanAdapterException($"SLCAN has no setting for {options.Bitrate} bit/s."),
        };
        return new SlcanChannel(device, code, options.ListenOnly);
    }
}

sealed class SlcanChannel : ICanChannel
{
    readonly SerialPort port;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Thread reader;
    readonly Channel<CanFrame> frames = Channel.CreateBounded<CanFrame>(new BoundedChannelOptions(1 << 16)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true,
    });
    readonly object writeGate = new();
    volatile bool closed;
    volatile string state = "OK";

    public SlcanChannel(CanDevice device, char bitrateCode, bool listenOnly)
    {
        Device = device;
        port = new SerialPort(device.Id, 115200) { ReadTimeout = 200, WriteTimeout = 500, NewLine = "\r", Encoding = Encoding.ASCII };
        try
        {
            port.Open();
            // A clean state: close whatever channel a previous session left open, then configure and open.
            WriteRaw("\r\r\rC\r");
            Thread.Sleep(50);
            port.DiscardInBuffer();
            Command($"S{bitrateCode}");
            Command(listenOnly ? "L" : "O");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            port.Dispose();
            throw new CanAdapterException($"{device.Id}: {e.Message}", e);
        }
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "SLCAN reader", Priority = ThreadPriority.AboveNormal };
        reader.Start();
    }

    public CanDevice Device { get; }
    public string BusState => closed ? "closed" : state;
    public bool CanSend => true;

    void WriteRaw(string text)
    {
        lock (writeGate) port.Write(text);
    }

    /// <summary>Sends a command and waits for the CR acknowledgement (BEL means refused).</summary>
    void Command(string text)
    {
        WriteRaw(text + "\r");
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 1000)
        {
            int b;
            try { b = port.ReadByte(); }
            catch (TimeoutException) { continue; }
            if (b == '\r') return;
            if (b == 7) throw new CanAdapterException($"The adapter refused \"{text}\" (is it an SLCAN device?).");
        }
        throw new CanAdapterException($"No answer to \"{text}\"; the port is probably not an SLCAN adapter.");
    }

    void ReadLoop()
    {
        var line = new StringBuilder(64);
        var buffer = new byte[4096];
        while (!closed)
        {
            int n;
            try { n = port.Read(buffer, 0, buffer.Length); }
            catch (TimeoutException) { continue; }
            catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException)
            {
                if (!closed) state = "port lost: " + e.Message;
                break;
            }
            ulong now = (ulong)(clock.Elapsed.Ticks * 100);
            for (int i = 0; i < n; i++)
            {
                byte b = buffer[i];
                if (b == '\r')
                {
                    if (line.Length > 0 && Parse(line.ToString(), now, out var frame)) frames.Writer.TryWrite(frame);
                    line.Clear();
                }
                else if (b == 7) { state = "adapter reported an error"; line.Clear(); }
                else if (line.Length < 64) line.Append((char)b);
            }
        }
        frames.Writer.TryComplete();
    }

    /// <summary>Parses tIIIL[DD..], TIIIIIIIIL[DD..], rIIIL, RIIIIIIIIL; a trailing 4-digit time stamp is ignored.</summary>
    internal static bool Parse(string s, ulong timestampNs, out CanFrame frame)
    {
        frame = default;
        if (s.Length < 5) return false;
        char kind = s[0];
        bool ext = kind is 'T' or 'R';
        bool remote = kind is 'r' or 'R';
        if (kind is not ('t' or 'T' or 'r' or 'R')) return false;
        int idLen = ext ? 8 : 3;
        if (s.Length < 1 + idLen + 1) return false;
        if (!uint.TryParse(s.AsSpan(1, idLen), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id)) return false;
        int dlc = s[1 + idLen] - '0';
        if (dlc is < 0 or > 8) return false;
        byte[] data = [];
        if (!remote)
        {
            int at = 2 + idLen;
            if (s.Length < at + dlc * 2) return false;
            data = new byte[dlc];
            for (int i = 0; i < dlc; i++)
                if (!byte.TryParse(s.AsSpan(at + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out data[i])) return false;
        }
        frame = new CanFrame(timestampNs, id & (ext ? 0x1FFFFFFFu : 0x7FFu), ext, remote ? CanFrameKind.Remote : CanFrameKind.Data, data, (byte)dlc);
        return true;
    }

    public async IAsyncEnumerable<CanFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await frames.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            while (frames.Reader.TryRead(out var frame))
                yield return frame;
    }

    public void Send(CanFrame frame)
    {
        if (frame.IsFd || frame.Data.Length > 8 || frame.Kind == CanFrameKind.Error)
            throw new NotSupportedException("SLCAN sends classic data and remote frames only.");
        var sb = new StringBuilder();
        bool remote = frame.Kind == CanFrameKind.Remote;
        sb.Append(frame.IsExtended ? (remote ? 'R' : 'T') : (remote ? 'r' : 't'));
        sb.Append(frame.Id.ToString(frame.IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture));
        sb.Append(remote ? frame.Dlc : frame.Data.Length);
        if (!remote) foreach (var b in frame.Data) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        sb.Append('\r');
        WriteRaw(sb.ToString());
    }

    public void Dispose()
    {
        if (closed) return;
        closed = true;
        try { WriteRaw("C\r"); } catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException) { }
        reader.Join(500);
        port.Dispose();
    }
}
