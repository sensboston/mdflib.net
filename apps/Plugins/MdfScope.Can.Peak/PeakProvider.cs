using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace MdfScope.Can.Peak;

/// <summary>PEAK-System PCAN USB interfaces through the PCAN-Basic library installed with the PEAK driver.</summary>
public sealed class PeakProvider : ICanAdapterProvider
{
    public string Name => "PEAK PCAN";

    public bool IsAvailable => Native.Load();

    public string? UnavailableReason => IsAvailable ? null :
        "PCAN-Basic is not installed (Windows: the PEAK driver package; Linux: libpcanbasic).";

    public IReadOnlyList<CanDevice> GetDevices()
    {
        var list = new List<CanDevice>();
        if (!IsAvailable) return list;
        for (int i = 0; i < 16; i++)
        {
            ushort handle = UsbHandle(i);
            uint condition = 0;
            if (Native.CAN_GetValue(handle, Native.ChannelCondition, ref condition, 4) != 0) continue;
            if ((condition & (Native.ChannelAvailable | Native.ChannelOccupied)) == 0) continue;
            uint features = 0;
            Native.CAN_GetValue(handle, Native.ChannelFeatures, ref features, 4);
            list.Add(new CanDevice($"usb:{i + 1}", $"PCAN-USB {i + 1}",
                SupportsFd: (features & Native.FeatureFd) != 0,
                IsInUse: (condition & Native.ChannelOccupied) != 0));
        }
        return list;
    }

    public ICanChannel Open(CanDevice device, CanChannelOptions options)
    {
        if (!device.Id.StartsWith("usb:", StringComparison.Ordinal) || !int.TryParse(device.Id.AsSpan(4), out int n) || n < 1 || n > 16)
            throw new CanAdapterException($"Unknown PCAN device {device.Id}.");
        if (options.DataBitrate != null)
            throw new CanAdapterException("CAN FD is not supported by this plugin yet.");
        if (!Btr0Btr1.TryGetValue(options.Bitrate, out ushort btr))
            throw new CanAdapterException($"PCAN classic CAN does not offer {options.Bitrate} bit/s.");

        ushort handle = UsbHandle(n - 1);
        uint listen = options.ListenOnly ? 1u : 0u;
        Native.CAN_SetValue(handle, Native.ListenOnly, ref listen, 4);
        uint status = Native.CAN_Initialize(handle, btr, 0, 0, 0);
        if (status != 0)
            throw new CanAdapterException($"{device.DisplayName}: {Native.Describe(status)}");
        return new PeakChannel(device, handle);
    }

    /// <summary>The classic CAN bit rates PCAN-Basic has register values for.</summary>
    public static IReadOnlyList<int> Bitrates => Btr0Btr1.Keys.OrderByDescending(k => k).ToList();

    static readonly Dictionary<int, ushort> Btr0Btr1 = new()
    {
        [1_000_000] = 0x0014, [800_000] = 0x0016, [500_000] = 0x001C, [250_000] = 0x011C, [125_000] = 0x031C,
        [100_000] = 0x432F, [95_000] = 0xC34E, [83_333] = 0x852B, [50_000] = 0x472F, [47_619] = 0x1414,
        [33_333] = 0x8B2F, [20_000] = 0x532F, [10_000] = 0x672F, [5_000] = 0x7F7F,
    };

    // PCAN_USBBUS1..8 are 0x51..0x58, PCAN_USBBUS9..16 are 0x509..0x510.
    static ushort UsbHandle(int index) => (ushort)(index < 8 ? 0x51 + index : 0x509 + (index - 8));
}

sealed class PeakChannel : ICanChannel
{
    readonly ushort handle;
    readonly AutoResetEvent received = new(false);
    readonly Channel<CanFrame> frames = Channel.CreateBounded<CanFrame>(new BoundedChannelOptions(1 << 16)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true,
    });
    readonly Thread reader;
    volatile bool closed;

    public PeakChannel(CanDevice device, ushort handle)
    {
        Device = device;
        this.handle = handle;
        uint eventHandle = (uint)received.SafeWaitHandle.DangerousGetHandle().ToInt64();
        bool events = Native.CAN_SetValue(handle, Native.ReceiveEvent, ref eventHandle, 4) == 0;
        reader = new Thread(() => ReadLoop(events)) { IsBackground = true, Name = "PCAN reader", Priority = ThreadPriority.AboveNormal };
        reader.Start();
    }

    public CanDevice Device { get; }

    public bool CanSend => true;

    public string BusState
    {
        get
        {
            if (closed) return "closed";
            uint s = Native.CAN_GetStatus(handle);
            return (s & Native.ErrorBusOff) != 0 ? "bus off"
                : (s & Native.ErrorBusPassive) != 0 ? "error passive"
                : (s & Native.ErrorBusWarning) != 0 ? "error warning"
                : "OK";
        }
    }

    void ReadLoop(bool events)
    {
        var raw = new Native.Message { Data = new byte[8] };
        while (!closed)
        {
            uint status = Native.CAN_Read(handle, out raw, out var ts);
            if (status == Native.ErrorQueueEmpty)
            {
                if (events) received.WaitOne(50); else Thread.Sleep(1);
                continue;
            }
            if (status != 0 && (status & Native.ErrorQueueOverrun) == 0) continue;
            if ((raw.Type & Native.MessageStatus) != 0) continue;

            ulong micros = ts.Micros + 1000UL * ts.Millis + 0x1_0000_0000UL * 1000UL * ts.MillisOverflow;
            var kind = (raw.Type & Native.MessageErrorFrame) != 0 ? CanFrameKind.Error
                : (raw.Type & Native.MessageRtr) != 0 ? CanFrameKind.Remote : CanFrameKind.Data;
            int len = Math.Min((int)raw.Length, 8);
            var data = kind == CanFrameKind.Data ? raw.Data.AsSpan(0, len).ToArray() : [];
            frames.Writer.TryWrite(new CanFrame(micros * 1000, raw.Id, (raw.Type & Native.MessageExtended) != 0,
                kind, data, raw.Length));
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
        if (frame.Kind == CanFrameKind.Error || frame.IsFd || frame.Data.Length > 8)
            throw new NotSupportedException("Only classic data and remote frames can be sent.");
        var msg = new Native.Message
        {
            Id = frame.Id,
            Type = (byte)((frame.IsExtended ? Native.MessageExtended : 0) | (frame.Kind == CanFrameKind.Remote ? Native.MessageRtr : 0)),
            Length = frame.Kind == CanFrameKind.Remote ? frame.Dlc : (byte)frame.Data.Length,
            Data = new byte[8],
        };
        frame.Data.CopyTo(msg.Data, 0);
        uint status = Native.CAN_Write(handle, ref msg);
        if (status != 0) throw new CanAdapterException(Native.Describe(status));
    }

    public void Dispose()
    {
        if (closed) return;
        closed = true;
        received.Set();
        reader.Join(500);
        Native.CAN_Uninitialize(handle);
        received.Dispose();
    }
}

/// <summary>The part of the PCAN-Basic C API this plugin uses (see the PCAN-Basic documentation).</summary>
static class Native
{
    const string Library = "PCANBasic";

    public const byte ReceiveEvent = 0x03, ListenOnly = 0x08, ChannelCondition = 0x0D, ChannelFeatures = 0x16;
    public const uint ChannelAvailable = 0x01, ChannelOccupied = 0x02, FeatureFd = 0x01;
    public const uint ErrorQueueEmpty = 0x20, ErrorQueueOverrun = 0x40;
    public const uint ErrorBusWarning = 0x04, ErrorBusPassive = 0x08, ErrorBusOff = 0x10;
    public const byte MessageRtr = 0x01, MessageExtended = 0x02, MessageErrorFrame = 0x40, MessageStatus = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    public struct Message
    {
        public uint Id;
        public byte Type;
        public byte Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Timestamp
    {
        public uint Millis;
        public ushort MillisOverflow;
        public ushort Micros;
    }

    static int loaded; // 0 unknown, 1 yes, -1 no

    static Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, assembly, path) =>
        {
            if (name != Library) return IntPtr.Zero;
            foreach (var candidate in new[] { "PCANBasic.dll", "libpcanbasic.so", "libpcanbasic.so.4", "libPCBUSB.dylib" })
                if (NativeLibrary.TryLoad(candidate, assembly, path, out var h)) return h;
            return IntPtr.Zero;
        });
    }

    public static bool Load()
    {
        if (loaded == 0)
        {
            try { CAN_GetStatus(0x51); loaded = 1; }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { loaded = -1; }
        }
        return loaded > 0;
    }

    public static string Describe(uint status)
    {
        var text = new System.Text.StringBuilder(256);
        return CAN_GetErrorText(status, 0x09, text) == 0 && text.Length > 0
            ? $"{text} (PCAN 0x{status:X})"
            : $"PCAN error 0x{status:X}";
    }

    [DllImport(Library, CharSet = CharSet.Ansi)] static extern uint CAN_GetErrorText(uint error, ushort language, System.Text.StringBuilder buffer);

    [DllImport(Library)] public static extern uint CAN_Initialize(ushort channel, ushort btr0btr1, byte hwType, uint ioPort, ushort interrupt);
    [DllImport(Library)] public static extern uint CAN_Uninitialize(ushort channel);
    [DllImport(Library)] public static extern uint CAN_GetStatus(ushort channel);
    [DllImport(Library)] public static extern uint CAN_Read(ushort channel, out Message message, out Timestamp timestamp);
    [DllImport(Library)] public static extern uint CAN_Write(ushort channel, ref Message message);
    [DllImport(Library)] public static extern uint CAN_GetValue(ushort channel, byte parameter, ref uint buffer, uint length);
    [DllImport(Library)] public static extern uint CAN_SetValue(ushort channel, byte parameter, ref uint buffer, uint length);
}
