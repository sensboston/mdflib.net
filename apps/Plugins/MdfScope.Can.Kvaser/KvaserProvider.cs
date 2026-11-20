using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace MdfScope.Can.Kvaser;

/// <summary>
/// Kvaser interfaces through CANlib (canlib32.dll on Windows, libcanlib.so on Linux), installed with the Kvaser
/// drivers. Written from the CANlib documentation and NOT tested on hardware: reports are welcome.
/// </summary>
public sealed class KvaserProvider : ICanAdapterProvider
{
    public string Name => "Kvaser CANlib (experimental)";

    public bool IsAvailable => Native.Load();

    public string? UnavailableReason => IsAvailable ? null : "Kvaser CANlib is not installed (Kvaser drivers / linuxcan).";

    public IReadOnlyList<CanDevice> GetDevices()
    {
        var list = new List<CanDevice>();
        if (!IsAvailable || Native.canGetNumberOfChannels(out int count) != 0) return list;
        for (int ch = 0; ch < count; ch++)
        {
            var name = new byte[256];
            Native.canGetChannelData(ch, Native.ChannelDataDevDescrAscii, name, name.Length);
            string text = Encoding.ASCII.GetString(name).TrimEnd('\0');
            uint caps = 0;
            var capBuf = new byte[4];
            if (Native.canGetChannelData(ch, Native.ChannelDataChannelCap, capBuf, 4) == 0) caps = BitConverter.ToUInt32(capBuf);
            list.Add(new CanDevice(ch.ToString(), $"{(text.Length > 0 ? text : "Kvaser")} (channel {ch})", (caps & Native.CapCanFd) != 0));
        }
        return list;
    }

    public ICanChannel Open(CanDevice device, CanChannelOptions options)
    {
        if (options.DataBitrate != null) throw new CanAdapterException("CAN FD is not supported by this plugin yet.");
        if (!int.TryParse(device.Id, out int ch)) throw new CanAdapterException($"Unknown Kvaser channel {device.Id}.");
        int freq = options.Bitrate switch
        {
            1_000_000 => -1, 500_000 => -2, 250_000 => -3, 125_000 => -4, 100_000 => -5, 62_500 => -6, 50_000 => -7,
            83_333 => -8, 10_000 => -9,
            _ => throw new CanAdapterException($"No predefined Kvaser bit rate for {options.Bitrate} bit/s."),
        };
        int handle = Native.canOpenChannel(ch, Native.OpenAcceptVirtual);
        if (handle < 0) throw new CanAdapterException($"canOpenChannel failed ({Native.Error(handle)}).");
        try
        {
            Check(Native.canSetBusParams(handle, freq, 0, 0, 0, 0, 0), "canSetBusParams");
            Check(Native.canSetBusOutputControl(handle, options.ListenOnly ? Native.DriverSilent : Native.DriverNormal), "canSetBusOutputControl");
            int scale = 1; // time stamps in microseconds
            Native.canIoCtl(handle, Native.IoctlSetTimerScale, ref scale, 4);
            Check(Native.canBusOn(handle), "canBusOn");
        }
        catch
        {
            Native.canClose(handle);
            throw;
        }
        return new KvaserChannel(device, handle);
    }

    static void Check(int status, string call)
    {
        if (status < 0) throw new CanAdapterException($"{call} failed ({Native.Error(status)}).");
    }
}

sealed class KvaserChannel : ICanChannel
{
    readonly int handle;
    readonly Thread reader;
    readonly Channel<CanFrame> frames = Channel.CreateBounded<CanFrame>(new BoundedChannelOptions(1 << 16)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true,
    });
    volatile bool closed;
    ulong wraps;
    uint lastTime;

    public KvaserChannel(CanDevice device, int handle)
    {
        Device = device;
        this.handle = handle;
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "Kvaser reader", Priority = ThreadPriority.AboveNormal };
        reader.Start();
    }

    public CanDevice Device { get; }
    public bool CanSend => true;

    public string BusState
    {
        get
        {
            if (closed) return "closed";
            if (Native.canReadStatus(handle, out uint flags) != 0) return "?";
            return (flags & Native.StatBusOff) != 0 ? "bus off"
                : (flags & Native.StatErrorPassive) != 0 ? "error passive"
                : (flags & Native.StatErrorWarning) != 0 ? "error warning" : "OK";
        }
    }

    void ReadLoop()
    {
        var data = new byte[64];
        while (!closed)
        {
            int status = Native.canReadWait(handle, out int id, data, out int dlc, out uint flag, out uint time, 100);
            if (status == Native.ErrNoMsg) continue;
            if (status < 0) { Thread.Sleep(10); continue; }
            if (time < lastTime) wraps++;
            lastTime = time;
            ulong micros = (wraps << 32) + time;
            var kind = (flag & Native.MsgErrorFrame) != 0 ? CanFrameKind.Error
                : (flag & Native.MsgRtr) != 0 ? CanFrameKind.Remote : CanFrameKind.Data;
            int len = Math.Clamp(dlc, 0, 8);
            var payload = kind == CanFrameKind.Data ? data.AsSpan(0, len).ToArray() : [];
            frames.Writer.TryWrite(new CanFrame(micros * 1000, (uint)id, (flag & Native.MsgExt) != 0, kind, payload, (byte)Math.Clamp(dlc, 0, 15)));
        }
        frames.Writer.TryComplete();
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
            throw new NotSupportedException("Only classic data and remote frames can be sent.");
        uint flag = (frame.IsExtended ? Native.MsgExt : Native.MsgStd) | (frame.Kind == CanFrameKind.Remote ? Native.MsgRtr : 0);
        var data = new byte[8];
        frame.Data.CopyTo(data, 0);
        int status = Native.canWrite(handle, (int)frame.Id, data, frame.Kind == CanFrameKind.Remote ? frame.Dlc : frame.Data.Length, flag);
        if (status < 0) throw new CanAdapterException($"canWrite failed ({Native.Error(status)}).");
    }

    public void Dispose()
    {
        if (closed) return;
        closed = true;
        reader.Join(500);
        Native.canBusOff(handle);
        Native.canClose(handle);
    }
}

/// <summary>The part of the CANlib C API used here (see the Kvaser CANlib SDK documentation).</summary>
static class Native
{
    const string Library = "canlib";

    public const int OpenAcceptVirtual = 0x0020;
    public const int ChannelDataChannelCap = 1, ChannelDataDevDescrAscii = 26;
    public const uint CapCanFd = 0x00080000;
    public const int DriverSilent = 1, DriverNormal = 4;
    public const int IoctlSetTimerScale = 6;
    public const int ErrNoMsg = -2;
    public const uint MsgRtr = 0x0001, MsgStd = 0x0002, MsgExt = 0x0004, MsgErrorFrame = 0x0020;
    public const uint StatErrorPassive = 0x01, StatBusOff = 0x02, StatErrorWarning = 0x04;

    static int loaded;

    static Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, assembly, path) =>
        {
            if (name != Library) return IntPtr.Zero;
            foreach (var candidate in new[] { "canlib32.dll", "libcanlib.so", "libcanlib.so.1" })
                if (NativeLibrary.TryLoad(candidate, assembly, path, out var h)) return h;
            return IntPtr.Zero;
        });
    }

    public static bool Load()
    {
        if (loaded == 0)
        {
            try { canInitializeLibrary(); loaded = 1; }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { loaded = -1; }
        }
        return loaded > 0;
    }

    public static string Error(int status)
    {
        var text = new byte[128];
        return canGetErrorText(status, text, text.Length) == 0 ? $"{Encoding.ASCII.GetString(text).TrimEnd('\0')} ({status})" : $"error {status}";
    }

    [DllImport(Library)] public static extern void canInitializeLibrary();
    [DllImport(Library)] public static extern int canGetNumberOfChannels(out int count);
    [DllImport(Library)] public static extern int canGetChannelData(int channel, int item, byte[] buffer, int size);
    [DllImport(Library)] public static extern int canOpenChannel(int channel, int flags);
    [DllImport(Library)] public static extern int canSetBusParams(int handle, int freq, uint tseg1, uint tseg2, uint sjw, uint noSamp, uint syncmode);
    [DllImport(Library)] public static extern int canSetBusOutputControl(int handle, int drivertype);
    [DllImport(Library)] public static extern int canIoCtl(int handle, int func, ref int buf, int buflen);
    [DllImport(Library)] public static extern int canBusOn(int handle);
    [DllImport(Library)] public static extern int canBusOff(int handle);
    [DllImport(Library)] public static extern int canClose(int handle);
    [DllImport(Library)] public static extern int canReadWait(int handle, out int id, byte[] msg, out int dlc, out uint flag, out uint time, uint timeout);
    [DllImport(Library)] public static extern int canWrite(int handle, int id, byte[] msg, int dlc, uint flag);
    [DllImport(Library)] public static extern int canReadStatus(int handle, out uint flags);
    [DllImport(Library)] public static extern int canGetErrorText(int err, byte[] buf, int bufsiz);
}
