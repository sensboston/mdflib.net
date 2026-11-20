using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace MdfScope.Can.SocketCan;

/// <summary>
/// Linux SocketCAN: every CAN network interface (can0, vcan0, slcan0, PEAK/Kvaser/Ixxat/gs_usb through their
/// kernel drivers). The bit rate and listen-only mode belong to the interface and are set with
/// <c>ip link set can0 type can bitrate 500000 [listen-only on]</c>; this plugin only opens it.
/// </summary>
public sealed class SocketCanProvider : ICanAdapterProvider
{
    public string Name => "SocketCAN";

    public bool IsAvailable => OperatingSystem.IsLinux();

    public string? UnavailableReason => IsAvailable ? null : "SocketCAN exists on Linux only.";

    public IReadOnlyList<CanDevice> GetDevices()
    {
        var list = new List<CanDevice>();
        if (!IsAvailable || !Directory.Exists("/sys/class/net")) return list;
        foreach (var dir in Directory.GetDirectories("/sys/class/net").OrderBy(d => d, StringComparer.Ordinal))
        {
            // ARPHRD_CAN = 280
            if (ReadSys(dir, "type") != "280") continue;
            string name = Path.GetFileName(dir);
            string state = ReadSys(dir, "operstate") ?? "?";
            bool fd = int.TryParse(ReadSys(dir, "mtu"), out int mtu) && mtu >= 72;
            list.Add(new CanDevice(name, $"{name} ({state})", fd));
        }
        return list;
    }

    static string? ReadSys(string dir, string file)
    {
        try { return File.ReadAllText(Path.Combine(dir, file)).Trim(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public ICanChannel Open(CanDevice device, CanChannelOptions options) => new SocketCanChannel(device);
}

sealed class SocketCanChannel : ICanChannel
{
    const int AfCan = 29, SockRaw = 3, CanRaw = 1, SolCanRaw = 101, CanRawFdFrames = 5;
    const int SolSocket = 1, SoRcvTimeo = 20;
    const uint SiocGIfIndex = 0x8933, SiocGStamp = 0x8906;
    const uint EffFlag = 0x80000000, RtrFlag = 0x40000000, ErrFlag = 0x20000000;
    const byte CanFdBrs = 0x01;

    readonly int fd;
    readonly Thread reader;
    readonly Channel<CanFrame> frames = Channel.CreateBounded<CanFrame>(new BoundedChannelOptions(1 << 16)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true,
    });
    volatile bool closed;
    volatile string state = "OK";

    public SocketCanChannel(CanDevice device)
    {
        Device = device;
        fd = socket(AfCan, SockRaw, CanRaw);
        if (fd < 0) throw new CanAdapterException($"Cannot create a CAN socket (errno {Marshal.GetLastPInvokeError()}).");
        try
        {
            var ifr = new byte[40];
            var nameBytes = System.Text.Encoding.ASCII.GetBytes(device.Id);
            if (nameBytes.Length > 15) throw new CanAdapterException($"Interface name {device.Id} is too long.");
            nameBytes.CopyTo(ifr, 0);
            if (ioctl(fd, SiocGIfIndex, ifr) < 0) throw new CanAdapterException($"No CAN interface {device.Id}.");
            int index = BinaryPrimitives.ReadInt32LittleEndian(ifr.AsSpan(16));

            int on = 1;
            setsockopt(fd, SolCanRaw, CanRawFdFrames, ref on, 4); // accept CAN FD frames too, where the interface has them
            var timeout = new long[] { 0, 100_000 };              // 100 ms, so the reader notices Dispose
            setsockopt(fd, SolSocket, SoRcvTimeo, timeout, 16);

            var addr = new byte[24];
            BinaryPrimitives.WriteUInt16LittleEndian(addr, AfCan);
            BinaryPrimitives.WriteInt32LittleEndian(addr.AsSpan(4), index);
            if (bind(fd, addr, addr.Length) < 0)
                throw new CanAdapterException($"Cannot bind to {device.Id} (errno {Marshal.GetLastPInvokeError()}); is the interface up?");
        }
        catch
        {
            close(fd);
            throw;
        }
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "SocketCAN reader", Priority = ThreadPriority.AboveNormal };
        reader.Start();
    }

    public CanDevice Device { get; }
    public string BusState => closed ? "closed" : state;
    public bool CanSend => true;

    void ReadLoop()
    {
        var buf = new byte[72];
        var tv = new long[2];
        while (!closed)
        {
            nint n = read(fd, buf, buf.Length);
            if (n < 0) continue; // timeout (EAGAIN) or interrupted
            if (n != 16 && n != 72) continue;
            ulong ns = ioctl(fd, SiocGStamp, tv) == 0 ? (ulong)tv[0] * 1_000_000_000UL + (ulong)tv[1] * 1000UL : 0;
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            bool fdFrame = n == 72;
            int len = buf[4];
            bool ext = (raw & EffFlag) != 0;
            uint id = raw & (ext ? 0x1FFFFFFFu : 0x7FFu);
            var kind = (raw & ErrFlag) != 0 ? CanFrameKind.Error : (raw & RtrFlag) != 0 ? CanFrameKind.Remote : CanFrameKind.Data;
            if (kind == CanFrameKind.Error) state = "error frames seen";
            int dataLen = Math.Min(len, fdFrame ? 64 : 8);
            byte dlc = fdFrame ? FdDlc(dataLen) : (byte)len;
            var data = kind == CanFrameKind.Remote ? Array.Empty<byte>() : buf.AsSpan(8, dataLen).ToArray();
            frames.Writer.TryWrite(new CanFrame(ns, id, ext, kind, data, dlc, fdFrame, fdFrame && (buf[5] & CanFdBrs) != 0));
        }
        frames.Writer.TryComplete();
    }

    static byte FdDlc(int len) => len switch
    {
        <= 8 => (byte)len, <= 12 => 9, <= 16 => 10, <= 20 => 11, <= 24 => 12, <= 32 => 13, <= 48 => 14, _ => 15,
    };

    public async IAsyncEnumerable<CanFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await frames.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            while (frames.Reader.TryRead(out var frame))
                yield return frame;
    }

    public void Send(CanFrame frame)
    {
        bool fdFrame = frame.IsFd || frame.Data.Length > 8;
        var buf = new byte[fdFrame ? 72 : 16];
        uint raw = frame.Id | (frame.IsExtended ? EffFlag : 0) | (frame.Kind == CanFrameKind.Remote ? RtrFlag : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buf, raw);
        buf[4] = (byte)(frame.Kind == CanFrameKind.Remote ? frame.Dlc : frame.Data.Length);
        if (fdFrame && frame.BitRateSwitch) buf[5] = CanFdBrs;
        frame.Data.CopyTo(buf, 8);
        if (write(fd, buf, buf.Length) != buf.Length)
            throw new CanAdapterException($"Send failed (errno {Marshal.GetLastPInvokeError()}).");
    }

    public void Dispose()
    {
        if (closed) return;
        closed = true;
        reader.Join(500);
        close(fd);
    }

    [DllImport("libc", SetLastError = true)] static extern int socket(int domain, int type, int protocol);
    [DllImport("libc", SetLastError = true)] static extern int bind(int fd, byte[] addr, int len);
    [DllImport("libc", SetLastError = true)] static extern int ioctl(int fd, uint request, byte[] arg);
    [DllImport("libc", SetLastError = true)] static extern int ioctl(int fd, uint request, long[] arg);
    [DllImport("libc", SetLastError = true)] static extern int setsockopt(int fd, int level, int name, ref int value, int len);
    [DllImport("libc", SetLastError = true)] static extern int setsockopt(int fd, int level, int name, long[] value, int len);
    [DllImport("libc", SetLastError = true)] static extern nint read(int fd, byte[] buf, nint count);
    [DllImport("libc", SetLastError = true)] static extern nint write(int fd, byte[] buf, nint count);
    [DllImport("libc", SetLastError = true)] static extern int close(int fd);
}
